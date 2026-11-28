using XStream.Core.Xbox;

namespace XStream.Core.Input;

/// <summary>When keyboard and mouse drive the virtual controller.</summary>
public enum KeyboardMouseMode
{
    /// <summary>Never: the keyboard only types and runs Vexel's own shortcuts.</summary>
    Off,
    /// <summary>Only while no controller is connected (it steps aside as soon as one is).</summary>
    Auto,
    /// <summary>Always: merged into the first controller if one is connected.</summary>
    On,
}

/// <summary>What a key (or mouse button) does on the virtual controller: a button, a trigger, or one direction of a stick.</summary>
public enum KbmTarget
{
    A, B, X, Y, LeftShoulder, RightShoulder, LeftTrigger, RightTrigger, LeftThumb, RightThumb, View, Menu, Nexus,
    DPadUp, DPadDown, DPadLeft, DPadRight,
    LeftStickUp, LeftStickDown, LeftStickLeft, LeftStickRight,
    RightStickUp, RightStickDown, RightStickLeft, RightStickRight,
}

/// <summary>
/// Key names shared by the launcher (Avalonia's physical keys) and the stream window (SDL scancodes). Physical
/// positions, so WASD stays under the left hand on any keyboard layout. Mouse buttons and the wheel are "keys" too.
/// </summary>
public static class KeyNames
{
    /// <summary>Codes: SDL scancodes below 512, mouse buttons at 512 + SDL button (1 left, 2 middle, 3 right, 4, 5), wheel 520/521.</summary>
    public const int MouseBase = 512, WheelUp = 520, WheelDown = 521, CodeCount = 522;

    private static readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, (string Name, string Long, string Short)> ByCode = [];

    static KeyNames()
    {
        void K(string name, int code, string? longName = null, string? shortName = null)
        {
            ByName[name] = code;
            ByCode[code] = (name, longName ?? name, shortName ?? longName ?? name);
        }
        for (int i = 0; i < 26; i++) K(((char)('A' + i)).ToString(), 4 + i);
        for (int i = 1; i <= 9; i++) K(i.ToString(), 29 + i);
        K("0", 39);
        K("Enter", 40); K("Escape", 41, "Esc"); K("Backspace", 42); K("Tab", 43); K("Space", 44);
        K("Minus", 45, "-"); K("Equals", 46, "="); K("LeftBracket", 47, "["); K("RightBracket", 48, "]"); K("Backslash", 49, "\\");
        K("Semicolon", 51, ";"); K("Quote", 52, "'"); K("Backquote", 53, "`"); K("Comma", 54, ","); K("Period", 55, "."); K("Slash", 56, "/");
        K("CapsLock", 57, "Caps Lock", "Caps");
        for (int i = 1; i <= 12; i++) K("F" + i, 57 + i);
        K("Insert", 73, "Insert", "Ins"); K("Home", 74); K("PageUp", 75, "Page Up", "PgUp"); K("Delete", 76, "Delete", "Del");
        K("End", 77); K("PageDown", 78, "Page Down", "PgDn");
        K("Right", 79, "Right arrow", "→"); K("Left", 80, "Left arrow", "←"); K("Down", 81, "Down arrow", "↓"); K("Up", 82, "Up arrow", "↑");
        K("NumPadDivide", 84, "Num /"); K("NumPadMultiply", 85, "Num *"); K("NumPadSubtract", 86, "Num -"); K("NumPadAdd", 87, "Num +");
        K("NumPadEnter", 88, "Num Enter");
        for (int i = 1; i <= 9; i++) K("NumPad" + i, 88 + i, "Num " + i);
        K("NumPad0", 98, "Num 0"); K("NumPadDecimal", 99, "Num .");
        K("IntlBackslash", 100, "§");
        string meta = OperatingSystem.IsMacOS() ? "Cmd" : "Win", alt = OperatingSystem.IsMacOS() ? "Option" : "Alt";
        K("LeftCtrl", 224, "Left Ctrl", "Ctrl"); K("LeftShift", 225, "Left Shift", "Shift"); K("LeftAlt", 226, "Left " + alt, alt);
        K("LeftMeta", 227, "Left " + meta, meta);
        K("RightCtrl", 228, "Right Ctrl", "R Ctrl"); K("RightShift", 229, "Right Shift", "R Shift"); K("RightAlt", 230, "Right " + alt, "R " + alt);
        K("RightMeta", 231, "Right " + meta, "R " + meta);
        K("MouseLeft", MouseBase + 1, "Left click", "LMB"); K("MouseMiddle", MouseBase + 2, "Middle click", "MMB");
        K("MouseRight", MouseBase + 3, "Right click", "RMB"); K("Mouse4", MouseBase + 4, "Mouse 4", "M4"); K("Mouse5", MouseBase + 5, "Mouse 5", "M5");
        K("WheelUp", WheelUp, "Wheel up", "Wheel ↑"); K("WheelDown", WheelDown, "Wheel down", "Wheel ↓");

        // Other spellings: Avalonia's PhysicalKey names (the launcher), and friendly ones.
        foreach (var (alias, name) in new[]
                 {
                     ("ShiftLeft", "LeftShift"), ("ShiftRight", "RightShift"), ("ControlLeft", "LeftCtrl"), ("ControlRight", "RightCtrl"),
                     ("AltLeft", "LeftAlt"), ("AltRight", "RightAlt"), ("MetaLeft", "LeftMeta"), ("MetaRight", "RightMeta"),
                     ("ArrowUp", "Up"), ("ArrowDown", "Down"), ("ArrowLeft", "Left"), ("ArrowRight", "Right"),
                     ("Equal", "Equals"), ("BracketLeft", "LeftBracket"), ("BracketRight", "RightBracket"), ("Return", "Enter"),
                     ("Esc", "Escape"), ("Shift", "LeftShift"), ("Ctrl", "LeftCtrl"), ("Control", "LeftCtrl"), ("Alt", "LeftAlt"),
                     ("Grave", "Backquote"), ("Apostrophe", "Quote"), ("Mouse1", "MouseLeft"), ("Mouse2", "MouseRight"),
                     ("Mouse3", "MouseMiddle"), ("LeftClick", "MouseLeft"), ("RightClick", "MouseRight"), ("MiddleClick", "MouseMiddle"),
                     ("LMB", "MouseLeft"), ("RMB", "MouseRight"), ("MMB", "MouseMiddle"),
                 })
            ByName[alias] = ByName[name];
    }

    /// <summary>A key's code from its name (or an alias such as Avalonia's "KeyW", "Digit1", "ShiftLeft").</summary>
    public static bool TryParse(string? name, out int code)
    {
        code = -1;
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (name.Length == 4 && name.StartsWith("Key", StringComparison.OrdinalIgnoreCase) && char.IsAsciiLetter(name[3])) name = name[3..];
        else if (name.Length == 6 && name.StartsWith("Digit", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(name[5])) name = name[5..];
        return ByName.TryGetValue(name, out code);
    }

    /// <summary>The canonical name (what's stored in settings).</summary>
    public static string Name(int code) => ByCode.TryGetValue(code, out var n) ? n.Name : "";
    /// <summary>"Left Shift", "Left click": for the launcher's text.</summary>
    public static string Long(int code) => ByCode.TryGetValue(code, out var n) ? n.Long : "";
    /// <summary>"Shift", "LMB": for the little tags on the controller drawing.</summary>
    public static string Short(int code) => ByCode.TryGetValue(code, out var n) ? n.Short : "";

    /// <summary>Keys Vexel keeps for itself in the stream: Esc (menu), F3 (stats), F8 (mini player), F10 (clip), F11 (full screen), F12 (screenshot).</summary>
    public static bool IsReserved(int code) => code is 41 or 60 or 65 or 67 or 68 or 69;
}

/// <summary>
/// Keyboard-and-mouse bindings: which keys press each part of the virtual controller. Stored in settings as changes from
/// the default layout, "Target=Key" pairs separated by commas; a target can take several keys ("RightThumb=C/MouseMiddle")
/// or none ("Nexus=None"). Targets use the Xbox button names (LB, RT, LS… also work) plus LeftTrigger / RightTrigger and
/// LeftStickUp … RightStickRight. Keys use <see cref="KeyNames"/>. Unlisted targets keep their default keys.
/// </summary>
public sealed class KeyboardBindings
{
    /// <summary>
    /// The default layout (a typical PC shooter / action layout):
    /// WASD left stick, mouse right stick, left click RT, right click LT, Space A, Left Ctrl B, E X, R Y,
    /// Q LB, F RB, Left Shift LS (sprint), C or middle click RS, arrows or 1-4 the D-pad, Tab View, Enter Menu,
    /// ` (backquote) or Home the Xbox button. Mouse 4/5, the wheel and the right stick's keys start unbound.
    /// </summary>
    public static readonly IReadOnlyDictionary<KbmTarget, string[]> DefaultKeys = new Dictionary<KbmTarget, string[]>
    {
        [KbmTarget.A] = ["Space"], [KbmTarget.B] = ["LeftCtrl"], [KbmTarget.X] = ["E"], [KbmTarget.Y] = ["R"],
        [KbmTarget.LeftShoulder] = ["Q"], [KbmTarget.RightShoulder] = ["F"],
        [KbmTarget.LeftTrigger] = ["MouseRight"], [KbmTarget.RightTrigger] = ["MouseLeft"],
        [KbmTarget.LeftThumb] = ["LeftShift"], [KbmTarget.RightThumb] = ["C", "MouseMiddle"],
        [KbmTarget.View] = ["Tab"], [KbmTarget.Menu] = ["Enter"], [KbmTarget.Nexus] = ["Backquote", "Home"],
        [KbmTarget.DPadUp] = ["Up", "1"], [KbmTarget.DPadRight] = ["Right", "2"], [KbmTarget.DPadDown] = ["Down", "3"], [KbmTarget.DPadLeft] = ["Left", "4"],
        [KbmTarget.LeftStickUp] = ["W"], [KbmTarget.LeftStickDown] = ["S"], [KbmTarget.LeftStickLeft] = ["A"], [KbmTarget.LeftStickRight] = ["D"],
        [KbmTarget.RightStickUp] = [], [KbmTarget.RightStickDown] = [], [KbmTarget.RightStickLeft] = [], [KbmTarget.RightStickRight] = [],
    };

    private static readonly KbmTarget[] Targets = Enum.GetValues<KbmTarget>();
    private readonly int[][] _keys = new int[Targets.Length][];
    // Flattened (code, target) pairs for the 1 kHz loop.
    private readonly (int Code, KbmTarget Target)[] _pairs;

    public static readonly KeyboardBindings Default = Parse("");

    private KeyboardBindings(int[][] keys)
    {
        _keys = keys;
        _pairs = Targets.SelectMany(t => keys[(int)t].Select(c => (c, t))).ToArray();
    }

    /// <summary>The keys for a target (codes).</summary>
    public IReadOnlyList<int> Keys(KbmTarget t) => _keys[(int)t];

    public static KeyboardBindings Parse(string? text) => Parse(text, out _);

    /// <summary>Parse a bindings string; unknown names and Vexel's own keys (Esc, F3, F8, F10-F12) are skipped and listed in <paramref name="problems"/>.</summary>
    public static KeyboardBindings Parse(string? text, out List<string> problems)
    {
        problems = [];
        var keys = Targets.Select(t => DefaultKeys[t].Select(n => KeyNames.TryParse(n, out int c) ? c : -1).Where(c => c >= 0).ToArray()).ToArray();
        if (!string.IsNullOrWhiteSpace(text))
            foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = raw.Split('=', 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2 || !TryTarget(kv[0], out var target)) { problems.Add(raw); continue; }
                var list = new List<int>();
                if (!kv[1].Equals("None", StringComparison.OrdinalIgnoreCase))
                    foreach (var name in kv[1].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (KeyNames.TryParse(name, out int code) && !KeyNames.IsReserved(code)) { if (!list.Contains(code)) list.Add(code); }
                        else problems.Add($"{kv[0]}={name}");
                    }
                keys[(int)target] = [.. list];
            }
        return new KeyboardBindings(keys);
    }

    /// <summary>The same layout with <paramref name="code"/> as the only key for <paramref name="target"/> (and taken off anything else it pressed).</summary>
    public KeyboardBindings Bind(KbmTarget target, int code)
    {
        var keys = _keys.Select(k => k.Where(c => c != code).ToArray()).ToArray();
        keys[(int)target] = [code];
        return new KeyboardBindings(keys);
    }

    /// <summary>The same layout with these keys for one target (empty: unbound).</summary>
    public KeyboardBindings With(KbmTarget target, IEnumerable<int> codes)
    {
        var keys = _keys.Select(k => k.ToArray()).ToArray();
        keys[(int)target] = codes.Distinct().ToArray();
        return new KeyboardBindings(keys);
    }

    /// <summary>The settings string: only what differs from the default layout ("" when it's the default).</summary>
    public string ToText()
    {
        var parts = new List<string>();
        foreach (var t in Targets)
        {
            var mine = _keys[(int)t];
            var def = Default._keys[(int)t];
            if (mine.SequenceEqual(def)) continue;
            parts.Add($"{t}=" + (mine.Length == 0 ? "None" : string.Join("/", mine.Select(KeyNames.Name))));
        }
        return string.Join(",", parts);
    }

    /// <summary>The controller state these keys make (sticks from keys: digital, opposite keys cancel, diagonals normalised). Allocation-free.</summary>
    public GamepadState Evaluate(ReadOnlySpan<bool> down, byte index = 0)
    {
        var b = GamepadButtons.None;
        float lt = 0, rt = 0;
        int dirs = 0; // one bit per stick direction, in KbmTarget order from LeftStickUp
        foreach (var (code, t) in _pairs)
        {
            if ((uint)code >= (uint)down.Length || !down[code]) continue;
            if (t == KbmTarget.LeftTrigger) lt = 1;
            else if (t == KbmTarget.RightTrigger) rt = 1;
            else if (t >= KbmTarget.LeftStickUp) dirs |= 1 << (t - KbmTarget.LeftStickUp);
            else b |= Button(t);
        }
        var (lx, ly) = Stick(dirs);
        var (rx, ry) = Stick(dirs >> 4);
        return new GamepadState(index, b, lx, ly, rx, ry, lt, rt);
    }

    private static (float X, float Y) Stick(int d)
    {
        float y = (d & 1) - ((d >> 1) & 1), x = ((d >> 3) & 1) - ((d >> 2) & 1); // up, down, left, right
        const float Diagonal = 0.70710678f;
        return x != 0 && y != 0 ? (x * Diagonal, y * Diagonal) : (x, y);
    }

    /// <summary>The Xbox button for a button target (None for triggers and stick directions).</summary>
    public static GamepadButtons Button(KbmTarget t) => t switch
    {
        KbmTarget.A => GamepadButtons.A, KbmTarget.B => GamepadButtons.B, KbmTarget.X => GamepadButtons.X, KbmTarget.Y => GamepadButtons.Y,
        KbmTarget.LeftShoulder => GamepadButtons.LeftShoulder, KbmTarget.RightShoulder => GamepadButtons.RightShoulder,
        KbmTarget.LeftThumb => GamepadButtons.LeftThumb, KbmTarget.RightThumb => GamepadButtons.RightThumb,
        KbmTarget.View => GamepadButtons.View, KbmTarget.Menu => GamepadButtons.Menu, KbmTarget.Nexus => GamepadButtons.Nexus,
        KbmTarget.DPadUp => GamepadButtons.DPadUp, KbmTarget.DPadDown => GamepadButtons.DPadDown,
        KbmTarget.DPadLeft => GamepadButtons.DPadLeft, KbmTarget.DPadRight => GamepadButtons.DPadRight,
        _ => GamepadButtons.None,
    };

    /// <summary>A target from its name: the enum names, or LB / RB / LT / RT / LS / RS / Start / Back / Xbox / Up / Down / Left / Right.</summary>
    public static bool TryTarget(string name, out KbmTarget t)
    {
        name = name.Trim().ToLowerInvariant() switch
        {
            "lb" => "LeftShoulder", "rb" => "RightShoulder", "lt" => "LeftTrigger", "rt" => "RightTrigger",
            "ls" or "l3" => "LeftThumb", "rs" or "r3" => "RightThumb", "start" => "Menu", "back" or "select" => "View",
            "guide" or "xbox" => "Nexus", "up" => "DPadUp", "down" => "DPadDown", "left" => "DPadLeft", "right" => "DPadRight",
            _ => name,
        };
        return Enum.TryParse(name, ignoreCase: true, out t) && Enum.IsDefined(t) && !int.TryParse(name, out _);
    }
}
