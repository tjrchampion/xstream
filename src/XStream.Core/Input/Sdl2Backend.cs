using System.Runtime.InteropServices;
using XStream.Core.Xbox;

namespace XStream.Core.Input;

/// <summary>
/// SDL2 game-controller backend for macOS (and a fallback elsewhere). Install SDL2 with
/// `brew install sdl2`, or point XSTREAM_SDL2 at the library file.
/// Background events are enabled so input works without a focused window.
/// </summary>
public sealed class Sdl2Backend : IGamepadBackend
{
    private const string Lib = "SDL2";
    private const uint InitJoystick = 0x200, InitGameController = 0x2000;

    [DllImport(Lib)] private static extern int SDL_Init(uint flags);
    [DllImport(Lib)] private static extern int SDL_InitSubSystem(uint flags);
    [DllImport(Lib)] private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport(Lib)] private static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib)] private static extern int SDL_NumJoysticks();
    [DllImport(Lib)] private static extern int SDL_IsGameController(int index);
    [DllImport(Lib)] private static extern IntPtr SDL_GameControllerOpen(int index);
    [DllImport(Lib)] private static extern void SDL_GameControllerClose(IntPtr gc);
    [DllImport(Lib)] private static extern int SDL_GameControllerGetAttached(IntPtr gc);
    [DllImport(Lib)] private static extern void SDL_GameControllerUpdate();
    [DllImport(Lib)] private static extern byte SDL_GameControllerGetButton(IntPtr gc, int button);
    [DllImport(Lib)] private static extern short SDL_GameControllerGetAxis(IntPtr gc, int axis);
    [DllImport(Lib)] private static extern int SDL_GameControllerRumble(IntPtr gc, ushort low, ushort high, uint ms);
    [DllImport(Lib)] private static extern int SDL_GameControllerRumbleTriggers(IntPtr gc, ushort left, ushort right, uint ms);
    [DllImport(Lib)] private static extern IntPtr SDL_GetError();
    [DllImport(Lib)] private static extern int SDL_JoystickGetDeviceInstanceID(int index);

    static Sdl2Backend() => XStream.Core.Native.NativeLibs.Ensure();

    private readonly IntPtr[] _pads = new IntPtr[4];
    private readonly int[] _ids = { -1, -1, -1, -1 };
    private readonly long[] _rumbleUntil = new long[4];
    private long _nextScan;

    public string Name => "SDL2";
    public int MaxPads => 4;

    public Sdl2Backend()
    {
        SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
        SDL_SetHint("SDL_JOYSTICK_HIDAPI", "1");
        SDL_SetHint("SDL_JOYSTICK_HIDAPI_XBOX", "1");
        if (SDL_InitSubSystem(InitJoystick | InitGameController) != 0)
            throw new InvalidOperationException("SDL2 init failed: " + Marshal.PtrToStringUTF8(SDL_GetError()));
    }

    public void Poll(Span<GamepadState> states, Span<bool> connected)
    {
        SDL_GameControllerUpdate(); // pumps joystick state; cheap
        long now = Environment.TickCount64;
        if (now >= _nextScan) { Scan(); _nextScan = now + 1000; }

        for (int i = 0; i < 4; i++)
        {
            var gc = _pads[i];
            if (gc == IntPtr.Zero || SDL_GameControllerGetAttached(gc) == 0)
            {
                if (gc != IntPtr.Zero) { SDL_GameControllerClose(gc); _pads[i] = IntPtr.Zero; _ids[i] = -1; }
                connected[i] = false;
                continue;
            }
            connected[i] = true;
            Span<bool> b = stackalloc bool[15];
            for (int k = 0; k < 15; k++) b[k] = SDL_GameControllerGetButton(gc, k) != 0;
            states[i] = Map((byte)i, b,
                SDL_GameControllerGetAxis(gc, 0), SDL_GameControllerGetAxis(gc, 1),
                SDL_GameControllerGetAxis(gc, 2), SDL_GameControllerGetAxis(gc, 3),
                SDL_GameControllerGetAxis(gc, 4), SDL_GameControllerGetAxis(gc, 5));
            if (_rumbleUntil[i] != 0 && now >= _rumbleUntil[i]) _rumbleUntil[i] = 0;
        }
    }

    private void Scan()
    {
        int n = SDL_NumJoysticks();
        for (int j = 0; j < n; j++)
        {
            if (SDL_IsGameController(j) == 0) continue;
            int id = SDL_JoystickGetDeviceInstanceID(j);
            if (Array.IndexOf(_ids, id) >= 0) continue; // already open
            int slot = Array.IndexOf(_pads, IntPtr.Zero);
            if (slot < 0) break;
            var gc = SDL_GameControllerOpen(j);
            if (gc == IntPtr.Zero) continue;
            _pads[slot] = gc;
            _ids[slot] = id;
        }
    }

    public void Rumble(int pad, float low, float high, float leftTrigger, float rightTrigger, int durationMs)
    {
        if (pad is < 0 or > 3 || _pads[pad] == IntPtr.Zero) return;
        uint ms = (uint)Math.Clamp(durationMs, 0, 10_000);
        SDL_GameControllerRumble(_pads[pad], U16(low), U16(high), ms);
        SDL_GameControllerRumbleTriggers(_pads[pad], U16(leftTrigger), U16(rightTrigger), ms); // impulse triggers where supported
    }

    /// <summary>SDL button/axis values -> wire state. Public for tests.</summary>
    /// <param name="b">SDL buttons A,B,X,Y,Back,Guide,Start,LStick,RStick,LShoulder,RShoulder,Up,Down,Left,Right.</param>
    public static GamepadState Map(byte index, ReadOnlySpan<bool> b, short lx, short ly, short rx, short ry, short lt, short rt)
    {
        var order = Order;
        var o = GamepadButtons.None;
        for (int k = 0; k < Math.Min(b.Length, order.Length); k++) if (b[k]) o |= order[k];
        // SDL sticks are Y-down; the wire format is Y-up. SDL triggers are 0..32767.
        return new GamepadState(index, o, Axis(lx), -Axis(ly), Axis(rx), -Axis(ry), Math.Clamp(lt / 32767f, 0, 1), Math.Clamp(rt / 32767f, 0, 1));
    }

    private static readonly GamepadButtons[] Order =
        {
            GamepadButtons.A, GamepadButtons.B, GamepadButtons.X, GamepadButtons.Y, GamepadButtons.View, GamepadButtons.Nexus,
            GamepadButtons.Menu, GamepadButtons.LeftThumb, GamepadButtons.RightThumb, GamepadButtons.LeftShoulder,
            GamepadButtons.RightShoulder, GamepadButtons.DPadUp, GamepadButtons.DPadDown, GamepadButtons.DPadLeft, GamepadButtons.DPadRight,
        };

    private static float Axis(short v) => Math.Max(-1f, v / 32767f);
    private static ushort U16(float v) => (ushort)(Math.Clamp(v, 0, 1) * 65535);

    public void Dispose()
    {
        for (int i = 0; i < 4; i++) if (_pads[i] != IntPtr.Zero) { SDL_GameControllerClose(_pads[i]); _pads[i] = IntPtr.Zero; _ids[i] = -1; }
        SDL_QuitSubSystem(InitJoystick | InitGameController);
    }
}
