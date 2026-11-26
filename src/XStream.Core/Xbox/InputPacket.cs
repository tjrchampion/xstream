using System.Buffers.Binary;

namespace XStream.Core.Xbox;

[Flags]
public enum GamepadButtons : ushort
{
    None = 0, Nexus = 2, Menu = 4, View = 8, A = 16, B = 32, X = 64, Y = 128,
    DPadUp = 256, DPadDown = 512, DPadLeft = 1024, DPadRight = 2048,
    LeftShoulder = 4096, RightShoulder = 8192, LeftThumb = 16384, RightThumb = 32768,
}

/// <summary>One controller snapshot. Sticks -1..1 (Y up = positive), triggers 0..1.</summary>
public readonly record struct GamepadState(
    byte Index, GamepadButtons Buttons,
    float LeftX, float LeftY, float RightX, float RightY,
    float LeftTrigger, float RightTrigger);

/// <summary>
/// Binary packets for the "input" data channel (protocol "1.0"), ported from xbox-xcloud-player packet.ts.
/// Layout: u16 reportType | u32 sequence | f64 timestampMs | payload...
/// </summary>
public static class InputPacket
{
    public const ushort ReportMetadata = 1, ReportGamepad = 2, ReportClientMetadata = 8, ReportMouse = 32, ReportKeyboard = 64, ReportVibration = 128;

    /// <summary>Mouse buttons in a native mouse report (the xbox.com player's values).</summary>
    [Flags] public enum MouseButtons : byte { None = 0, Left = 1, Right = 2, Middle = 4, X1 = 8, X2 = 16 }
    private const int HeaderSize = 14;

    /// <summary>First packet after the message-channel handshake.</summary>
    public static byte[] ClientMetadata(uint sequence, double nowMs, byte maxTouchpoints = 1)
    {
        var p = new byte[HeaderSize + 1];
        WriteHeader(p, ReportClientMetadata, sequence, nowMs);
        p[HeaderSize] = maxTouchpoints;
        return p;
    }

    public static byte[] Gamepad(uint sequence, double nowMs, IReadOnlyList<GamepadState> frames)
    {
        var p = new byte[HeaderSize + 1 + 23 * frames.Count];
        WriteHeader(p, ReportGamepad, sequence, nowMs);
        int o = HeaderSize;
        p[o++] = (byte)frames.Count;
        foreach (var f in frames)
        {
            var s = p.AsSpan(o);
            s[0] = f.Index;
            BinaryPrimitives.WriteUInt16LittleEndian(s[1..], (ushort)f.Buttons);
            BinaryPrimitives.WriteInt16LittleEndian(s[3..], Axis(f.LeftX));
            BinaryPrimitives.WriteInt16LittleEndian(s[5..], Axis(f.LeftY));
            BinaryPrimitives.WriteInt16LittleEndian(s[7..], Axis(f.RightX));
            BinaryPrimitives.WriteInt16LittleEndian(s[9..], Axis(f.RightY));
            BinaryPrimitives.WriteUInt16LittleEndian(s[11..], Trigger(f.LeftTrigger));
            BinaryPrimitives.WriteUInt16LittleEndian(s[13..], Trigger(f.RightTrigger));
            BinaryPrimitives.WriteUInt32LittleEndian(s[15..], 1); // physical physicality
            BinaryPrimitives.WriteUInt32BigEndian(s[19..], 1);    // virtual physicality (big-endian in the reference)
            o += 23;
        }
        return p;
    }

    /// <summary>
    /// Native mouse (report 32): u8 count, then per record u32 X, u32 Y, u32 wheelX, u32 wheelY (little-endian;
    /// relative deltas as two's complement), u8 buttons, u8 type (0 = relative). Layout from xbox-xcloud-player packet.ts;
    /// type and button values from the xbox.com player (Relative = 0, Absolute = 1; Left 1, Right 2, Middle 4, X1 8, X2 16).
    /// </summary>
    public static byte[] Mouse(uint sequence, double nowMs, int dx, int dy, int wheelX, int wheelY, MouseButtons buttons)
    {
        var p = new byte[HeaderSize + 1 + 18];
        WriteHeader(p, ReportMouse, sequence, nowMs);
        var s = p.AsSpan(HeaderSize);
        s[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(s[1..], dx);
        BinaryPrimitives.WriteInt32LittleEndian(s[5..], dy);
        BinaryPrimitives.WriteInt32LittleEndian(s[9..], wheelX);
        BinaryPrimitives.WriteInt32LittleEndian(s[13..], wheelY);
        s[17] = (byte)buttons;
        s[18] = 0; // type: 0 = relative, 1 = absolute (the web player's enum; absolute needs "absolute mouse capable" first)
        return p;
    }

    /// <summary>
    /// Native keyboard (report 64): u8 count, then per key u8 type (2 = Windows virtual key), u8 pressed, u8 key code.
    /// </summary>
    public static byte[] Keyboard(uint sequence, double nowMs, IReadOnlyList<(byte Vk, bool Down)> keys)
    {
        var p = new byte[HeaderSize + 1 + 3 * keys.Count];
        WriteHeader(p, ReportKeyboard, sequence, nowMs);
        int o = HeaderSize;
        p[o++] = (byte)keys.Count;
        foreach (var (vk, down) in keys) { p[o++] = 2; p[o++] = down ? (byte)1 : (byte)0; p[o++] = vk; }
        return p;
    }

    /// <summary>Rumble from the console: (gamepad, leftMotor%, rightMotor%, leftTrigger%, rightTrigger%, durationMs).</summary>
    public static bool TryParseVibration(ReadOnlySpan<byte> data, out (int pad, int left, int right, int lt, int rt, int durationMs) v)
    {
        v = default;
        if (data.Length < 1 || data[0] != ReportVibration || data.Length < 10) return false;
        // Reference layout: [0]=type [2]=rumbleType [3]=gamepad [4]=left [5]=right [6]=lt [7]=rt [8..9]=duration
        v = (data[3], data[4], data[5], data[6], data[7], BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));
        return true;
    }

    private static void WriteHeader(Span<byte> p, ushort type, uint seq, double nowMs)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(p, type);
        BinaryPrimitives.WriteUInt32LittleEndian(p[2..], seq);
        BinaryPrimitives.WriteDoubleLittleEndian(p[6..], nowMs);
    }

    // Wire format is Y-up positive (the browser reference negates its Y-down Gamepad API values).
    // XInput on the ROG Ally is already Y-up; SDL is Y-down, so negate SDL values before calling.
    private static short Axis(float v) => (short)Math.Clamp(Math.Round(v * 32767), -32767, 32767);
    private static ushort Trigger(float v) => (ushort)Math.Clamp(Math.Round(v * 65535), 0, 65535);
}
