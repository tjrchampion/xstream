using XStream.Core.Xbox;

namespace XStream.Core.Input;

/// <summary>What a backend can read beyond the Xbox layout: back paddles (bits 0-3 = Paddle1-4) and the gyro (rad/s).</summary>
public readonly record struct PadExtras(byte Paddles, float GyroPitch, float GyroYaw, bool HasGyro);

/// <summary>
/// Controller remapping from settings ("A=B,B=A,Paddle1=LeftThumb,View=None"). Sources: the Xbox buttons and
/// Paddle1-4; targets: Xbox buttons or None. Unlisted buttons pass through. Several sources may share a target.
/// Pure and allocation-free per call (runs on the 1 kHz input thread).
/// </summary>
public sealed class ControllerMapping
{
    private static readonly GamepadButtons[] Buttons = Enum.GetValues<GamepadButtons>().Where(b => b != GamepadButtons.None).ToArray();
    private readonly (GamepadButtons from, GamepadButtons to)[] _buttons;
    private readonly GamepadButtons[] _paddles = new GamepadButtons[4];
    private readonly GamepadButtons _remapped;

    public static readonly ControllerMapping None = new([], new GamepadButtons[4]);
    public bool IsIdentity { get; }

    private ControllerMapping((GamepadButtons, GamepadButtons)[] buttons, GamepadButtons[] paddles)
    {
        _buttons = buttons;
        _paddles = paddles;
        foreach (var (f, _) in buttons) _remapped |= f;
        IsIdentity = buttons.Length == 0 && paddles.All(p => p == GamepadButtons.None);
    }

    /// <summary>Parse a mapping string; unknown names are skipped (listed in <paramref name="problems"/>).</summary>
    public static ControllerMapping Parse(string? text) => Parse(text, out _);

    public static ControllerMapping Parse(string? text, out List<string> problems)
    {
        problems = [];
        if (string.IsNullOrWhiteSpace(text)) return None;
        var buttons = new List<(GamepadButtons, GamepadButtons)>();
        var paddles = new GamepadButtons[4];
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = raw.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2 || !TryTarget(kv[1], out var to)) { problems.Add(raw); continue; }
            if (kv[0].StartsWith("Paddle", StringComparison.OrdinalIgnoreCase) && int.TryParse(kv[0][6..], out int n) && n is >= 1 and <= 4)
                paddles[n - 1] = to;
            else if (TryButton(kv[0], out var from)) buttons.Add((from, to));
            else problems.Add(raw);
        }
        return new ControllerMapping(buttons.ToArray(), paddles);
    }

    public GamepadButtons Apply(GamepadButtons pressed, byte paddles)
    {
        var o = pressed & ~_remapped;
        foreach (var (from, to) in _buttons) if ((pressed & from) != 0) o |= to;
        for (int i = 0; i < 4; i++) if ((paddles & (1 << i)) != 0) o |= _paddles[i];
        return o;
    }

    /// <summary>Whether a hold-to-aim button (Xbox button or PaddleN) is held.</summary>
    public static bool Held(string name, GamepadButtons pressed, byte paddles)
    {
        if (name.StartsWith("Paddle", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[6..], out int n) && n is >= 1 and <= 4)
            return (paddles & (1 << (n - 1))) != 0;
        return TryButton(name, out var b) && (pressed & b) != 0;
    }

    /// <summary>An Xbox button by name (not a paddle).</summary>
    public static bool IsButton(string name, out GamepadButtons b) => TryButton(name, out b);

    private static bool TryTarget(string name, out GamepadButtons b)
    {
        if (name.Equals("None", StringComparison.OrdinalIgnoreCase)) { b = GamepadButtons.None; return true; }
        return TryButton(name, out b);
    }

    private static bool TryButton(string name, out GamepadButtons b)
    {
        // Friendly aliases as well as the enum names.
        name = name.ToLowerInvariant() switch
        {
            "lb" => "LeftShoulder", "rb" => "RightShoulder", "ls" or "l3" => "LeftThumb", "rs" or "r3" => "RightThumb",
            "start" => "Menu", "back" or "select" => "View", "guide" or "xbox" => "Nexus",
            "up" => "DPadUp", "down" => "DPadDown", "left" => "DPadLeft", "right" => "DPadRight",
            _ => name,
        };
        return Enum.TryParse(name, ignoreCase: true, out b) && b != GamepadButtons.None && Buttons.Contains(b);
    }
}

/// <summary>
/// Gyro aiming: the controller's turn rate becomes right-stick deflection (games read the stick as a turn speed,
/// so rate maps to it directly). A small noise floor keeps the aim still when the pad is still, and an
/// "anti-deadzone" lifts the smallest movements past the game's own stick deadzone.
/// </summary>
public sealed class GyroAim(double sensitivity = 1, float antiDeadzone = 0.12f)
{
    /// <summary>Turn rate (rad/s) that gives full stick at sensitivity 1 (about 100 degrees a second).</summary>
    public const float FullScaleRadPerS = 1.75f;
    private const float NoiseRadPerS = 0.015f;
    private readonly float _gain = (float)Math.Clamp(sensitivity, 0.25, 4) / FullScaleRadPerS;

    /// <summary>Stick deflection (-1..1, Y up) for a turn rate: yaw left = stick left, pitch up = stick up.</summary>
    public (float X, float Y) Stick(float pitch, float yaw)
    {
        if (MathF.Sqrt(pitch * pitch + yaw * yaw) < NoiseRadPerS) return (0, 0);
        float x = -yaw * _gain, y = pitch * _gain;
        float mag = MathF.Sqrt(x * x + y * y);
        float lifted = Math.Min(1, antiDeadzone + (1 - antiDeadzone) * Math.Min(1, mag));
        return (x / mag * lifted, y / mag * lifted);
    }

    /// <summary>Real stick plus gyro, clamped to the stick's range.</summary>
    public static (float X, float Y) Combine(float sx, float sy, (float X, float Y) g) =>
        (Math.Clamp(sx + g.X, -1, 1), Math.Clamp(sy + g.Y, -1, 1));
}
