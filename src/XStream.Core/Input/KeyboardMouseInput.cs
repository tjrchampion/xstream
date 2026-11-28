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
        new(s.KeyboardMouse, Bindings(s), Mouse(s), setRelativeMouse) { Native = s.KeyboardMouseNative };

    // ---- Native: a real keyboard and mouse for the console (experimental) ------------------------------
    /// <summary>Send keys and the mouse to the console as themselves (the virtual controller stays neutral).</summary>
    public bool Native { get => _native; set { if (!value) ReleaseNative(); _native = value; } }
    private volatile bool _native;
    /// <summary>Keys going down/up (Windows virtual-key codes). Window thread.</summary>
    public Action<IReadOnlyList<(byte Vk, bool Down)>>? NativeKeys { get; set; }
    /// <summary>Mouse movement (relative), wheel (120 per notch) and buttons held. Window thread.</summary>
    public Action<int, int, int, int, XStream.Core.Xbox.InputPacket.MouseButtons>? NativeMouse { get; set; }
    private readonly HashSet<byte> _vkDown = [];
    private XStream.Core.Xbox.InputPacket.MouseButtons _mouseDown;

    /// <summary>Let go of everything the console thinks is held (menu opened, focus lost, native turned off).</summary>
    private void ReleaseNative()
    {
        if (_vkDown.Count > 0) { NativeKeys?.Invoke(_vkDown.Select(v => (v, false)).ToList()); _vkDown.Clear(); }
        if (_mouseDown != 0) { _mouseDown = 0; NativeMouse?.Invoke(0, 0, 0, 0, 0); }
    }

    /// <summary>SDL scancode -> Windows virtual-key code (0 = not sent). Keys Vexel keeps (Esc, F3, F8-F12) aren't sent.</summary>
    public static byte Vk(int sc) => sc switch
    {
        >= 4 and <= 29 => (byte)(0x41 + sc - 4),          // A-Z
        >= 30 and <= 38 => (byte)(0x31 + sc - 30),        // 1-9
        39 => 0x30,                                       // 0
        40 => 0x0D, 42 => 0x08, 43 => 0x09, 44 => 0x20,   // Enter, Backspace, Tab, Space
        45 => 0xBD, 46 => 0xBB, 47 => 0xDB, 48 => 0xDD, 49 => 0xDC, 51 => 0xBA, 52 => 0xDE, 53 => 0xC0, 54 => 0xBC, 55 => 0xBE, 56 => 0xBF,
        57 => 0x14,                                       // Caps Lock
        58 or 59 or 61 or 62 or 63 or 64 => (byte)(0x70 + sc - 58), // F1, F2, F4-F7 (F3, F8-F12 are Vexel's)
        73 => 0x2D, 74 => 0x24, 75 => 0x21, 76 => 0x2E, 77 => 0x23, 78 => 0x22, // Insert Home PgUp Delete End PgDn
        79 => 0x27, 80 => 0x25, 81 => 0x28, 82 => 0x26,   // Right Left Down Up
        >= 89 and <= 97 => (byte)(0x61 + sc - 89),        // keypad 1-9
        98 => 0x60, 88 => 0x0D,                           // keypad 0, keypad Enter
        224 => 0xA2, 225 => 0xA0, 226 => 0xA4, 227 => 0x5B, 228 => 0xA3, 229 => 0xA1, 230 => 0xA5, 231 => 0x5C, // Ctrl Shift Alt Win, left/right
        _ => 0,
    };

    private void NativeKey(int scancode, bool down, bool repeat)
    {
        byte vk = Vk(scancode);
        if (vk == 0 || repeat) return;
        if (down ? !_vkDown.Add(vk) : !_vkDown.Remove(vk)) return;
        NativeKeys?.Invoke([(vk, down)]);
    }

    private static XStream.Core.Xbox.InputPacket.MouseButtons MouseBit(int sdlButton) => sdlButton switch
    {
        1 => XStream.Core.Xbox.InputPacket.MouseButtons.Left, 2 => XStream.Core.Xbox.InputPacket.MouseButtons.Middle,
        3 => XStream.Core.Xbox.InputPacket.MouseButtons.Right, 4 => XStream.Core.Xbox.InputPacket.MouseButtons.X1,
        5 => XStream.Core.Xbox.InputPacket.MouseButtons.X2, _ => 0,
    };

    /// <summary>Settings changed (launcher or menu): new mode, keys and mouse feel, live.</summary>
    public void Configure(AppSettings s)
    {
        Mode = s.KeyboardMouse;
        Native = s.KeyboardMouseNative;
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

    private long _motionEvents, _motionCounts;
    private volatile float _peakStick;
    /// <summary>For the session log: mouse-motion events seen, counts taken while captured, and the biggest stick deflection from the mouse, since the last call.</summary>
    public (long Events, long Counts, float PeakStick) TakeMouseStats()
    {
        var r = (Interlocked.Exchange(ref _motionEvents, 0), Interlocked.Exchange(ref _motionCounts, 0), _peakStick);
        _peakStick = 0;
        return r;
    }
    /// <summary>The virtual controller is in use (set each poll by the input pump from <see cref="Mode"/> and the real pads).</summary>
    public bool Active { get => _active; set => _active = value; }

    // ---- Window thread -----------------------------------------------------------------------------------

    private const uint EvWindow = 0x200, EvKeyDown = 0x300, EvKeyUp = 0x301,
        EvMotion = 0x400, EvButtonDown = 0x401, EvButtonUp = 0x402, EvWheel = 0x403;
    private const byte FocusGained = 12, FocusLost = 13;
    private const uint TouchMouseId = 0xFFFFFFFF;

    /// <summary>
    /// One SDL event (the raw SDL_Event bytes). Returns true when it was used here and the window should ignore it
    /// (clicks while captured, and the click that captures). Keys are only watched, never taken: Esc, F3, F8, F10-F12
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
                if (_native && _captured && !IsBlocked()) NativeKey(scancode, type == EvKeyDown, ev[13] != 0);
                return false;
            }
            case EvMotion:
                Interlocked.Increment(ref _motionEvents);
                if (_captured && U32(ev, 12) != TouchMouseId)
                {
                    Interlocked.Add(ref _motionCounts, Math.Abs((int)U32(ev, 28)) + Math.Abs((int)U32(ev, 32)));
                    Interlocked.Add(ref _dx, (int)U32(ev, 28));
                    Interlocked.Add(ref _dy, (int)U32(ev, 32));
                    if (_native && !IsBlocked()) NativeMouse?.Invoke((int)U32(ev, 28), (int)U32(ev, 32), 0, 0, _mouseDown);
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
                    if (_native && !IsBlocked() && MouseBit(button) is var bit and not 0)
                    {
                        _mouseDown = down ? _mouseDown | bit : _mouseDown & ~bit;
                        NativeMouse?.Invoke(0, 0, 0, 0, _mouseDown);
                    }
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
                if (_native && !IsBlocked()) { NativeMouse?.Invoke(0, 0, 0, y * 120, _mouseDown); return true; }
                if (y > 0) Interlocked.Increment(ref _wheelUp);
                else if (y < 0) Interlocked.Increment(ref _wheelDown);
                return true;
            case EvWindow:
                if (ev[12] == FocusLost)
                {
                    _focused = false;
                    // Playing when something took the focus (a permission prompt, another app): carry on when it comes back.
                    _resume = _captured || _resume;
                    Release();
                    Array.Clear(_down); // keys let go while away never send their key-up here
                }
                else if (ev[12] == FocusGained)
                {
                    _focused = true;
                    if (_captured) _reassertAt = Environment.TickCount64 + 50; // captured during the focusing click: apply again now we're in front
                    if (!_resume) _hinted = false; // waiting for a click again: say so
                }
                return false;
        }
        return false;
    }

    private long _reassertAt;

    /// <summary>Every window loop: let go when something else needs the input, take it back after the menu / typing.</summary>
    public void Tick()
    {
        if (_reassertAt > 0 && Environment.TickCount64 >= _reassertAt)
        {
            _reassertAt = 0;
            if (_captured && _focused) { _setRelative(false); _setRelative(true); } // SDL ignores "on" when it thinks it's on already
        }
        bool active = _active, blocked = IsBlocked();
        if (_captured && (blocked || !active))
        {
            Release();
            _resume = blocked && active && _focused; // menu, typing: back to the game afterwards without another click
        }
        else if (!_captured && _resume && !blocked && _focused) // (while away, keep the resume for when the focus comes back)
        {
            _resume = false;
            if (active) Capture();
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
        // macOS: relative mode switched on while the window is still coming to the front (the click that focuses it)
        // hides the pointer but delivers no movement. Apply it again a moment later, when the window is key.
        _reassertAt = Environment.TickCount64 + 150;
        Interlocked.Exchange(ref _dx, 0); Interlocked.Exchange(ref _dy, 0);
        _captured = true;
    }

    /// <summary>Free the mouse (cursor back, clicks go to the menu again).</summary>
    public void Release()
    {
        if (!_captured) return;
        ReleaseNative();
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

        if (!_captured || IsBlocked() || _native) // native: the console gets a real keyboard and mouse instead
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
        float m = MathF.Max(MathF.Abs(mx), MathF.Abs(my));
        if (m > _peakStick) _peakStick = m;
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
