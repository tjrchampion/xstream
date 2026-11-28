using System.Runtime.CompilerServices;
using XStream.Core.Xbox;

namespace XStream.Core.Input;

/// <summary>What holding a button does: press it (Normal), press and release it repeatedly (Turbo), or latch it (Toggle).</summary>
public enum ButtonMode : byte { Normal, Turbo, Toggle }

/// <summary>
/// Turbo and toggle buttons from settings ("A:turbo,LT:toggle,RightShoulder:turbo"). Names are the Xbox buttons (with
/// the same aliases as the mapping) and the triggers, LT / RT. Applied after remapping, so a paddle takes the mode of
/// the button it presses. Paddles themselves can't have a mode (they never reach the console as themselves).
/// </summary>
public sealed class ButtonModes
{
    /// <summary>Bits past the 16 button bits: the triggers, so one mask covers everything a mode can apply to.</summary>
    public const uint LeftTriggerBit = 1u << 16, RightTriggerBit = 1u << 17;
    private const int Bits = 18;

    public const double DefaultRate = 10, MinRate = 2, MaxRate = 20;

    public static readonly ButtonModes None = new(0, 0);

    /// <summary>Buttons (and trigger bits) that are turbo / toggle.</summary>
    public uint Turbo { get; }
    public uint Toggle { get; }
    public bool IsEmpty => (Turbo | Toggle) == 0;

    private ButtonModes(uint turbo, uint toggle) { Turbo = turbo; Toggle = toggle & ~turbo; }

    public static ButtonModes Parse(string? text) => Parse(text, out _);

    /// <summary>Parse a modes string; unknown names or modes are skipped (listed in <paramref name="problems"/>).</summary>
    public static ButtonModes Parse(string? text, out List<string> problems)
    {
        problems = [];
        if (string.IsNullOrWhiteSpace(text)) return None;
        uint turbo = 0, toggle = 0;
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !TryBit(kv[0], out uint bit)) { problems.Add(raw); continue; }
            switch (kv[1].ToLowerInvariant())
            {
                case "turbo": turbo |= bit; toggle &= ~bit; break;
                case "toggle": toggle |= bit; turbo &= ~bit; break;
                case "normal": turbo &= ~bit; toggle &= ~bit; break;
                default: problems.Add(raw); break;
            }
        }
        return turbo == 0 && toggle == 0 ? None : new ButtonModes(turbo, toggle);
    }

    /// <summary>Back to text, in a fixed order (Xbox buttons by name, then LT, RT).</summary>
    public string ToText()
    {
        if (IsEmpty) return "";
        var parts = new List<string>();
        foreach (var (name, bit) in Names)
            if ((Turbo & bit) != 0) parts.Add(name + ":turbo");
            else if ((Toggle & bit) != 0) parts.Add(name + ":toggle");
        return string.Join(",", parts);
    }

    public ButtonMode Get(string name) =>
        !TryBit(name, out uint bit) ? ButtonMode.Normal
        : (Turbo & bit) != 0 ? ButtonMode.Turbo : (Toggle & bit) != 0 ? ButtonMode.Toggle : ButtonMode.Normal;

    public ButtonModes With(string name, ButtonMode mode)
    {
        if (!TryBit(name, out uint bit)) return this;
        uint t = Turbo & ~bit, g = Toggle & ~bit;
        if (mode == ButtonMode.Turbo) t |= bit; else if (mode == ButtonMode.Toggle) g |= bit;
        return t == 0 && g == 0 ? None : new ButtonModes(t, g);
    }

    /// <summary>Short names of the buttons in a mode ("A, RB, LT"), for logs.</summary>
    public string Describe(ButtonMode mode)
    {
        uint mask = mode == ButtonMode.Turbo ? Turbo : mode == ButtonMode.Toggle ? Toggle : 0;
        return string.Join(", ", Names.Where(n => (mask & n.Bit) != 0).Select(n => Short(n.Name)));
    }

    private static string Short(string name) => name switch
    {
        "LeftShoulder" => "LB", "RightShoulder" => "RB", "LeftThumb" => "LS", "RightThumb" => "RS",
        "DPadUp" => "Up", "DPadDown" => "Down", "DPadLeft" => "Left", "DPadRight" => "Right", "Nexus" => "Xbox",
        _ => name,
    };

    private static readonly (string Name, uint Bit)[] Names =
    [
        .. Enum.GetValues<GamepadButtons>().Where(b => b != GamepadButtons.None).Select(b => (b.ToString(), (uint)b)),
        ("LT", LeftTriggerBit), ("RT", RightTriggerBit),
    ];

    private static bool TryBit(string name, out uint bit)
    {
        switch (name.ToLowerInvariant())
        {
            case "lt" or "lefttrigger": bit = LeftTriggerBit; return true;
            case "rt" or "righttrigger": bit = RightTriggerBit; return true;
        }
        bool ok = ControllerMapping.IsButton(name, out var b);
        bit = ok ? (uint)b : 0;
        return ok;
    }

    // ---- Per pad, on the input thread ---------------------------------------------------------------------

    /// <summary>When each turbo button went down (ms), by bit position. Inline, so the state needs no allocation.</summary>
    [InlineArray(Bits)] public struct PressTimes { private double _t; }

    /// <summary>One pad's turbo/toggle state: what was held last poll, which toggles are latched, when turbo presses began.</summary>
    public struct State
    {
        public uint Held, Latched;
        public PressTimes Since;
        public void Clear() => this = default;
    }

    /// <summary>Trigger counts as held from half way, and stays held until a quarter (no chatter around one point).</summary>
    private static bool TriggerHeld(float v, bool was) => v >= 0.5f || (was && v > 0.25f);

    /// <summary>
    /// Apply turbo and toggle to one pad's state. <paramref name="paused"/> (the stream menu has the controller): pass
    /// the real buttons through so the menu works, and track them without flipping a toggle, so a latched button is
    /// still held when the menu closes and the press that closed it doesn't count. Allocation-free.
    /// </summary>
    public GamepadState Apply(in GamepadState s, ref State st, double nowMs, double rate, bool paused)
    {
        uint modal = Turbo | Toggle;
        uint raw = (uint)s.Buttons;
        if (TriggerHeld(s.LeftTrigger, (st.Held & LeftTriggerBit) != 0)) raw |= LeftTriggerBit;
        if (TriggerHeld(s.RightTrigger, (st.Held & RightTriggerBit) != 0)) raw |= RightTriggerBit;
        uint rising = raw & ~st.Held;
        st.Held = raw;
        if (paused) return s;

        st.Latched = (st.Latched ^ (rising & Toggle)) & Toggle;
        uint outBits = (raw & ~modal) | st.Latched;
        uint turboHeld = raw & Turbo;
        if (turboHeld != 0)
        {
            // The first press is immediate: each button's cycle starts when it goes down, on for half of it.
            double period = 1000.0 / Math.Clamp(rate, MinRate, MaxRate);
            for (int i = 0; i < Bits; i++)
            {
                uint bit = 1u << i;
                if ((turboHeld & bit) == 0) continue;
                if ((rising & bit) != 0) st.Since[i] = nowMs;
                double phase = (nowMs - st.Since[i]) % period;
                if (phase < period / 2) outBits |= bit;
            }
        }
        float lt = (modal & LeftTriggerBit) != 0 ? ((outBits & LeftTriggerBit) != 0 ? 1f : 0f) : s.LeftTrigger;
        float rt = (modal & RightTriggerBit) != 0 ? ((outBits & RightTriggerBit) != 0 ? 1f : 0f) : s.RightTrigger;
        return s with { Buttons = (GamepadButtons)(outBits & 0xFFFF), LeftTrigger = lt, RightTrigger = rt };
    }
}
