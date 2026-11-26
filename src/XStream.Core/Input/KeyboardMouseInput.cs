using System.Buffers.Binary;
using System.Runtime.InteropServices;
using XStream.Core.Config;
using XStream.Core.Video;
using XStream.Core.Xbox;

namespace XStream.Core.Input;

/// <summary>
/// Play with keyboard and mouse: the stream window's keys and mouse drive a virtual Xbox controller (merged into the
/// controller stream by <see cref="InputPump"/>, so the console sees an ordinary pad).
///
/// The window thread feeds SDL events in (<see cref="OnEvent"/>) and calls <see cref="Tick"/> every loop; the input
/// thread reads the controller state (<see cref="Read"/>). Nothing reaches the console until the player clicks the
/// picture: that captures the mouse (relative mode, cursor hidden). The capture is released, and the virtual pad goes
/// neutral, whenever <see cref="Blocked"/> says the stream menu, typing, the launcher's settings or the reconnect screen
/// has the input, when the window loses focus, or when a controller takes over (Auto). After the menu or typing it
/// comes back by itself.
/// </summary>
public sealed class KeyboardMouseInput : IWindowInput
{
    private sealed record Setup(KeyboardBindings Bindings, MouseStick Mouse);
    private volatile Setup _setup;
    private volatile int _mode;
    private readonly Action<bool> _setRelative;

    // Written on the window thread, read on the input thread (a stale read lasts one 1 ms poll at most).
    private readonly bool[] _down = new bool[KeyNames.CodeCount];
    private readonly bool[] _snap = new bool[KeyNames.CodeCount];
    private long _dx, _dy;
    private int _wheelUp, _wheelDown;
    private volatile bool _captured, _active;
    private bool _focused = true, _resume, _hinted;

    // Input thread only.
    private double _lastReadMs = double.NaN, _wheelUpUntil, _wheelDownUntil;
    private int _seenWheelUp, _seenWheelDown;
    private bool _wasEngaged;

    /// <summary>How long a wheel notch holds its button (the wheel has no "release").</summary>
    public const double WheelPulseMs = 60;

    /// <param name="setRelativeMouse">Turns SDL's relative mouse mode on/off (tests pass their own).</param>
    public KeyboardMouseInput(KeyboardMouseMode mode, KeyboardBindings bindings, MouseStick mouse, Action<bool>? setRelativeMouse = null)
    {
        _mode = (int)mode;
        _setup = new Setup(bindings, mouse);
        _setRelative = setRelativeMouse ?? Sdl.SetRelative;
    }

    public static KeyboardMouseInput FromSettings(AppSettings s, Action<bool>? setRelativeMouse = null) =>
        new(s.KeyboardMouse, Bindings(s), Mouse(s), setRelativeMouse);

    /// <summary>Settings changed (launcher or menu): new mode, keys and mouse feel, live.</summary>
    public void Configure(AppSettings s)
    {
        Mode = s.KeyboardMouse;
        var cur = _setup;
        var mouse = Mouse(s);
        bool sameMouse = cur.Mouse.Sensitivity == mouse.Sensitivity && cur.Mouse.InvertY == mouse.InvertY;
        _setup = new Setup(Bindings(s), sameMouse ? cur.Mouse : mouse); // keep the moving filter if nothing changed
    }

    private static KeyboardBindings Bindings(AppSettings s) => KeyboardBindings.Parse(s.KeyboardMouseBindings);
    private static MouseStick Mouse(AppSettings s) => new(s.MouseSensitivity, invertY: s.MouseInvertY);

    public KeyboardMouseMode Mode { get => (KeyboardMouseMode)_mode; set => _mode = (int)value; }
    public KeyboardBindings Layout => _setup.Bindings;

    /// <summary>True while something else has the input (stream menu, typing, launcher settings, reconnecting). Any thread.</summary>
    public Func<bool>? Blocked { get; set; }
    /// <summary>Raised once (window thread) the first time keyboard and mouse are ready: tell the player to click.</summary>
    public event Action? Hint;

    /// <summary>The mouse is captured and keys drive the controller.</summary>
    public bool Captured => _captured;
    /// <summary>The virtual controller is in use (set each poll by the input pump from <see cref="Mode"/> and the real pads).</summary>
    public bool Active { get => _active; set => _active = value; }

    // ---- Window thread -----------------------------------------------------------------------------------

    private const uint EvWindow = 0x200, EvKeyDown = 0x300, EvKeyUp = 0x301,
        EvMotion = 0x400, EvButtonDown = 0x401, EvButtonUp = 0x402, EvWheel = 0x403;
    private const byte FocusGained = 12, FocusLost = 13;
    private const uint TouchMouseId = 0xFFFFFFFF;

    /// <summary>
    /// One SDL event (the raw SDL_Event bytes). Returns true when it was used here and the window should ignore it
    /// (clicks while captured, and the click that captures). Keys are only watched, never taken: Esc, F3, F10-F12
    /// and typing still work.
    /// </summary>
    public bool OnEvent(ReadOnlySpan<byte> ev)
    {
        if (ev.Length < 36) return false;
        uint type = U32(ev, 0);
        switch (type)
        {
            case EvKeyDown or EvKeyUp:
            {
                int scancode = (int)U32(ev, 16);
                if ((uint)scancode < KeyNames.MouseBase) Volatile.Write(ref _down[scancode], type == EvKeyDown);
                return false;
            }
            case EvMotion:
                if (_captured && U32(ev, 12) != TouchMouseId)
                {
                    Interlocked.Add(ref _dx, (int)U32(ev, 28));
                    Interlocked.Add(ref _dy, (int)U32(ev, 32));
                }
                return false;
            case EvButtonDown or EvButtonUp:
            {
                if (U32(ev, 12) == TouchMouseId) return false; // touchscreen taps stay taps (the menu bar)
                int button = ev[16];
                int code = KeyNames.MouseBase + button;
                bool down = type == EvButtonDown;
                if (_captured)
                {
                    if (button is >= 1 and <= 5) Volatile.Write(ref _down[code], down);
                    return true;
                }
                if (button is >= 1 and <= 5) Volatile.Write(ref _down[code], false);
                if (down && _active && !IsBlocked()) { Capture(); return true; }
                return false;
            }
            case EvWheel:
                if (!_captured || U32(ev, 12) == TouchMouseId) return false;
                int y = (int)U32(ev, 20);
                if (U32(ev, 24) == 1) y = -y; // SDL_MOUSEWHEEL_FLIPPED
                if (y > 0) Interlocked.Increment(ref _wheelUp);
                else if (y < 0) Interlocked.Increment(ref _wheelDown);
                return true;
            case EvWindow:
                if (ev[12] == FocusLost)
                {
                    _focused = false;
                    _resume = false;
                    Release();
                    Array.Clear(_down); // keys let go while away never send their key-up here
                }
                else if (ev[12] == FocusGained) _focused = true;
                return false;
        }
        return false;
    }

    /// <summary>Every window loop: let go when something else needs the input, take it back after the menu / typing.</summary>
    public void Tick()
    {
        bool active = _active, blocked = IsBlocked();
        if (_captured && (blocked || !active))
        {
            Release();
            _resume = blocked && active && _focused; // menu, typing: back to the game afterwards without another click
        }
        else if (!_captured && _resume && !blocked)
        {
            _resume = false;
            if (active && _focused) Capture();
        }
        if (active && !_hinted && !_captured && !blocked)
        {
            _hinted = true;
            Hint?.Invoke();
        }
    }

    public void Capture()
    {
        if (_captured) return;
        _setRelative(true);
        Interlocked.Exchange(ref _dx, 0); Interlocked.Exchange(ref _dy, 0);
        _captured = true;
    }

    /// <summary>Free the mouse (cursor back, clicks go to the menu again).</summary>
    public void Release()
    {
        if (!_captured) return;
        _captured = false;
        _setRelative(false);
        for (int c = KeyNames.MouseBase; c < KeyNames.CodeCount; c++) Volatile.Write(ref _down[c], false);
    }

    private bool IsBlocked() => Blocked?.Invoke() ?? false;

    // ---- Input thread ------------------------------------------------------------------------------------

    /// <summary>The virtual controller now (neutral unless captured and nothing else has the input). Input thread only.</summary>
    public GamepadState Read(double nowMs)
    {
        double dt = double.IsNaN(_lastReadMs) ? 1 : nowMs - _lastReadMs;
        _lastReadMs = nowMs;
        var s = _setup;
        long dx = Interlocked.Exchange(ref _dx, 0), dy = Interlocked.Exchange(ref _dy, 0);
        int wu = Volatile.Read(ref _wheelUp), wd = Volatile.Read(ref _wheelDown);
        if (wu != _seenWheelUp) { _seenWheelUp = wu; _wheelUpUntil = nowMs + WheelPulseMs; }
        if (wd != _seenWheelDown) { _seenWheelDown = wd; _wheelDownUntil = nowMs + WheelPulseMs; }

        if (!_captured || IsBlocked())
        {
            if (_wasEngaged) { s.Mouse.Reset(); _wasEngaged = false; }
            return default;
        }
        _wasEngaged = true;
        for (int i = 0; i < _snap.Length; i++) _snap[i] = Volatile.Read(ref _down[i]);
        _snap[KeyNames.WheelUp] = nowMs < _wheelUpUntil;
        _snap[KeyNames.WheelDown] = nowMs < _wheelDownUntil;
        var keys = s.Bindings.Evaluate(_snap);
        var (mx, my) = s.Mouse.Update(dx, dy, dt);
        var (rx, ry) = Clamp(keys.RightX + mx, keys.RightY + my);
        return keys with { RightX = rx, RightY = ry };
    }

    /// <summary>Real pad plus keyboard and mouse: buttons combined, triggers the larger, sticks added (kept inside the circle).</summary>
    public static GamepadState Merge(in GamepadState pad, in GamepadState kbm)
    {
        var (lx, ly) = Clamp(pad.LeftX + kbm.LeftX, pad.LeftY + kbm.LeftY);
        var (rx, ry) = Clamp(pad.RightX + kbm.RightX, pad.RightY + kbm.RightY);
        return new GamepadState(pad.Index, pad.Buttons | kbm.Buttons, lx, ly, rx, ry,
            Math.Max(pad.LeftTrigger, kbm.LeftTrigger), Math.Max(pad.RightTrigger, kbm.RightTrigger));
    }

    private static (float, float) Clamp(float x, float y)
    {
        float m = MathF.Sqrt(x * x + y * y);
        return m > 1 ? (x / m, y / m) : (x, y);
    }

    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);

    private static class Sdl
    {
        static Sdl() => XStream.Core.Native.NativeLibs.Ensure();
        [DllImport("SDL2")] private static extern int SDL_SetRelativeMouseMode(int enabled);
        public static void SetRelative(bool on) => SDL_SetRelativeMouseMode(on ? 1 : 0);
    }
}
