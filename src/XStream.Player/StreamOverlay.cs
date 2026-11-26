using SkiaSharp;
using XStream.Core.Config;
using XStream.Core.Stability;
using XStream.Core.Video;
using XStream.Core.Diagnostics;
using XStream.Core.Xbox;

namespace XStream.Player;

/// <summary>
/// The in-stream menu and stats readout, drawn over the video.
///
/// Open the menu by holding View + Menu on the controller for half a second, tapping the screen (then
/// "Stream menu"), or Esc. While it's open the console gets no controller input. D-pad up/down moves,
/// left/right changes a value, A changes / activates, B resumes. F11 toggles full screen, F3 the stats readout.
///
/// Settings changed here are reported through <see cref="SettingsChanged"/> (the session saves and applies
/// them live); <see cref="Sync"/> keeps the menu in step with settings changed elsewhere (the launcher).
/// Input arrives on the controller thread and the window's main thread; window changes are queued and
/// applied on the main thread by <see cref="Apply"/>.
/// </summary>
public sealed class StreamOverlay : IVideoOverlay
{
    public const int CanvasWidth = 1920, CanvasHeight = 1080;
    private const double ChordHoldMs = 500;
    private const double BarShowMs = 4000;

    private enum Item
    {
        Resume, XboxButton, Type,
        Profile, MaxBitrate, Tips,
        Picture, Clarity, Enhance, Fullscreen, Stats,
        Volume, Microphone, Party, Rumble, TriggerRumble, MouseSpeed,
        Screenshot, SaveClip, MoreSettings, Disconnect, TurnOff,
    }

    // Built per stream: typing and Turn off need a console (not cloud), Save clip needs instant replay on.
    private readonly (Item item, string? section)[] Layout;

    private static (Item item, string? section)[] BuildLayout(bool console, bool clips, bool mouse)
    {
        var l = new List<(Item, string?)> { (Item.Resume, null), (Item.XboxButton, null) };
        if (console) l.Add((Item.Type, null));
        l.AddRange([
            (Item.Profile, "STREAM"), (Item.MaxBitrate, null), (Item.Tips, null),
            (Item.Picture, "PICTURE"), (Item.Clarity, null), (Item.Enhance, null), (Item.Fullscreen, null), (Item.Stats, null),
            (Item.Volume, "SOUND & CONTROLLER"), (Item.Microphone, null), (Item.Party, null), (Item.Rumble, null), (Item.TriggerRumble, null)]);
        if (mouse) l.Add((Item.MouseSpeed, null)); // keyboard and mouse can play: tune the mouse while playing
        l.Add((Item.Screenshot, ""));
        if (clips) l.Add((Item.SaveClip, null));
        l.AddRange([(Item.MoreSettings, null), (Item.Disconnect, null)]);
        if (console) l.Add((Item.TurnOff, null));
        return l.ToArray();
    }
    private static readonly double[] BitrateSteps = [10, 15, 20, 25, 30, 40, 50, 60];
    private static readonly int[] RumbleSteps = [0, 25, 50, 75, 100];
    private static readonly double[] MouseSteps = [0.1, 0.15, 0.2, 0.25, 0.3, 0.35, 0.4, 0.5, 0.6, 0.7, 0.85, 1, 1.25, 1.5, 2, 2.5, 3, 4];
    private static readonly string[] ClarityNames = ["Off", "Low", "Medium", "High"];

    private readonly object _gate = new();
    private readonly string _console;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private volatile bool _dirty = true;

    private AppSettings _s;
    private bool _menu, _fullscreen, _away;
    private int _selected;
    private LiveStats? _stats;
    private (int w, int h) _videoSize;
    private bool? _pendingFullscreen;
    private bool _pendingRaise;
    private GamepadButtons _lastButtons;
    private double _chordSinceMs = double.NaN;
    private bool _chordFired;
    private double _barUntilMs;
    private string? _toast;
    private string? _reconnecting;       // non-null: the connection dropped and we're getting it back
    private SKRect _reconnectDisconnectRect;
    private double _toastUntilMs;

    private readonly SKRect[] _itemRects;
    private SKRect _panelRect, _barMenuRect, _barDisconnectRect, _awayRect;

    public event Action? DisconnectRequested;
    /// <summary>A setting changed in the menu. The session saves it to settings.json and applies it.</summary>
    public event Action<Func<AppSettings, AppSettings>>? SettingsChanged;
    /// <summary>"More settings…": bring the launcher's Settings page forward.</summary>
    public event Action? MoreSettingsRequested;
    /// <summary>"Xbox button": press the console's Xbox (Home) button (View + Menu opens this menu instead).</summary>
    public event Action? XboxButtonRequested;
    /// <summary>"Take screenshot" (or F12): the session captures the next picture and saves it.</summary>
    public event Action? ScreenshotRequested;
    /// <summary>"Save last 30 s" (or F10): the session writes the instant-replay buffer to a video file.</summary>
    public event Action? SaveClipRequested;
    /// <summary>"Party &amp; Discord chat…": show the console's guide (its Parties &amp; chats) and how to bring Discord voice in.</summary>
    public event Action? PartyRequested;
    /// <summary>Real keyboard in use (keys go to the console): Esc is then the game's, held for this menu.</summary>
    public Func<bool>? EscGoesToGame { get; set; }
    /// <summary>Esc tapped while it's the game's: send it to the console.</summary>
    public event Action? EscTapped;
    public const double EscHoldMs = 500;
    private double _escDownAt = -1;
    private long _escPress;

    /// <summary>F9 down (true) / up (false): toggles mute, or push to talk.</summary>
    public event Action<bool>? MicKey;
    private bool _f9Down;

    /// <summary>Key released (from the window).</summary>
    public void OnKeyUp(int sym)
    {
        if (sym == 27)
        {
            bool tap;
            lock (_gate) { tap = _escDownAt >= 0 && Now - _escDownAt < EscHoldMs; _escDownAt = -1; }
            if (tap) EscTapped?.Invoke();
            return;
        }
        if (sym != 0x40000042) return; // F9
        lock (_gate) { if (!_f9Down) return; _f9Down = false; }
        MicKey?.Invoke(false);
    }
    /// <summary>"End and turn off Xbox": stop the stream and switch the console off.</summary>
    public event Action? TurnOffRequested;
    /// <summary>Typing on the console: the whole text so far (sent as it changes), and whether typing finished.</summary>
    public event Action<string, bool>? TextTyped;
    /// <summary>Typing started from the menu: show the device's touch keyboard if it has one.</summary>
    public event Action? TouchKeyboardWanted;
    /// <summary>The menu took over the controller: release whatever the console thinks is held.</summary>
    public event Action? InputCaptured;

    /// <param name="consoleControl">A console stream with remote control (typing, Turn off) available.</param>
    /// <param name="clips">Instant replay is recording (Save clip in the menu).</param>
    public StreamOverlay(string consoleName, AppSettings settings, bool fullscreen, bool consoleControl = false, bool clips = false)
    {
        Layout = BuildLayout(consoleControl, clips, settings.KeyboardMouse != XStream.Core.Input.KeyboardMouseMode.Off);
        _canType = consoleControl;
        _itemRects = new SKRect[Layout.Length];
        _console = consoleName;
        _s = settings;
        _fullscreen = fullscreen;
    }

    public bool MenuOpen { get { lock (_gate) return _menu; } }
    public bool Away { get { lock (_gate) return _away; } }
    /// <summary>The menu, typing, the launcher's settings or the reconnect screen has the input (keyboard-and-mouse play pauses).</summary>
    public bool HoldsInput { get { lock (_gate) return _menu || _typing is not null || _away || _reconnecting is not null; } }
    public bool Visible { get { lock (_gate) return _typing is not null || _menu || _s.ShowStatsHud || _barUntilMs > 0 || _away || _toastUntilMs > 0 || _reconnecting is not null || MicShown; } }

    /// <summary>Show (text) or hide (null) the "Reconnecting…" screen. While it's up, B or a tap on Disconnect ends the stream.</summary>
    public void SetReconnecting(string? detail)
    {
        lock (_gate) { _reconnecting = detail; if (detail is not null) { _menu = false; _barUntilMs = 0; _toastUntilMs = 0; _toast = null; } _dirty = true; }
    }
    public bool NeedsRender => _dirty || (_barUntilMs > 0 && Now >= _barUntilMs) || (_toastUntilMs > 0 && Now >= _toastUntilMs);

    /// <summary>A short message at the top of the screen ("Screenshot saved"). Any thread.</summary>
    public void ShowToast(string text, double ms = 2500, ToastKind kind = ToastKind.Good)
    {
        lock (_gate) { _toast = text; _toastKind = kind; _toastUntilMs = Now + ms; _dirty = true; }
    }
    private ToastKind _toastKind;
    public int Width => CanvasWidth;
    public int Height => CanvasHeight;
    private double Now => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Settings changed somewhere (menu, launcher): show the new values.</summary>
    public void Sync(AppSettings s)
    {
        lock (_gate) { _s = s; _dirty = true; }
    }

    private string _route = "";
    private const int HistorySeconds = 60;
    private CheckResult? _tips;
    private bool _micOn, _micMuted, _micTalking, _micPtt;

    /// <summary>Connection tips from the stream's own numbers (shown in the menu; "Suggested settings" applies them).</summary>
    public void SetTips(CheckResult? tips) { lock (_gate) { _tips = tips; _dirty = true; } }

    /// <summary>Microphone state for the small indicator: shown while muted (or push-to-talk idle) and while you talk.</summary>
    public void SetMic(bool on, bool muted, bool talking, bool pushToTalk)
    {
        lock (_gate)
        {
            if (on == _micOn && muted == _micMuted && talking == _micTalking && pushToTalk == _micPtt) return;
            _micOn = on; _micMuted = muted; _micTalking = talking; _micPtt = pushToTalk; _dirty = true;
        }
    }
    private bool MicShown => _micOn && (_micMuted || _micTalking);
    private readonly Queue<LiveStats> _history = new();
    /// <summary>"Home network" / "Internet" / "Relayed", shown under the console name in the menu.</summary>
    public void SetRoute(string route)
    {
        string shortRoute = route.StartsWith("Home") ? "Home network" : route.StartsWith("Internet") ? "Internet" : route.StartsWith("Relayed") ? "Relayed" : "";
        lock (_gate) { _route = shortRoute; _dirty = true; }
    }

    public void UpdateStats(LiveStats s, (int w, int h) videoSize)
    {
        lock (_gate)
        {
            _stats = s;
            _videoSize = videoSize;
            _history.Enqueue(s);
            while (_history.Count > HistorySeconds) _history.Dequeue();
            if (_menu || _s.ShowStatsHud) _dirty = true;
        }
    }

    /// <summary>The launcher's Settings page is open: hold the controller (console gets nothing) until resumed.</summary>
    public void GoAway()
    {
        lock (_gate) { _away = true; _menu = false; _barUntilMs = 0; _dirty = true; }
    }

    /// <summary>Back from the launcher: give the controller back to the game and bring the video forward.</summary>
    public void ComeBack()
    {
        bool was;
        lock (_gate) { was = _away; _away = false; _pendingRaise = true; _dirty = true; }
        if (was) CameBack?.Invoke();
    }

    /// <summary>Returned from the launcher's Settings (tap on the video, or the launcher asked).</summary>
    public event Action? CameBack;

    /// <summary>Called continuously from the window loop (main thread).</summary>
    public void Apply(VideoWindow window)
    {
        // The controller only reports changes, so a steady hold produces no new reports: time it here.
        bool captured = false;
        bool? fs;
        bool raise;
        lock (_gate)
        {
            if (!_chordFired && !double.IsNaN(_chordSinceMs) && Now - _chordSinceMs >= ChordHoldMs && !_away)
            {
                _chordFired = true;
                _menu = !_menu; _selected = 0; _barUntilMs = 0; _dirty = true;
                captured = _menu;
            }
            if (_barUntilMs > 0 && Now >= _barUntilMs) { _barUntilMs = 0; _dirty = true; }
            fs = _pendingFullscreen; _pendingFullscreen = null;
            raise = _pendingRaise; _pendingRaise = false;
        }
        if (captured) InputCaptured?.Invoke();
        if (fs is { } on) window?.SetFullscreen(on);
        if (raise) window?.Raise();
    }

    // ---- Input ----------------------------------------------------------------------------------

    /// <summary>
    /// Controller state from the input thread. Returns true when the overlay owns the input (menu open, or
    /// settings open in the launcher), in which case the caller sends the console a neutral pad instead.
    /// </summary>
    public bool OnPad(ReadOnlySpan<GamepadState> pads)
    {
        var buttons = GamepadButtons.None;
        foreach (var p in pads)
        {
            buttons |= p.Buttons;
            if (p.LeftY > 0.6f) buttons |= GamepadButtons.DPadUp;
            if (p.LeftY < -0.6f) buttons |= GamepadButtons.DPadDown;
            if (p.LeftX < -0.6f) buttons |= GamepadButtons.DPadLeft;
            if (p.LeftX > 0.6f) buttons |= GamepadButtons.DPadRight;
        }
        var pressed = buttons & ~_lastButtons;
        _lastButtons = buttons;

        const GamepadButtons chord = GamepadButtons.View | GamepadButtons.Menu;
        bool reconnecting;
        lock (_gate) reconnecting = _reconnecting is not null;
        if (reconnecting)
        {
            // No connection to send to anyway: the only thing the controller does now is B to give up.
            if (pressed.HasFlag(GamepadButtons.B)) DisconnectRequested?.Invoke();
            return true;
        }
        bool closeTyping = false;
        lock (_gate)
        {
            // Typing: B on the controller closes the typing bar (the rest of the pad still drives the console).
            if (_typing is not null && pressed.HasFlag(GamepadButtons.B) && !_menu) { _typing = null; _dirty = true; closeTyping = true; }
        }
        if (closeTyping) { ShowToast("Typing closed", 1500); return true; }
        lock (_gate)
        {
            if (_away) return true; // the launcher has the controller
            // Start timing the hold; Apply (called continuously from the window loop) fires it.
            if ((buttons & chord) == chord) { if (double.IsNaN(_chordSinceMs)) { _chordSinceMs = Now; _chordFired = false; } }
            else _chordSinceMs = double.NaN;
            if (!_menu) return false;
            if (_chordFired && (buttons & chord) != 0) return true; // still releasing the hold that opened it
        }
        if (pressed.HasFlag(GamepadButtons.DPadUp)) Move(-1);
        if (pressed.HasFlag(GamepadButtons.DPadDown)) Move(+1);
        if (pressed.HasFlag(GamepadButtons.DPadLeft)) Change(-1);
        if (pressed.HasFlag(GamepadButtons.DPadRight)) Change(+1);
        if (pressed.HasFlag(GamepadButtons.A)) Change(+1, activate: true);
        if (pressed.HasFlag(GamepadButtons.B)) CloseMenu();
        return true;
    }

    // ---- Typing on the console: text from a keyboard (or the touch keyboard) goes to the console's text box -------
    private string? _typing;            // non-null while typing
    private readonly bool _canType;

    /// <summary>Whether typing is on screen.</summary>
    public bool Typing { get { lock (_gate) return _typing is not null; } }

    /// <summary>Start typing (menu item, or the first key typed on a keyboard).</summary>
    public void StartTyping()
    {
        if (!_canType) return;
        lock (_gate) { _typing ??= ""; _menu = false; _barUntilMs = 0; _dirty = true; }
    }

    /// <summary>Text typed in the window (SDL text input: already in the keyboard's layout, any language).</summary>
    public void OnText(string text)
    {
        if (!_canType || text.Length == 0) return;
        string now;
        lock (_gate)
        {
            if (_away || _reconnecting is not null || _menu) return;
            _typing = (_typing ?? "") + text;
            now = _typing; _dirty = true;
        }
        TextTyped?.Invoke(now, false);
    }

    private bool TypingKey(int sym)
    {
        const int Esc = 27, Enter = 13, Backspace = 8, KpEnter = 0x40000058;
        string? now = null; bool done = false, cancel = false;
        lock (_gate)
        {
            if (_typing is null) return false;
            switch (sym)
            {
                case Backspace when _typing.Length > 0:
                    _typing = char.IsLowSurrogate(_typing[^1]) && _typing.Length > 1 ? _typing[..^2] : _typing[..^1];
                    now = _typing; break;
                case Enter or KpEnter: now = _typing; done = true; _typing = null; break;
                case Esc: cancel = true; _typing = null; break;
            }
            _dirty = true;
        }
        if (now is not null) TextTyped?.Invoke(now, done);
        if (cancel) ShowToast("Typing closed", 1500);
        return true; // every key belongs to the text while typing
    }

    /// <summary>Key press from the window (SDL keycode). Returns true if handled.</summary>
    public bool OnKey(int sym)
    {
        const int Esc = 27, Enter = 13, Space = 32, Up = 0x40000052, Down = 0x40000051, Left = 0x40000050, Right = 0x4000004F,
            F3 = 0x4000003C, F9 = 0x40000042, F10 = 0x40000043, F11 = 0x40000044, F12 = 0x40000045;
        lock (_gate) if (_away) return true;
        if (TypingKey(sym)) return true;
        switch (sym)
        {
            case F10: SaveClipRequested?.Invoke(); return true;
            case Esc:
                // Real keyboard in use: a tap is the game's Esc; hold it to open this menu (the game sees nothing).
                if (!MenuOpen && EscGoesToGame?.Invoke() == true)
                {
                    long press;
                    lock (_gate) { if (_escDownAt >= 0) return true; press = ++_escPress; _escDownAt = Now; } // repeats: already timing
                    _ = Task.Delay((int)EscHoldMs).ContinueWith(_ =>
                    {
                        bool held;
                        lock (_gate) { held = _escDownAt >= 0 && _escPress == press; if (held) _escDownAt = -2; } // -2: used for the menu
                        if (held) ToggleMenu();
                    });
                    return true;
                }
                ToggleMenu();
                return true;
            case F11: ToggleFullscreen(); return true;
            case F3: Set(NextStats(1)); return true;
            case F9:
                bool first;
                lock (_gate) { first = !_f9Down; _f9Down = true; }
                if (first) MicKey?.Invoke(true); // held keys repeat: only the first press counts
                return true;
            case F12: ScreenshotRequested?.Invoke(); return true;
        }
        if (!MenuOpen) return false;
        switch (sym)
        {
            case Up: Move(-1); return true;
            case Down: Move(+1); return true;
            case Left: Change(-1); return true;
            case Right: Change(+1); return true;
            case Enter or Space: Change(+1, activate: true); return true;
        }
        return false;
    }

    /// <summary>
    /// Tap (touchscreen) or click in canvas coordinates. With nothing showing, a tap brings up a small bar
    /// (Stream menu / Disconnect) for a few seconds, so a stray touch never does anything by itself.
    /// </summary>
    public void OnClick(int x, int y)
    {
        bool disconnect = false, back = false;
        lock (_gate)
        {
            if (_reconnecting is not null)
            {
                if (_reconnectDisconnectRect.Contains(x, y)) disconnect = true;
                else return;
            }
            else if (_away)
            {
                back = _awayRect.Contains(x, y);
                if (!back) return;
            }
            else if (_menu)
            {
                int hit = Array.FindIndex(_itemRects, r => r.Contains(x, y));
                if (hit < 0)
                {
                    if (!_panelRect.Contains(x, y)) { _menu = false; _dirty = true; } // tap outside closes
                    return;
                }
                _selected = hit; _dirty = true;
            }
            else if (_barUntilMs > 0)
            {
                if (_barMenuRect.Contains(x, y)) { _menu = true; _selected = 0; }
                else if (_barDisconnectRect.Contains(x, y)) disconnect = true;
                _barUntilMs = 0; _dirty = true;
                if (!disconnect) return;
            }
            else
            {
                _barUntilMs = Now + BarShowMs; _dirty = true;
                return;
            }
        }
        if (back) { ComeBack(); return; }
        if (disconnect) { DisconnectRequested?.Invoke(); return; }
        Change(+1, activate: true);
    }

    private void ToggleMenu()
    {
        lock (_gate) { _menu = !_menu; _selected = 0; _barUntilMs = 0; _dirty = true; }
    }

    private void CloseMenu()
    {
        lock (_gate) { _menu = false; _dirty = true; }
    }

    private void Move(int d)
    {
        lock (_gate) { _selected = (_selected + d + Layout.Length) % Layout.Length; _dirty = true; }
    }

    private void ToggleFullscreen()
    {
        lock (_gate) { _fullscreen = !_fullscreen; _pendingFullscreen = _fullscreen; _dirty = true; }
    }

    private void Set(Func<AppSettings, AppSettings> change)
    {
        lock (_gate) { _s = change(_s); _dirty = true; }
        SettingsChanged?.Invoke(change);
    }

    /// <summary>Change the selected item: left/right step a value (dir), A/Enter/tap steps forward and wraps.</summary>
    private void Change(int dir, bool activate = false)
    {
        Item item;
        lock (_gate) item = Layout[_selected].item;
        static T Step<T>(T[] steps, T current, int dir, bool wrap) where T : IComparable<T>
        {
            int i = Array.FindIndex(steps, v => v.CompareTo(current) >= 0);
            if (i < 0) i = steps.Length - 1;
            int next = i + dir;
            if (wrap) next = (next + steps.Length) % steps.Length;
            return steps[Math.Clamp(next, 0, steps.Length - 1)];
        }
        static int Cycle(int value, int count, int dir) => ((value + dir) % count + count) % count;
        switch (item)
        {
            case Item.Resume: if (activate) CloseMenu(); break;
            case Item.XboxButton: if (activate) { CloseMenu(); XboxButtonRequested?.Invoke(); } break;
            case Item.Profile: Set(s => s with { Profile = (StreamProfile)Cycle((int)s.Profile, 3, dir) }); break;
            case Item.MaxBitrate: Set(s => s with { MaxMbps = Step(BitrateSteps, s.MaxMbps, dir, activate) }); break;
            case Item.Picture: Set(s => s with { Scaling = (VideoScaling)Cycle((int)s.Scaling, 3, dir) }); break;
            case Item.Clarity: Set(s => s with { Clarity = (ClarityLevel)(activate ? Cycle((int)s.Clarity, 4, 1) : Math.Clamp((int)s.Clarity + dir, 0, 3)) }); break;
            case Item.Fullscreen: ToggleFullscreen(); break;
            case Item.Enhance: Set(s => s with { Enhance = !s.Enhance }); break;
            case Item.Stats: Set(NextStats(activate ? 1 : dir)); break;
            case Item.Volume:
                Set(s => s with { Audio = true, Volume = activate ? (s.Volume >= 100 ? 0 : Math.Min(100, s.Volume + 10)) : Math.Clamp(s.Volume + dir * 10, 0, 100) });
                break;
            case Item.Rumble: Set(s => s with { RumbleStrength = Step(RumbleSteps, s.RumbleStrength, dir, activate) }); break;
            case Item.TriggerRumble: Set(s => s with { TriggerRumble = !s.TriggerRumble }); break;
            case Item.MouseSpeed: Set(s => s with { MouseSensitivity = Step(MouseSteps, Math.Round(s.MouseSensitivity, 2), activate ? 1 : dir, activate) }); break;
            case Item.Microphone:
                // Off -> On (open mic) -> Push to talk -> Off.
                Set(s => !s.Microphone ? s with { Microphone = true, PushToTalk = false }
                    : !s.PushToTalk ? s with { PushToTalk = true } : s with { Microphone = false, PushToTalk = false });
                break;
            case Item.Party:
                if (!activate) break;
                CloseMenu();
                PartyRequested?.Invoke();
                break;
            case Item.Tips:
                if (!activate) break;
                CheckResult? tips;
                lock (_gate) tips = _tips;
                if (tips is not null && ConnectionAdvisor.Suggestion(tips, _s).Length > 0)
                {
                    Set(s => ConnectionAdvisor.Apply(tips, s));
                    ShowToast(tips.RecommendFastest ? "Saved. Bitrate changes now; Fastest (720p) from the next stream" : "Suggested settings saved", 4000);
                }
                break;
            case Item.Screenshot: if (activate) { CloseMenu(); ScreenshotRequested?.Invoke(); } break;
            case Item.SaveClip: if (activate) { CloseMenu(); SaveClipRequested?.Invoke(); } break;
            case Item.Type: if (activate) { CloseMenu(); StartTyping(); TouchKeyboardWanted?.Invoke(); } break;
            case Item.TurnOff: if (activate) { CloseMenu(); TurnOffRequested?.Invoke(); } break;
            case Item.MoreSettings: if (activate) { GoAway(); MoreSettingsRequested?.Invoke(); } break;
            case Item.Disconnect: if (activate) { CloseMenu(); DisconnectRequested?.Invoke(); } break;
        }
    }

    // ---- Drawing --------------------------------------------------------------------------------

    private static readonly SKColor Accent = new(0x3D, 0xDC, 0x97);
    private static readonly SKColor Warn = new(0xF5, 0xB8, 0x41);
    private static readonly SKColor Bad = new(0xFF, 0x6B, 0x6B);
    private static readonly SKColor Text = new(0xEC, 0xEE, 0xF1);
    private static readonly SKColor Muted = new(0x8E, 0x98, 0xA5);
    private static readonly SKColor Faint = new(0x5D, 0x66, 0x72);
    private static readonly SKColor Panel = new(0x16, 0x1A, 0x20, 0xF5);
    private static readonly SKColor Tile = new(0x1D, 0x22, 0x2A);
    private static readonly SKTypeface Regular = SKTypeface.FromFamilyName(null, SKFontStyle.Normal);
    private static readonly SKTypeface Bold = SKTypeface.FromFamilyName(null, SKFontStyle.Bold);

    public unsafe void Render(Span<byte> rgba, int stride)
    {
        bool menu, fullscreen, bar, away;
        string? reconnecting, typing;
        string? toast;
        ToastKind toastKind;
        int selected;
        LiveStats? st;
        (int w, int h) size;
        AppSettings s;
        lock (_gate)
        {
            menu = _menu; fullscreen = _fullscreen; selected = _selected; st = _stats; size = _videoSize; s = _s; away = _away;
            if (_barUntilMs > 0 && Now >= _barUntilMs) _barUntilMs = 0;
            bar = _barUntilMs > 0;
            if (_toastUntilMs > 0 && Now >= _toastUntilMs) { _toastUntilMs = 0; _toast = null; }
            toast = _toast;
            reconnecting = _reconnecting;
            typing = _typing;
            toastKind = _toastKind;
            _dirty = false;
        }
        var info = new SKImageInfo(CanvasWidth, CanvasHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        fixed (byte* px = rgba)
        {
            using var surface = SKSurface.Create(info, (IntPtr)px, stride);
            var c = surface.Canvas;
            c.Clear(SKColors.Transparent);
            if (reconnecting is not null) DrawReconnecting(c, reconnecting);
            else if (away) DrawAway(c);
            else
            {
                if (s.ShowStatsHud && !menu)
                {
                    if (s.StatsDetailed) { LiveStats[] hist; string route; lock (_gate) { hist = _history.ToArray(); route = _route; } DrawHudDetailed(c, st, size, s, hist, route); }
                    else DrawHud(c, st, size);
                }
                if (bar && !menu) DrawBar(c);
                if (menu) DrawMenu(c, st, size, selected, s, fullscreen);
                else if (typing is not null) DrawTyping(c, typing);
            }
            if (!menu && reconnecting is null && !away) DrawMic(c);
            if (toast is not null) DrawToast(c, toast, toastKind);
            c.Flush();
        }
    }

    private static SKPaint Font(SKTypeface face, float size, SKColor color) =>
        new() { Typeface = face, TextSize = size, Color = color, IsAntialias = true, SubpixelText = true };

    private static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true };

    private static string Res((int w, int h) size) => size.h > 0 ? $"{size.h}p" : "–";

    private static SKColor Quality(LiveStats? s) =>
        s is null ? Muted : s.Link != "Connected" ? Warn : s.LossPct > 2 || s.PingMs > 80 || s.JitterMs > 15 ? Warn : Accent;

    private static void DrawHud(SKCanvas c, LiveStats? s, (int w, int h) size)
    {
        var parts = s is null
            ? new[] { "Connecting…" }
            : new[] { $"{s.Mbps:F1} Mbps", $"{s.PingMs:F0} ms ping", $"{s.Fps:F0} fps", Res(size), $"{s.NetToScreenP50Ms:F0} ms to screen" };
        using var font = Font(Bold, 26, Text);
        using var sep = Font(Regular, 26, Muted);
        float x = 48, y = 40, h = 56, pad = 24, gap = 22;
        float w = pad * 2 + 22 + parts.Sum(p => font.MeasureText(p)) + gap * 2 * (parts.Length - 1) + sep.MeasureText("·") * (parts.Length - 1);
        using (var bg = Fill(new SKColor(0x0C, 0x0E, 0x12, 0xC8))) c.DrawRoundRect(x, y, w, h, h / 2, h / 2, bg);
        using (var dot = Fill(Quality(s))) c.DrawCircle(x + pad + 6, y + h / 2, 7, dot);
        float tx = x + pad + 22, ty = y + h / 2 + 9;
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) { tx += gap; c.DrawText("·", tx, ty, sep); tx += sep.MeasureText("·") + gap; }
            c.DrawText(parts[i], tx, ty, font);
            tx += font.MeasureText(parts[i]);
        }
    }

    /// <summary>
    /// Detailed stats (F3 twice, or Stats on screen: Detailed): where the delay goes, how the network is doing,
    /// what the picture is, and the controller, with the last minute of bitrate and network-to-screen time.
    /// </summary>
    private static void DrawHudDetailed(SKCanvas c, LiveStats? s, (int w, int h) size, AppSettings cfg, LiveStats[] hist, string route)
    {
        float x = 40, y = 36, w = 520, pad = 26, row = 34;
        var sections = new List<(string title, (string k, string v, SKColor color)[] rows)>();
        if (s is not null)
        {
            SKColor Lvl(double v, double warn, double bad) => v >= bad ? Bad : v >= warn ? Warn : Text;
            sections.Add(("LATENCY", [
                ("Ping (round trip)", $"{s.PingMs:F0} ms", Lvl(s.PingMs, 40, 80)),
                ("Network to screen", $"{s.NetToScreenP50Ms:F1} ms  (worst {s.NetToScreenP99Ms:F0})", Lvl(s.NetToScreenP50Ms, 25, 50)),
                ("Decode", $"{s.DecodeMs:F1} ms", Lvl(s.DecodeMs, 8, 14)),
                ("Jitter", $"{s.JitterMs:F1} ms", Lvl(s.JitterMs, 8, 15)),
            ]));
            sections.Add(("NETWORK", [
                ("Bitrate", s.CapMbps > 0 ? $"{s.Mbps:F1} Mbps  (cap {s.CapMbps:F0})" : $"{s.Mbps:F1} Mbps", Text),
                ("Packet loss", $"{s.LossPct:F1} %", Lvl(s.LossPct, 0.5, 2)),
                ("Buffer", $"{s.BufferMs:F0} ms", Text),
            ]));
            string res = size.h > 0 ? $"{size.w}×{size.h}" : "–";
            string look = (cfg.Enhance ? "Enhance on" : "Enhance off") + " · Clarity " + ClarityNames[Math.Clamp((int)cfg.Clarity, 0, 3)];
            sections.Add(("PICTURE", [
                ("Stream", $"{res} · {s.Fps:F0} fps", Lvl(60 - s.Fps, 5, 15)),
                ("Look", look, Text),
                ("Sound behind", $"{s.AudioQueuedMs:F0} ms", Lvl(s.AudioQueuedMs, 120, 250)),
            ]));
            if (s.Pads > 0)
                sections.Add(("CONTROLLER", [("Input", $"{s.Pads} pad{(s.Pads == 1 ? "" : "s")} · {s.PadHz:F0} Hz · {s.PadP99Us / 1000:F1} ms to send", Text)]));
        }
        float h = pad + 44 + sections.Sum(sec => 44 + sec.rows.Length * row) + (hist.Length > 1 ? 150 : 0) + pad - 10;
        using (var bg = Fill(new SKColor(0x0C, 0x0E, 0x12, 0xE8))) c.DrawRoundRect(x, y, w, h, 22, 22, bg);
        using var head = Font(Bold, 26, Text);
        using var key = Font(Regular, 23, Muted);
        using var val = Font(Bold, 23, Text);
        using var sec = Font(Bold, 16, Faint);
        float cy = y + pad + 22;
        using (var dot = Fill(Quality(s))) c.DrawCircle(x + pad + 7, cy - 8, 8, dot);
        string title = s is null ? "Connecting…" : (s.Link.Length > 0 ? s.Link : "Streaming") + (route.Length > 0 ? " · " + route : "");
        c.DrawText(Trim(title, head, w - pad * 2 - 26), x + pad + 26, cy, head);
        cy += 22;
        foreach (var (stitle, rows) in sections)
        {
            cy += 40;
            c.DrawText(stitle, x + pad, cy, sec);
            cy += 4;
            foreach (var (k, v, color) in rows)
            {
                cy += row;
                c.DrawText(k, x + pad, cy, key);
                val.Color = color;
                string vv = Trim(v, val, w - pad * 2 - key.MeasureText(k) - 16);
                c.DrawText(vv, x + w - pad - val.MeasureText(vv), cy, val);
            }
        }
        if (hist.Length > 1)
        {
            // Last minute: bitrate (green, against the cap) and network-to-screen time (amber).
            cy += 24;
            c.DrawText("LAST MINUTE", x + pad, cy + 16, sec);
            using var legend = Font(Regular, 17, Muted);
            string l2 = "— to screen";
            c.DrawText(l2, x + w - pad - legend.MeasureText(l2), cy + 16, legend);
            legend.Color = Accent;
            string l1 = "— bitrate   ";
            c.DrawText(l1, x + w - pad - legend.MeasureText(l2) - legend.MeasureText(l1), cy + 16, legend);
            var area = new SKRect(x + pad, cy + 28, x + w - pad, cy + 118);
            using (var tile = Fill(Tile)) c.DrawRoundRect(area, 10, 10, tile);
            double maxMbps = Math.Max(5, hist.Max(v => Math.Max(v.Mbps, v.CapMbps)));
            double maxMs = Math.Max(20, hist.Max(v => v.NetToScreenP50Ms));
            void Line(Func<LiveStats, double> pick, double max, SKColor color)
            {
                using var path = new SKPath();
                for (int i = 0; i < hist.Length; i++)
                {
                    float px = area.Left + 6 + (area.Width - 12) * (HistorySeconds - hist.Length + i) / (HistorySeconds - 1);
                    float py = area.Bottom - 6 - (float)(Math.Clamp(pick(hist[i]) / max, 0, 1) * (area.Height - 12));
                    if (i == 0) path.MoveTo(px, py); else path.LineTo(px, py);
                }
                using var pen = new SKPaint { Color = color, IsStroke = true, StrokeWidth = 3, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
                c.DrawPath(path, pen);
            }
            Line(v => v.Mbps, maxMbps, Accent);
            Line(v => v.NetToScreenP50Ms, maxMs, Warn);
        }
    }

    /// <summary>Bottom-left mic pill: "Muted" (red), "Hold to talk" (push to talk, idle) or a green light while you talk.</summary>
    private void DrawMic(SKCanvas c)
    {
        bool on, muted, talking, ptt;
        lock (_gate) { on = _micOn; muted = _micMuted; talking = _micTalking; ptt = _micPtt; }
        if (!on || !(muted || talking)) return;
        string text = talking && !muted ? "" : ptt ? "Hold to talk" : "Mic muted";
        using var f = Font(Bold, 24, Text);
        float h = 52, w = text.Length == 0 ? h : 64 + f.MeasureText(text) + 22, x = 40, y = CanvasHeight - h - 40;
        var color = talking && !muted ? Accent : ptt ? Muted : Bad;
        using (var bg = Fill(new SKColor(0x0C, 0x0E, 0x12, 0xD0))) c.DrawRoundRect(x, y, w, h, h / 2, h / 2, bg);
        // A microphone: capsule, cradle and stand.
        float cx = x + h / 2, cy = y + h / 2 - 3;
        using var ink = new SKPaint { Color = color, IsAntialias = true, StrokeWidth = 3.5f, IsStroke = true, StrokeCap = SKStrokeCap.Round };
        using (var fill = Fill(color)) c.DrawRoundRect(cx - 6, cy - 13, 12, 20, 6, 6, fill);
        c.DrawArc(new SKRect(cx - 11, cy - 6, cx + 11, cy + 13), 0, 180, false, ink);
        c.DrawLine(cx, cy + 13, cx, cy + 18, ink);
        if (muted) using (var slash = new SKPaint { Color = Bad, IsAntialias = true, StrokeWidth = 4, StrokeCap = SKStrokeCap.Round }) c.DrawLine(cx - 14, cy - 14, cx + 14, cy + 18, slash);
        if (text.Length > 0) c.DrawText(text, x + 60, y + h / 2 + 9, f);
    }

    /// <summary>Bottom-centre bar shown after a tap: big touch targets, within thumb reach on a handheld.</summary>
    private void DrawBar(SKCanvas c)
    {
        float bw = 330, bh = 96, gap = 18, y = CanvasHeight - bh - 64;
        float x0 = (CanvasWidth - (bw * 2 + gap)) / 2;
        var menuRect = new SKRect(x0, y, x0 + bw, y + bh);
        var discRect = new SKRect(x0 + bw + gap, y, x0 + bw * 2 + gap, y + bh);
        lock (_gate) { _barMenuRect = menuRect; _barDisconnectRect = discRect; }
        using (var bg = Fill(new SKColor(0x16, 0x1A, 0x20, 0xF0)))
        {
            c.DrawRoundRect(menuRect, bh / 2, bh / 2, bg);
            c.DrawRoundRect(discRect, bh / 2, bh / 2, bg);
        }
        using (var ring = new SKPaint { Color = new SKColor(0x3D, 0xDC, 0x97, 0x90), IsStroke = true, StrokeWidth = 3, IsAntialias = true })
            c.DrawRoundRect(menuRect, bh / 2, bh / 2, ring);
        using (var ring = new SKPaint { Color = new SKColor(0xFF, 0x6B, 0x6B, 0x90), IsStroke = true, StrokeWidth = 3, IsAntialias = true })
            c.DrawRoundRect(discRect, bh / 2, bh / 2, ring);
        using (var ink = new SKPaint { Color = Accent, IsAntialias = true, StrokeWidth = 6, IsStroke = true, StrokeCap = SKStrokeCap.Round })
            for (int i = -1; i <= 1; i++) c.DrawLine(menuRect.Left + 46, menuRect.MidY + i * 14, menuRect.Left + 80, menuRect.MidY + i * 14, ink);
        using (var ink = new SKPaint { Color = Bad, IsAntialias = true, StrokeWidth = 6, IsStroke = true, StrokeCap = SKStrokeCap.Round })
        {
            c.DrawArc(new SKRect(discRect.Left + 44, discRect.MidY - 18, discRect.Left + 80, discRect.MidY + 18), -60, 300, false, ink);
            c.DrawLine(discRect.Left + 62, discRect.MidY - 24, discRect.Left + 62, discRect.MidY - 4, ink);
        }
        using var f = Font(Bold, 32, Text);
        c.DrawText("Stream menu", menuRect.Left + 104, menuRect.MidY + 11, f);
        c.DrawText("Disconnect", discRect.Left + 104, discRect.MidY + 11, f);
        // "Controller: hold [View] + [Menu]" with the buttons drawn, not named.
        using var hint = Font(Regular, 24, Text);
        const string lead = "Controller: hold", plus = "+";
        float r = 20, sp = 12;
        float total = hint.MeasureText(lead) + sp + 2 * r + sp + hint.MeasureText(plus) + sp + 2 * r;
        float hx = (CanvasWidth - total) / 2, mid = y - 46, baseline = mid + 8;
        var pill = new SKRect(hx - 22, mid - r - 8, hx + total + 22, mid + r + 8);
        using (var bg = Fill(new SKColor(0x16, 0x1A, 0x20, 0xE0))) c.DrawRoundRect(pill, pill.Height / 2, pill.Height / 2, bg);
        c.DrawText(lead, hx, baseline, hint); hx += hint.MeasureText(lead) + sp;
        DrawPadButton(c, hx + r, mid, r, view: true); hx += 2 * r + sp;
        c.DrawText(plus, hx, baseline, hint); hx += hint.MeasureText(plus) + sp;
        DrawPadButton(c, hx + r, mid, r, view: false);
    }

    /// <summary>A controller button badge: View (two overlapping windows) or Menu (three lines).</summary>
    private static void DrawPadButton(SKCanvas c, float cx, float cy, float r, bool view)
    {
        using (var bg = Fill(new SKColor(0x40, 0x48, 0x55))) c.DrawCircle(cx, cy, r, bg);
        using (var rim = new SKPaint { Color = new SKColor(0x5A, 0x64, 0x72), IsStroke = true, StrokeWidth = 1.5f, IsAntialias = true })
            c.DrawCircle(cx, cy, r, rim);
        // Glyph on a 24-unit grid centred on the badge, about 60% of its width.
        float k = r * 1.35f / 24f, ox = cx - 12 * k, oy = cy - 12 * k;
        using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true, IsStroke = true, StrokeWidth = 2.2f * k };
        if (view)
        {
            // Back window (partly hidden) and front window.
            using var back = new SKPath();
            back.MoveTo(ox + 13 * k, oy + 9 * k); back.LineTo(ox + 13 * k, oy + 4 * k); back.LineTo(ox + 4 * k, oy + 4 * k);
            back.LineTo(ox + 4 * k, oy + 13 * k); back.LineTo(ox + 9 * k, oy + 13 * k);
            c.DrawPath(back, ink);
            c.DrawRect(new SKRect(ox + 10 * k, oy + 10 * k, ox + 19 * k, oy + 19 * k), ink);
        }
        else
        {
            ink.StrokeCap = SKStrokeCap.Round;
            for (int i = -1; i <= 1; i++) c.DrawLine(ox + 5 * k, cy + i * 5 * k, ox + 19 * k, cy + i * 5 * k, ink);
        }
    }

    /// <summary>Bottom bar while typing: the text going to the console, and how to finish.</summary>
    private static void DrawTyping(SKCanvas c, string text)
    {
        float w = 1240, h = 150, x = (CanvasWidth - w) / 2, y = CanvasHeight - h - 70;
        using (var bg = Fill(new SKColor(0x16, 0x1A, 0x20, 0xF5))) c.DrawRoundRect(x, y, w, h, 28, 28, bg);
        using (var border = new SKPaint { Color = new SKColor(0x3D, 0xDC, 0x97, 0x70), IsStroke = true, StrokeWidth = 2, IsAntialias = true })
            c.DrawRoundRect(x, y, w, h, 28, 28, border);
        using (var label = Font(Bold, 19, Accent)) c.DrawText("TYPING ON THE CONSOLE", x + 36, y + 44, label);
        using var f = Font(Bold, 38, Text);
        // Show the end of long text: that's where the cursor is.
        string shown = text;
        while (shown.Length > 0 && f.MeasureText("…" + shown + "▏") > w - 72) shown = shown[1..];
        if (shown.Length < text.Length) shown = "…" + shown;
        c.DrawText(shown.Length == 0 ? "" : shown, x + 36, y + 96, f);
        using (var caret = Fill(Accent)) c.DrawRect(x + 36 + f.MeasureText(shown) + 4, y + 64, 3, 42, caret);
        using var hint = Font(Regular, 21, Muted);
        const string help = "Enter  done   ·   Esc or B  close   ·   the console's text box updates as you type";
        c.DrawText(help, x + 36, y + 134, hint);
    }

    private static void DrawToast(SKCanvas c, string text, ToastKind kind = ToastKind.Good)
    {
        using var f = Font(Bold, 26, Text);
        var color = kind switch { ToastKind.Bad => Bad, ToastKind.Warn => Warn, _ => Accent };
        float w = f.MeasureText(text) + 96, h = 64, x = (CanvasWidth - w) / 2, y = 48;
        using (var bg = Fill(new SKColor(0x16, 0x1A, 0x20, 0xF0))) c.DrawRoundRect(x, y, w, h, h / 2, h / 2, bg);
        using (var edge = new SKPaint { Color = color.WithAlpha(0x70), IsStroke = true, StrokeWidth = 2, IsAntialias = true })
            c.DrawRoundRect(x, y, w, h, h / 2, h / 2, edge);
        using (var dot = Fill(color)) c.DrawCircle(x + 34, y + h / 2, 8, dot);
        c.DrawText(text, x + 58, y + h / 2 + 9, f);
    }

    /// <summary>The connection dropped: the last picture stays, dimmed, with what's happening and a way out.</summary>
    private void DrawReconnecting(SKCanvas c, string detail)
    {
        using (var dim = Fill(new SKColor(0, 0, 0, 0xB8))) c.DrawRect(0, 0, CanvasWidth, CanvasHeight, dim);
        float w = 860, h = 300, x = (CanvasWidth - w) / 2, y = (CanvasHeight - h) / 2;
        using (var bg = Fill(Panel)) c.DrawRoundRect(x, y, w, h, 28, 28, bg);
        using (var edge = new SKPaint { Color = Warn.WithAlpha(0x60), IsStroke = true, StrokeWidth = 2, IsAntialias = true })
            c.DrawRoundRect(x, y, w, h, 28, 28, edge);
        // A small spinner: three dots, the bright one moving.
        int phase = (int)(Now / 250) % 3;
        for (int i = 0; i < 3; i++)
            using (var dot = Fill(i == phase ? Warn : Warn.WithAlpha(0x50))) c.DrawCircle(CanvasWidth / 2f - 24 + i * 24, y + 56, 8, dot);
        using var t = Font(Bold, 36, Text);
        using var sub = Font(Regular, 24, Muted);
        Center(c, $"Reconnecting to {_console}…", y + 122, t);
        Center(c, detail, y + 166, sub);
        using var bf = Font(Bold, 26, Bad);
        const string label = "Disconnect  (B)";
        float bw = bf.MeasureText(label) + 72, bh = 60, bx = (CanvasWidth - bw) / 2, by = y + h - bh - 32;
        _reconnectDisconnectRect = new SKRect(bx, by, bx + bw, by + bh);
        using (var bbg = Fill(new SKColor(0xFF, 0x6B, 0x6B, 0x26))) c.DrawRoundRect(_reconnectDisconnectRect, bh / 2, bh / 2, bbg);
        Center(c, label, by + bh / 2 + 9, bf);
        _dirty = true; // keep the spinner moving
    }

    /// <summary>Shown while the launcher's Settings page is open over the stream.</summary>
    private void DrawAway(SKCanvas c)
    {
        using (var dim = Fill(new SKColor(0, 0, 0, 0xB0))) c.DrawRect(0, 0, CanvasWidth, CanvasHeight, dim);
        float w = 820, h = 230, x = (CanvasWidth - w) / 2, y = (CanvasHeight - h) / 2;
        var r = new SKRect(x, y, x + w, y + h);
        lock (_gate) _awayRect = r;
        using (var bg = Fill(Panel)) c.DrawRoundRect(r, 28, 28, bg);
        using var t = Font(Bold, 34, Text);
        using var sub = Font(Regular, 24, Muted);
        using var cta = Font(Bold, 26, Accent);
        Center(c, "Settings are open in Vexel", y + 78, t);
        Center(c, "Your controller works the launcher until you come back.", y + 124, sub);
        Center(c, "Press B in Settings, or tap here, to return", y + 180, cta);
    }

    private static void Center(SKCanvas c, string text, float y, SKPaint p) => c.DrawText(text, (CanvasWidth - p.MeasureText(text)) / 2, y, p);

    private void DrawMenu(SKCanvas c, LiveStats? st, (int w, int h) size, int selected, AppSettings s, bool fullscreen)
    {
        using (var dim = Fill(new SKColor(0, 0, 0, 0x8C))) c.DrawRect(0, 0, CanvasWidth, CanvasHeight, dim);

        float pw = 1560, ph = 960, px = (CanvasWidth - pw) / 2, py = (CanvasHeight - ph) / 2;
        lock (_gate) _panelRect = new SKRect(px, py, px + pw, py + ph);
        using (var panel = Fill(Panel)) c.DrawRoundRect(px, py, pw, ph, 32, 32, panel);
        using (var border = new SKPaint { Color = new SKColor(0x3D, 0xDC, 0x97, 0x50), IsStroke = true, StrokeWidth = 2, IsAntialias = true })
            c.DrawRoundRect(px, py, pw, ph, 32, 32, border);

        // ---- Left column: what's streaming and how it's going
        float lx = px + 52, lw = 520, y = py + 70;
        using (var label = Font(Bold, 20, Accent)) c.DrawText("STREAMING", lx, y, label);
        using (var title = Font(Bold, 50, Text)) c.DrawText(Trim(_console, title, lw), lx, y + 62, title);
        var elapsed = DateTime.UtcNow - _started;
        using (var meta = Font(Regular, 24, Muted))
            c.DrawText($"{s.Profile} · {(elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss"))}{(_route.Length > 0 ? " · " + _route : "")}", lx, y + 104, meta);

        y += 146;
        var tiles = new (string label, string value, string unit)[]
        {
            ("BITRATE", st is null ? "–" : $"{st.Mbps:F1}", "Mbps"),
            ("PING", st is null ? "–" : $"{st.PingMs:F0}", "ms"),
            ("FRAME RATE", st is null ? "–" : $"{st.Fps:F0}", "fps"),
            ("PICTURE", Res(size), ""),
        };
        float tw = (lw - 16) / 2, th = 128;
        for (int i = 0; i < tiles.Length; i++)
        {
            float tx = lx + (i % 2) * (tw + 16), ty = y + (i / 2) * (th + 16);
            using (var bg = Fill(Tile)) c.DrawRoundRect(tx, ty, tw, th, 18, 18, bg);
            using var l = Font(Bold, 17, Muted);
            using var v = Font(Bold, 48, Text);
            using var u = Font(Regular, 20, Muted);
            c.DrawText(tiles[i].label, tx + 22, ty + 38, l);
            c.DrawText(tiles[i].value, tx + 22, ty + 98, v);
            c.DrawText(tiles[i].unit, tx + 22 + v.MeasureText(tiles[i].value) + 8, ty + 98, u);
        }
        y += 2 * th + 16 + 50;
        CheckResult? tips;
        lock (_gate) tips = _tips;
        if (tips is { Level: not CheckLevel.Good })
        {
            // Connection tips (from this stream's numbers): the worst two, with what to do.
            using (var label = Font(Bold, 18, tips.Level == CheckLevel.Poor ? Bad : Warn)) c.DrawText("CONNECTION TIPS", lx, y, label);
            float ty = y + 36;
            using var tt = Font(Bold, 23, Text);
            using var td = Font(Regular, 20, Muted);
            foreach (var f in tips.Findings.Where(f => f.Level != CheckLevel.Good).Take(2))
            {
                using (var dot = Fill(f.Level == CheckLevel.Poor ? Bad : Warn)) c.DrawCircle(lx + 7, ty - 8, 6, dot);
                c.DrawText(Trim(f.Title, tt, lw - 24), lx + 24, ty, tt);
                ty = DrawWrapped(c, f.Detail, lx + 24, ty + 30, lw - 24, td, 2) + 46;
            }
            if (ConnectionAdvisor.Suggestion(tips, s).Length > 0)
                using (var hint = Font(Regular, 20, Accent)) c.DrawText("Suggested settings (right) applies a fix", lx + 24, ty - 6, hint);
        }
        else if (st is not null && st.Verdict.Length > 0)
        {
            using (var dot = Fill(Quality(st))) c.DrawCircle(lx + 8, y - 8, 7, dot);
            using var vt = Font(Regular, 23, Text);
            DrawWrapped(c, st.Verdict, lx + 28, y, lw - 28, vt, 2);
        }
        using (var hint = Font(Regular, 21, Muted))
        {
            float hy = py + ph - 120;
            c.DrawText("D-pad up / down   move", lx, hy, hint);
            c.DrawText("Left / right or A   change", lx, hy + 32, hint);
            c.DrawText("B   back to game   ·   tap outside to close", lx, hy + 64, hint);
        }

        // ---- Right column: settings
        float rx = px + 640, rw = pw - 640 - 52, ry = py + 34, ih = Layout.Length switch { > 21 => 31, > 20 => 33, > 18 => 35, > 17 => 38, > 15 => 41, _ => 46 };
        using (var div = Fill(new SKColor(0x26, 0x2B, 0x33))) c.DrawRect(px + 610, py + 40, 1.5f, ph - 80, div);
        for (int i = 0; i < Layout.Length; i++)
        {
            var (item, section) = Layout[i];
            if (section is not null)
            {
                if (section.Length > 0)
                {
                    ry += 14;
                    using var sl = Font(Bold, 17, Faint);
                    c.DrawText(section, rx + 24, ry + 18, sl);
                    ry += 30;
                }
                else ry += 20;
            }
            var r = new SKRect(rx, ry, rx + rw, ry + ih);
            lock (_gate) _itemRects[i] = r;
            bool sel = i == selected, danger = item is Item.Disconnect or Item.TurnOff;
            if (sel)
                using (var hi = Fill(danger ? new SKColor(0xFF, 0x6B, 0x6B, 0x33) : new SKColor(0x3D, 0xDC, 0x97, 0x2E)))
                    c.DrawRoundRect(r, 16, 16, hi);
            using var f = Font(Bold, 27, danger ? Bad : sel ? Accent : Text);
            c.DrawText(Label(item), r.Left + 24, r.MidY + 9, f);

            string? value = item == Item.Tips ? TipsValue(s) : Value(item, s, fullscreen);
            if (value is not null)
            {
                bool off = value is "Off" or "0%";
                using var vf = Font(Bold, 25, off ? Muted : sel ? Text : Accent);
                float right = r.Right - 24;
                if (sel && Adjustable(item))
                {
                    using var arrow = Font(Bold, 28, Accent);
                    c.DrawText("›", right - arrow.MeasureText("›"), r.MidY + 10, arrow);
                    right -= arrow.MeasureText("›") + 14;
                    float vx = right - vf.MeasureText(value);
                    c.DrawText(value, vx, r.MidY + 9, vf);
                    c.DrawText("‹", vx - 14 - arrow.MeasureText("‹"), r.MidY + 10, arrow);
                }
                else c.DrawText(value, right - vf.MeasureText(value), r.MidY + 9, vf);
            }
            else if (item == Item.MoreSettings)
                using (var cf = Font(Bold, 28, Muted)) c.DrawText("›", r.Right - 24 - cf.MeasureText("›"), r.MidY + 10, cf);
            ry += ih + 3;
        }
    }

    private static string Label(Item item) => item switch
    {
        Item.Resume => "Resume",
        Item.XboxButton => "Xbox button (guide)",
        Item.Profile => "Profile",
        Item.MaxBitrate => "Maximum bitrate",
        Item.Picture => "Picture",
        Item.Clarity => "Clarity boost",
        Item.Fullscreen => "Full screen",
        Item.Enhance => "Enhance (sharper when enlarged)",
        Item.Stats => "Stats on screen",
        Item.Volume => "Volume",
        Item.Rumble => "Rumble",
        Item.TriggerRumble => "Trigger rumble",
        Item.MouseSpeed => "Mouse sensitivity",
        Item.Microphone => "Microphone (chat)",
        Item.Party => "Party & Discord chat…",
        Item.Tips => "Suggested settings",
        Item.Screenshot => "Take screenshot",
        Item.SaveClip => "Save last 30 seconds",
        Item.Type => "Type on the console",
        Item.TurnOff => "End and turn off Xbox",
        Item.MoreSettings => "More settings…",
        _ => "Disconnect",
    };

    private static bool Adjustable(Item item) => item is Item.Profile or Item.MaxBitrate or Item.Picture or Item.Clarity
        or Item.Volume or Item.Rumble or Item.Stats or Item.MouseSpeed;

    /// <summary>Stats readout: Off → Simple → Detailed → Off (dir -1 goes the other way).</summary>
    private static Func<AppSettings, AppSettings> NextStats(int dir) => s =>
    {
        int now = !s.ShowStatsHud ? 0 : s.StatsDetailed ? 2 : 1, next = ((now + dir) % 3 + 3) % 3;
        return s with { ShowStatsHud = next > 0, StatsDetailed = next == 2 };
    };

    private string TipsValue(AppSettings s)
    {
        CheckResult? t;
        lock (_gate) t = _tips;
        if (t is null) return "Checking…";
        string sug = ConnectionAdvisor.Suggestion(t, s);
        return sug.Length > 0 ? sug : t.Level == CheckLevel.Good ? "All good" : "None";
    }

    private static string? Value(Item item, AppSettings s, bool fullscreen) => item switch
    {
        Item.Profile => s.Profile.ToString(),
        Item.MaxBitrate => $"{s.MaxMbps:F0} Mbps",
        Item.Picture => s.Scaling switch { VideoScaling.Fill => "Fill", VideoScaling.Stretch => "Stretch", _ => "Fit" },
        Item.Clarity => ClarityNames[Math.Clamp((int)s.Clarity, 0, 3)],
        Item.Fullscreen => fullscreen ? "On" : "Off",
        Item.Enhance => s.Enhance ? "On" : "Off",
        Item.Stats => !s.ShowStatsHud ? "Off" : s.StatsDetailed ? "Detailed" : "Simple",
        Item.Volume => s.Audio ? $"{s.Volume}%" : "Off",
        Item.Microphone => !s.Microphone ? "Off" : s.PushToTalk ? "Push to talk" : "On",
        Item.Rumble => s.RumbleStrength <= 0 ? "Off" : $"{s.RumbleStrength}%",
        Item.TriggerRumble => s.TriggerRumble ? "On" : "Off",
        Item.MouseSpeed => s.KeyboardMouseNative ? "Game's own" : $"{s.MouseSensitivity:0.##}×",
        _ => null,
    };

    /// <summary>Draws text wrapped to <paramref name="max"/> wide (at most <paramref name="maxLines"/>); returns the last line's baseline.</summary>
    private static float DrawWrapped(SKCanvas c, string text, float x, float y, float max, SKPaint p, int maxLines)
    {
        var words = text.Split(' ');
        string line = "";
        int lines = 0;
        foreach (var w in words)
        {
            string trial = line.Length == 0 ? w : line + " " + w;
            if (p.MeasureText(trial) > max && line.Length > 0)
            {
                if (++lines == maxLines) { c.DrawText(Trim(line + "…", p, max), x, y, p); return y; }
                c.DrawText(line, x, y, p);
                y += p.TextSize * 1.35f;
                line = w;
            }
            else line = trial;
        }
        if (line.Length > 0) c.DrawText(line, x, y, p);
        return y;
    }

    private static string Trim(string text, SKPaint p, float max)
    {
        if (p.MeasureText(text) <= max) return text;
        while (text.Length > 1 && p.MeasureText(text + "…") > max) text = text[..^1];
        return text + "…";
    }
}
