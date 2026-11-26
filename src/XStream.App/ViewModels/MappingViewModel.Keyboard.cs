using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.Core.Input;

namespace XStream.App.ViewModels;

/// <summary>
/// The Mapping page's "Keyboard & mouse" view: the same controller drawing, each button tagged with its key. Click a
/// button, then press a key (or click a mouse button in the box) to bind it. One layout for every game, saved at once.
/// </summary>
public sealed partial class MappingViewModel
{
    private KeyboardBindings _keys = KeyboardBindings.Default;
    private bool _loadingKeys;

    // Parts only keyboard-and-mouse can bind (the triggers and stick directions; the buttons are shared with the controller view).
    public ButtonSlotViewModel LT { get; private set; } = null!;
    public ButtonSlotViewModel RT { get; private set; } = null!;
    public ButtonSlotViewModel LUp { get; private set; } = null!;
    public ButtonSlotViewModel LDown { get; private set; } = null!;
    public ButtonSlotViewModel LLeft { get; private set; } = null!;
    public ButtonSlotViewModel LRight { get; private set; } = null!;
    public ButtonSlotViewModel RUp { get; private set; } = null!;
    public ButtonSlotViewModel RDown { get; private set; } = null!;
    public ButtonSlotViewModel RLeft { get; private set; } = null!;
    public ButtonSlotViewModel RRight { get; private set; } = null!;
    /// <summary>Everything a key can press.</summary>
    public IReadOnlyList<ButtonSlotViewModel> KeySlots { get; private set; } = [];

    private void InitKeyboard()
    {
        ButtonSlotViewModel S(string source, string label) => new(source, label, PickSlotCommand);
        LT = S("LeftTrigger", "LT"); RT = S("RightTrigger", "RT");
        LUp = S("LeftStickUp", "↑"); LDown = S("LeftStickDown", "↓"); LLeft = S("LeftStickLeft", "←"); LRight = S("LeftStickRight", "→");
        RUp = S("RightStickUp", "↑"); RDown = S("RightStickDown", "↓"); RLeft = S("RightStickLeft", "←"); RRight = S("RightStickRight", "→");
        KeySlots = [.. Slots.Where(s => !s.IsPaddle), LT, RT, LUp, LDown, LLeft, LRight, RUp, RDown, RLeft, RRight];
        LoadKeyboard();
    }

    /// <summary>Showing keyboard-and-mouse keys instead of the controller layout.</summary>
    [ObservableProperty] public partial bool KeyboardView { get; set; }
    partial void OnKeyboardViewChanged(bool value)
    {
        PickSlot(null);
        KeySlot = null;
        foreach (var s in KeySlots) s.ShowKeys = value;
    }
    [RelayCommand] private void ShowController() => KeyboardView = false;
    [RelayCommand] private void ShowKeyboard() => KeyboardView = true;

    /// <summary>The button waiting for a key.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasKeySlot), nameof(KeySlotTitle), nameof(KeySlotKeys))]
    public partial ButtonSlotViewModel? KeySlot { get; set; }
    public bool HasKeySlot => KeySlot is not null;
    public string KeySlotTitle => KeySlot is null ? "" : "Key for " + TargetName(KeySlot.Source);
    public string KeySlotKeys => KeySlot is null ? "" : Target(KeySlot) is { } t && _keys.Keys(t) is { Count: > 0 } k
        ? "Now: " + string.Join(" or ", k.Select(KeyNames.Long)) : "Now: no key";
    /// <summary>What the last key press did ("Space was A's key: A has none now").</summary>
    [ObservableProperty] public partial string KeyMessage { get; set; } = "";
    public string KeyChangedText => _keys.ToText().Length == 0 ? "Default keys" : "Your keys";

    private void PickKey(ButtonSlotViewModel? slot)
    {
        foreach (var s in KeySlots) s.IsSelected = s == slot;
        KeySlot = slot;
        KeyMessage = "";
    }

    [RelayCommand] private void CloseKey() => PickKey(null);

    /// <summary>A key (or mouse button) was pressed while a button is waiting: bind it. Returns false when it isn't a key Vexel knows.</summary>
    public bool BindKey(string name)
    {
        if (KeySlot is not { } slot || Target(slot) is not { } target) return false;
        if (!KeyNames.TryParse(name, out int code)) return false;
        if (KeyNames.IsReserved(code))
        {
            KeyMessage = $"{KeyNames.Long(code)} is kept for Vexel (Esc menu, F3 stats, F10 clip, F11 full screen, F12 screenshot).";
            return true;
        }
        var took = Enum.GetValues<KbmTarget>().Where(t => t != target && _keys.Keys(t).Contains(code)).ToList();
        _keys = _keys.Bind(target, code);
        KeyMessage = took.Count == 0 ? $"{KeyNames.Long(code)} presses {TargetName(target.ToString())}."
            : $"{KeyNames.Long(code)} moved from {string.Join(", ", took.Select(t => TargetName(t.ToString())))}" +
              (took.Any(t => _keys.Keys(t).Count == 0) ? ", which has no key now." : ".");
        SaveKeyboard();
        return true;
    }

    [RelayCommand]
    private void ClearKey()
    {
        if (KeySlot is not { } slot || Target(slot) is not { } t) return;
        _keys = _keys.With(t, []);
        KeyMessage = "";
        SaveKeyboard();
    }

    [RelayCommand]
    private void DefaultKey()
    {
        if (KeySlot is not { } slot || Target(slot) is not { } t) return;
        _keys = _keys.With(t, KeyboardBindings.Default.Keys(t));
        KeyMessage = "";
        SaveKeyboard();
    }

    [RelayCommand]
    private void ResetKeys()
    {
        _keys = KeyboardBindings.Default;
        KeyMessage = "";
        SaveKeyboard();
    }

    // ---- When it's on, and how the mouse feels -----------------------------------------------------------
    public string[] KeyModeNames { get; } = ["Off", "When no controller is connected", "Always (alongside a controller)"];
    [ObservableProperty] public partial int KeyModeIndex { get; set; } = 1;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MouseSensitivityText))] public partial double MouseSensitivity { get; set; } = 1;
    [ObservableProperty] public partial bool MouseInvertY { get; set; }
    public string MouseSensitivityText => $"{MouseSensitivity:0.00}x";
    partial void OnKeyModeIndexChanged(int value) => SaveKeyboard();
    partial void OnMouseSensitivityChanged(double value) => SaveKeyboard();
    partial void OnMouseInvertYChanged(bool value) => SaveKeyboard();

    private void LoadKeyboard()
    {
        _loadingKeys = true;
        try
        {
            var s = _store.Current;
            _keys = KeyboardBindings.Parse(s.KeyboardMouseBindings);
            KeyModeIndex = Math.Clamp((int)s.KeyboardMouse, 0, 2);
            MouseSensitivity = Math.Clamp(s.MouseSensitivity, 0.25, 4);
            MouseInvertY = s.MouseInvertY;
        }
        finally { _loadingKeys = false; }
        ShowKeys();
    }

    private void SaveKeyboard()
    {
        ShowKeys();
        if (_loadingKeys || _loading) return;
        string text = _keys.ToText();
        var mode = (KeyboardMouseMode)Math.Clamp(KeyModeIndex, 0, 2);
        double sens = Math.Round(MouseSensitivity, 2);
        Save(s => s with { KeyboardMouseBindings = text, KeyboardMouse = mode, MouseSensitivity = sens, MouseInvertY = MouseInvertY });
    }

    /// <summary>Tag every button with its keys.</summary>
    private void ShowKeys()
    {
        foreach (var s in KeySlots)
            s.Keys = Target(s) is { } t ? string.Join(" / ", _keys.Keys(t).Select(KeyNames.Short)) : "";
        OnPropertyChanged(nameof(KeySlotKeys));
        OnPropertyChanged(nameof(KeyChangedText));
    }

    private static KbmTarget? Target(ButtonSlotViewModel s) => KeyboardBindings.TryTarget(s.Source, out var t) ? t : null;

    private static string TargetName(string target) => target switch
    {
        "LeftTrigger" => "LT (left trigger)", "RightTrigger" => "RT (right trigger)",
        "LeftStickUp" => "left stick up", "LeftStickDown" => "left stick down", "LeftStickLeft" => "left stick left", "LeftStickRight" => "left stick right",
        "RightStickUp" => "right stick up", "RightStickDown" => "right stick down", "RightStickLeft" => "right stick left", "RightStickRight" => "right stick right",
        _ => Long(target),
    };
}
