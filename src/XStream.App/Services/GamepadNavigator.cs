using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XStream.Core.Input;
using XStream.Core.Xbox;

namespace XStream.App.Services;

/// <summary>
/// Drives the launcher with a controller (the ROG Ally X's built-in pad, or any Xbox/DualSense pad), the
/// way a console UI works:
///   D-pad / left stick  move to the nearest control in that direction (spatial, not tab order)
///   A                   press / toggle / pick; cycles a dropdown
///   Left / Right        change a slider or dropdown that has focus
///   B                   back          LB / RB   previous / next page
///   Y                   refresh       Menu      stream the selected (last used) console
/// Polls at 60 Hz on the UI thread; paused while a stream runs (the stream reads the pad itself).
/// </summary>
public sealed class GamepadNavigator : IDisposable
{
    private readonly TopLevel _top;
    private readonly DispatcherTimer _timer;
    private IGamepadBackend? _backend;
    private GamepadState[] _states = [];
    private bool[] _connected = [];
    private GamepadButtons _held;
    private GamepadButtons _repeatButton;
    private DateTime _repeatAt;

    public event Action? Back;
    public event Action<int>? SwitchPage;   // -1 = previous, +1 = next
    public event Action? Start;
    public event Action? Refresh;

    public bool Available { get; private set; }
    /// <summary>Controllers connected right now (-1: not known, i.e. not polling).</summary>
    public int Connected { get; private set; } = -1;
    /// <summary>Raised on the UI thread when <see cref="Connected"/> changes.</summary>
    public event Action? ConnectedChanged;
    public string? BackendName => _backend?.Name;

    public GamepadNavigator(TopLevel top)
    {
        _top = top;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => Tick());
        _timer.Stop(); // this constructor starts it; only poll once Enable() is called
    }

    public void Enable()
    {
        if (_backend is null)
        {
            try
            {
                _backend = GamepadBackends.CreateDefault();
                _states = new GamepadState[_backend.MaxPads];
                _connected = new bool[_backend.MaxPads];
                Available = true;
            }
            catch { Available = false; return; } // no SDL2 / XInput: keyboard and mouse still work
        }
        _held = GamepadButtons.None;
        _timer.Start();
    }

    /// <summary>Stop polling and release the controller (so the stream process has it to itself).</summary>
    public void Disable()
    {
        if (Connected != -1) { Connected = -1; ConnectedChanged?.Invoke(); }
        _timer.Stop();
        _backend?.Dispose();
        _backend = null;
    }

    private void Tick()
    {
        if (_backend is null) return;
        try { _backend.Poll(_states, _connected); }
        catch { Disable(); Available = false; return; }

        int count = 0;
        foreach (var c in _connected) if (c) count++;
        if (count != Connected) { Connected = count; ConnectedChanged?.Invoke(); }

        var now = GamepadButtons.None;
        for (int i = 0; i < _states.Length; i++)
        {
            if (!_connected[i]) continue;
            var s = _states[i];
            now |= s.Buttons;
            // Left stick acts as a D-pad (Y up = positive).
            if (s.LeftY > 0.6f) now |= GamepadButtons.DPadUp;
            if (s.LeftY < -0.6f) now |= GamepadButtons.DPadDown;
            if (s.LeftX < -0.6f) now |= GamepadButtons.DPadLeft;
            if (s.LeftX > 0.6f) now |= GamepadButtons.DPadRight;
        }
        var pressed = now & ~_held;
        _held = now;

        const GamepadButtons dirs = GamepadButtons.DPadUp | GamepadButtons.DPadDown | GamepadButtons.DPadLeft | GamepadButtons.DPadRight;
        // Held direction repeats: first after 400 ms, then every 110 ms.
        if ((pressed & dirs) != 0)
        {
            _repeatButton = Lowest(pressed & dirs);
            _repeatAt = DateTime.UtcNow.AddMilliseconds(400);
        }
        else if (_repeatButton != 0 && (now & _repeatButton) != 0 && DateTime.UtcNow >= _repeatAt)
        {
            pressed |= _repeatButton;
            _repeatAt = DateTime.UtcNow.AddMilliseconds(110);
        }
        else if ((now & _repeatButton) == 0) _repeatButton = 0;

        if (pressed != 0) Press(pressed);
    }

    /// <summary>Handle newly pressed buttons (public so it can be driven without a controller, e.g. in tests).</summary>
    public void Press(GamepadButtons pressed)
    {
        if (pressed.HasFlag(GamepadButtons.DPadUp)) Move(0, -1);
        if (pressed.HasFlag(GamepadButtons.DPadDown)) Move(0, +1);
        if (pressed.HasFlag(GamepadButtons.DPadLeft)) Move(-1, 0);
        if (pressed.HasFlag(GamepadButtons.DPadRight)) Move(+1, 0);
        if (pressed.HasFlag(GamepadButtons.A)) Activate();
        if (pressed.HasFlag(GamepadButtons.B)) { Back?.Invoke(); FocusDefaultSoon(); }
        if (pressed.HasFlag(GamepadButtons.Y)) Refresh?.Invoke();
        if (pressed.HasFlag(GamepadButtons.LeftShoulder)) { SwitchPage?.Invoke(-1); FocusDefaultSoon(); }
        if (pressed.HasFlag(GamepadButtons.RightShoulder)) { SwitchPage?.Invoke(+1); FocusDefaultSoon(); }
        if (pressed.HasFlag(GamepadButtons.Menu)) Start?.Invoke();
    }

    private static GamepadButtons Lowest(GamepadButtons b) => (GamepadButtons)((ushort)b & -(ushort)b);

    public Control? Focused => _top.FocusManager?.GetFocusedElement() as Control;

    // ---- Spatial navigation ---------------------------------------------------------------------

    private void Move(int dx, int dy)
    {
        var focused = Focused;
        if (focused is null || !IsCandidate(focused)) { FocusDefault(); return; }

        // Left/right on a slider or dropdown changes its value instead of moving.
        if (dx != 0 && focused is Slider slider)
        {
            double step = slider.SmallChange > 0 ? slider.SmallChange : (slider.Maximum - slider.Minimum) / 20;
            slider.Value = Math.Clamp(slider.Value + dx * step, slider.Minimum, slider.Maximum);
            return;
        }
        if (dx != 0 && focused is ComboBox combo && combo.ItemCount > 0)
        {
            combo.SelectedIndex = Math.Clamp(combo.SelectedIndex + dx, 0, combo.ItemCount - 1);
            return;
        }

        var from = RectOf(focused);
        if (from is null) { FocusDefault(); return; }
        // Stay inside the area you're in (sidebar or page). Only left/right crosses between them: left off the
        // page's edge goes to the sidebar, right from the sidebar goes to the page's main action.
        bool inSidebar = InSidebar(focused);
        Control? Best(IEnumerable<Control> pool) => pool.Where(c => c != focused)
            .Select(c => (c, score: Score(from.Value, RectOf(c)!.Value, dx, dy)))
            .Where(x => x.score < double.MaxValue)
            .OrderBy(x => x.score)
            .Select(x => x.c)
            .FirstOrDefault();
        var all = Candidates().ToList();
        var best = Best(all.Where(c => InSidebar(c) == inSidebar));
        if (best is null && dx != 0)
        {
            if (inSidebar && dx > 0) { FocusDefault(); return; }
            best = Best(all.Where(c => InSidebar(c) != inSidebar));
        }
        if (best is not null) FocusOn(best);
    }

    /// <summary>
    /// Lower is better; MaxValue = not in that direction. Prefers controls straight ahead (overlapping on
    /// the other axis), then the closest; sideways distance counts double so "down" doesn't jump columns.
    /// </summary>
    private static double Score(Rect from, Rect to, int dx, int dy)
    {
        const double eps = 2;
        double primary, overlapGap;
        if (dx != 0)
        {
            primary = dx > 0 ? to.Left - from.Right : from.Left - to.Right;
            if ((dx > 0 ? to.Center.X - from.Center.X : from.Center.X - to.Center.X) <= eps) return double.MaxValue;
            overlapGap = Math.Max(0, Math.Max(from.Top, to.Top) - Math.Min(from.Bottom, to.Bottom));
        }
        else
        {
            primary = dy > 0 ? to.Top - from.Bottom : from.Top - to.Bottom;
            if ((dy > 0 ? to.Center.Y - from.Center.Y : from.Center.Y - to.Center.Y) <= eps) return double.MaxValue;
            overlapGap = Math.Max(0, Math.Max(from.Left, to.Left) - Math.Min(from.Right, to.Right));
        }
        double centreOffset = dx != 0 ? Math.Abs(to.Center.Y - from.Center.Y) : Math.Abs(to.Center.X - from.Center.X);
        return Math.Max(0, primary) + overlapGap * 2 + centreOffset * 0.3 + (overlapGap > 0 ? 400 : 0);
    }

    private IEnumerable<Control> Candidates() =>
        (_top as Visual)!.GetVisualDescendants().OfType<Control>().Where(IsCandidate);

    private bool IsCandidate(Control c) =>
        c is Button or ToggleButton or Slider or ComboBox or ListBoxItem
        && c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled
        && c.Bounds.Width > 0 && c.Bounds.Height > 0
        && !(c is ListBoxItem && c.FindAncestorOfType<ComboBox>() is not null)
        && RectOf(c) is not null;

    private static bool InSidebar(Control c) =>
        c.GetVisualAncestors().OfType<Border>().Any(b => b.Classes.Contains("sidebar"));

    private Rect? RectOf(Control c)
    {
        var p = c.TranslatePoint(default, (Visual)_top);
        return p is { } pt ? new Rect(pt, c.Bounds.Size) : null;
    }

    private static void FocusOn(Control c)
    {
        c.Focus(NavigationMethod.Directional);
        c.BringIntoView();
    }

    /// <summary>Focus the page's main action: its first primary button, else the top-left control of the page.</summary>
    public void FocusDefault()
    {
        var all = Candidates().ToList();
        if (all.Count == 0) return;
        var content = all.Where(c => !InSidebar(c)).ToList(); // the page, not the sidebar
        var primary = content.FirstOrDefault(c => c is Button b && b.Classes.Contains("primary"));
        var pick = primary ?? content.OrderBy(c => RectOf(c)!.Value.Top).ThenBy(c => RectOf(c)!.Value.Left).FirstOrDefault() ?? all[0];
        FocusOn(pick);
    }

    /// <summary>
    /// After a page change, focus once the new page is in place: now (if it's already laid out) and again
    /// after the page fade, in case focus was left on the old page or nowhere.
    /// </summary>
    public void FocusDefaultSoon()
    {
        Dispatcher.UIThread.Post(FocusDefault, DispatcherPriority.Loaded);
        DispatcherTimer.RunOnce(() =>
        {
            if (Focused is not { } f || !IsCandidate(f) || InSidebar(f)) FocusDefault();
        }, TimeSpan.FromMilliseconds(260));
    }

    private void Activate()
    {
        switch (Focused)
        {
            case RadioButton r: r.IsChecked = true; break;
            case ToggleButton t: t.IsChecked = !(t.IsChecked ?? false); break;
            case ComboBox cb when cb.ItemCount > 0: cb.SelectedIndex = (cb.SelectedIndex + 1) % cb.ItemCount; break;
            case Button b when b.IsEffectivelyEnabled:
                if (b.Command is { } cmd) { if (cmd.CanExecute(b.CommandParameter)) cmd.Execute(b.CommandParameter); }
                else b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                FocusDefaultSoonIfLost();
                break;
            case ListBoxItem li: li.IsSelected = true; FocusDefaultSoon(); break;
            case null: FocusDefault(); break;
        }
    }

    /// <summary>If pressing a button hid it (Stream -> Connected, Close), don't leave focus nowhere.</summary>
    private void FocusDefaultSoonIfLost() => Dispatcher.UIThread.Post(() =>
    {
        if (Focused is not { } f || !IsCandidate(f)) FocusDefault();
    }, DispatcherPriority.Loaded);

    public void Dispose() => Disable();
}
