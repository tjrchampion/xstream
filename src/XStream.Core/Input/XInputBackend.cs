using System.Runtime.InteropServices;
using XStream.Core.Xbox;

namespace XStream.Core.Input;

/// <summary>
/// Windows XInput backend. This is the path on the ROG Ally X: its built-in controller appears as an
/// XInput device in Armoury Crate's Gamepad mode.
///
/// Latency notes:
///  * Uses the hidden XInputGetStateEx (ordinal 100) so the Guide/Xbox button is readable.
///  * dwPacketNumber unchanged = nothing changed; we skip conversion entirely.
///  * Polling an EMPTY XInput slot is slow (can take milliseconds), so empty slots are only
///    re-checked once a second; connected slots are read every poll.
/// </summary>
public sealed class XInputBackend : IGamepadBackend
{
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration { public ushort Left, Right; }

    private const uint ErrorSuccess = 0;

    [DllImport("xinput1_4.dll", EntryPoint = "#100")] private static extern uint XInputGetStateEx(uint index, out XInputState state);
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out XInputState state);
    [DllImport("xinput1_4.dll")] private static extern uint XInputSetState(uint index, ref XInputVibration vibration);

    private readonly bool _hasEx;
    private readonly uint[] _packet = new uint[4];
    private readonly bool[] _connected = new bool[4];
    private readonly GamepadState[] _last = new GamepadState[4];
    private readonly long[] _nextEmptyCheck = new long[4];
    private readonly long[] _rumbleUntil = new long[4];

    public string Name => _hasEx ? "XInput (with Guide button)" : "XInput";
    public int MaxPads => 4;

    public XInputBackend()
    {
        if (!OperatingSystem.IsWindows()) throw new DllNotFoundException("XInput is Windows-only");
        try { XInputGetStateEx(0, out _); _hasEx = true; }
        catch (EntryPointNotFoundException) { XInputGetState(0, out _); _hasEx = false; }
    }

    public void Poll(Span<GamepadState> states, Span<bool> connected)
    {
        long now = Environment.TickCount64;
        for (uint i = 0; i < 4; i++)
        {
            if (!_connected[i] && now < _nextEmptyCheck[i]) { connected[(int)i] = false; continue; }
            uint r = _hasEx ? XInputGetStateEx(i, out var st) : XInputGetState(i, out st);
            if (r != ErrorSuccess)
            {
                _connected[i] = false;
                _nextEmptyCheck[i] = now + 1000;
                connected[(int)i] = false;
                continue;
            }
            if (!_connected[i] || st.PacketNumber != _packet[i])
            {
                _packet[i] = st.PacketNumber;
                _last[i] = Map((byte)i, st.Gamepad.Buttons, st.Gamepad.LeftTrigger, st.Gamepad.RightTrigger,
                    st.Gamepad.LX, st.Gamepad.LY, st.Gamepad.RX, st.Gamepad.RY);
            }
            _connected[i] = true;
            connected[(int)i] = true;
            states[(int)i] = _last[i];

            if (_rumbleUntil[i] != 0 && now >= _rumbleUntil[i]) { _rumbleUntil[i] = 0; SetRumble(i, 0, 0); }
        }
    }

    public void Rumble(int pad, float low, float high, float leftTrigger, float rightTrigger, int durationMs)
    {
        if (pad is < 0 or > 3 || !_connected[pad]) return;
        // XInput has no trigger motors; fold trigger rumble into the main motors so it isn't lost.
        SetRumble((uint)pad, Math.Max(low, leftTrigger * 0.6f), Math.Max(high, rightTrigger * 0.6f));
        _rumbleUntil[pad] = durationMs > 0 ? Environment.TickCount64 + durationMs : 0;
    }

    private static void SetRumble(uint pad, float low, float high)
    {
        var v = new XInputVibration { Left = (ushort)(Math.Clamp(low, 0, 1) * 65535), Right = (ushort)(Math.Clamp(high, 0, 1) * 65535) };
        XInputSetState(pad, ref v);
    }

    /// <summary>XInput report -> wire state. Public for tests.</summary>
    public static GamepadState Map(byte index, ushort b, byte lt, byte rt, short lx, short ly, short rx, short ry)
    {
        var o = GamepadButtons.None;
        if ((b & 0x0001) != 0) o |= GamepadButtons.DPadUp;
        if ((b & 0x0002) != 0) o |= GamepadButtons.DPadDown;
        if ((b & 0x0004) != 0) o |= GamepadButtons.DPadLeft;
        if ((b & 0x0008) != 0) o |= GamepadButtons.DPadRight;
        if ((b & 0x0010) != 0) o |= GamepadButtons.Menu;
        if ((b & 0x0020) != 0) o |= GamepadButtons.View;
        if ((b & 0x0040) != 0) o |= GamepadButtons.LeftThumb;
        if ((b & 0x0080) != 0) o |= GamepadButtons.RightThumb;
        if ((b & 0x0100) != 0) o |= GamepadButtons.LeftShoulder;
        if ((b & 0x0200) != 0) o |= GamepadButtons.RightShoulder;
        if ((b & 0x0400) != 0) o |= GamepadButtons.Nexus;
        if ((b & 0x1000) != 0) o |= GamepadButtons.A;
        if ((b & 0x2000) != 0) o |= GamepadButtons.B;
        if ((b & 0x4000) != 0) o |= GamepadButtons.X;
        if ((b & 0x8000) != 0) o |= GamepadButtons.Y;
        // XInput sticks are already Y-up, same as the wire format.
        return new GamepadState(index, o, Axis(lx), Axis(ly), Axis(rx), Axis(ry), lt / 255f, rt / 255f);
    }

    private static float Axis(short v) => Math.Max(-1f, v / 32767f);

    public void Dispose()
    {
        for (uint i = 0; i < 4; i++) if (_connected[i]) SetRumble(i, 0, 0);
    }
}
