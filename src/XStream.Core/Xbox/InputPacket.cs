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
    public const ushort ReportMetadata = 1, ReportGamepad = 2, ReportClientMetadata = 8, ReportVibration = 128;
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
