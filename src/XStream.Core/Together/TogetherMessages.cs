using System.Buffers.Binary;
using System.Text;
using XStream.Core.Xbox;

namespace XStream.Core.Together;

/// <summary>What a Play together datagram carries (the first byte of its decrypted body).</summary>
public enum TogetherMsg : byte
{
    Hello = 1, Welcome = 2, Video = 3, Audio = 4, Input = 5, Rumble = 6, KeyframeRequest = 7, Nack = 8,
    Ping = 9, Pong = 10, Stats = 11, Bye = 12, Kicked = 13,
    /// <summary>Host to guest: every player slot is taken.</summary>
    Full = 14,
    /// <summary>Host to guest, sent to the addresses in a reply code: opens the host's side of the NAT and tells the guest where to say Hello.</summary>
    Punch = 15,
}

/// <summary>Why a guest or the host left (carried in Bye).</summary>
public enum ByeReason : byte { Left = 0, HostEnded = 1, WrongVersion = 2 }

public readonly record struct HelloMsg(byte Version, uint SessionId, string Name);
public readonly record struct WelcomeMsg(byte Slot, string HostName, string GameName, string ArtUrl);
public readonly record struct RumbleMsg(float Low, float High, float LeftTrigger, float RightTrigger, int DurationMs);
public readonly record struct StatsMsg(float LossPct, float Kbps, float RttMs);
public readonly record struct NackEntry(uint FrameId, ushort Index)
{
    /// <summary>Index meaning "the whole frame": none of it arrived, so the guest doesn't know how many pieces it has.</summary>
    public const ushort WholeFrame = 0xFFFF;
}

/// <summary>One piece of a video frame (an H.264 access unit, Annex B).</summary>
public readonly ref struct VideoFragment
{
    public uint FrameId { get; init; }
    public ushort Index { get; init; }
    public ushort Count { get; init; }
    public bool Keyframe { get; init; }
    /// <summary>The frame's 90 kHz timestamp from the console (the guest's playout buffer paces on it).</summary>
    public uint RtpTimestamp { get; init; }
    /// <summary>When the host forwarded it (0.1 ms units on the host's clock, wraps): lets a same-machine test measure the delay added.</summary>
    public uint HostMs { get; init; }
    public ReadOnlySpan<byte> Payload { get; init; }
}

/// <summary>
/// Encoding of every Play together message body (before encryption). Big-endian, length-prefixed strings,
/// no allocations when writing. A body always starts with its <see cref="TogetherMsg"/> byte.
/// </summary>
public static class TogetherMessages
{
    /// <summary>Bumped when host and guest can no longer understand each other.</summary>
    public const byte ProtocolVersion = 1;
    /// <summary>
    /// Largest piece of video per datagram: 18 header + 18 fragment header + 1150 + 16 tag = 1202 bytes, plus 28 of
    /// IP/UDP is 1230, under a 1280-byte path MTU (the IPv6 minimum, and safe through VPNs and PPPoE).
    /// </summary>
    public const int MaxFragmentPayload = 1150;
    public const int VideoHeaderSize = 18;
    /// <summary>Largest body any message needs (a full video fragment).</summary>
    public const int MaxBody = VideoHeaderSize + MaxFragmentPayload;

    public static TogetherMsg TypeOf(ReadOnlySpan<byte> body) => body.Length > 0 ? (TogetherMsg)body[0] : 0;

    // ---- Writers: each returns the body length --------------------------------------------------

    public static int Hello(Span<byte> d, uint sessionId, string name)
    {
        d[0] = (byte)TogetherMsg.Hello; d[1] = ProtocolVersion;
        BinaryPrimitives.WriteUInt32BigEndian(d[2..], sessionId);
        return 6 + WriteString(d[6..], name, 64);
    }

    public static int Welcome(Span<byte> d, in WelcomeMsg w)
    {
        d[0] = (byte)TogetherMsg.Welcome; d[1] = ProtocolVersion; d[2] = w.Slot;
        int o = 3;
        o += WriteString(d[o..], w.HostName, 64);
        o += WriteString(d[o..], w.GameName, 120);
        o += WriteString(d[o..], w.ArtUrl, 600);
        return o;
    }

    /// <summary>Header for a video fragment; the payload (at most <see cref="MaxFragmentPayload"/>) goes straight after.</summary>
    public static int VideoHeader(Span<byte> d, uint frameId, ushort index, ushort count, bool keyframe, uint rtpTs, uint hostMs)
    {
        d[0] = (byte)TogetherMsg.Video;
        BinaryPrimitives.WriteUInt32BigEndian(d[1..], frameId);
        BinaryPrimitives.WriteUInt16BigEndian(d[5..], index);
        BinaryPrimitives.WriteUInt16BigEndian(d[7..], count);
        d[9] = keyframe ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(d[10..], rtpTs);
        BinaryPrimitives.WriteUInt32BigEndian(d[14..], hostMs);
        return VideoHeaderSize;
    }

    public static int Audio(Span<byte> d, ushort seq, ReadOnlySpan<byte> opus)
    {
        d[0] = (byte)TogetherMsg.Audio;
        BinaryPrimitives.WriteUInt16BigEndian(d[1..], seq);
        opus.CopyTo(d[3..]);
        return 3 + opus.Length;
    }

    public static int Input(Span<byte> d, uint seq, in GamepadState s)
    {
        d[0] = (byte)TogetherMsg.Input;
        BinaryPrimitives.WriteUInt32BigEndian(d[1..], seq);
        BinaryPrimitives.WriteUInt16BigEndian(d[5..], (ushort)s.Buttons);
        BinaryPrimitives.WriteInt16BigEndian(d[7..], Axis(s.LeftX));
        BinaryPrimitives.WriteInt16BigEndian(d[9..], Axis(s.LeftY));
        BinaryPrimitives.WriteInt16BigEndian(d[11..], Axis(s.RightX));
        BinaryPrimitives.WriteInt16BigEndian(d[13..], Axis(s.RightY));
        BinaryPrimitives.WriteUInt16BigEndian(d[15..], Trigger(s.LeftTrigger));
        BinaryPrimitives.WriteUInt16BigEndian(d[17..], Trigger(s.RightTrigger));
        return 19;
    }

    public static int Rumble(Span<byte> d, in RumbleMsg r)
    {
        d[0] = (byte)TogetherMsg.Rumble;
        d[1] = Unit(r.Low); d[2] = Unit(r.High); d[3] = Unit(r.LeftTrigger); d[4] = Unit(r.RightTrigger);
        BinaryPrimitives.WriteUInt16BigEndian(d[5..], (ushort)Math.Clamp(r.DurationMs, 0, 65535));
        return 7;
    }

    public static int Nack(Span<byte> d, ReadOnlySpan<NackEntry> entries)
    {
        int n = Math.Min(entries.Length, 180);
        d[0] = (byte)TogetherMsg.Nack; d[1] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(d[(2 + i * 6)..], entries[i].FrameId);
            BinaryPrimitives.WriteUInt16BigEndian(d[(6 + i * 6)..], entries[i].Index);
        }
        return 2 + n * 6;
    }

    public static int Ping(Span<byte> d, uint id, bool pong)
    {
        d[0] = (byte)(pong ? TogetherMsg.Pong : TogetherMsg.Ping);
        BinaryPrimitives.WriteUInt32BigEndian(d[1..], id);
        return 5;
    }

    public static int Stats(Span<byte> d, in StatsMsg s)
    {
        d[0] = (byte)TogetherMsg.Stats;
        BinaryPrimitives.WriteUInt16BigEndian(d[1..], (ushort)Math.Clamp(s.LossPct * 100, 0, 10000));
        BinaryPrimitives.WriteUInt32BigEndian(d[3..], (uint)Math.Clamp(s.Kbps, 0, uint.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(d[7..], (ushort)Math.Clamp(s.RttMs * 10, 0, 65535));
        return 9;
    }

    public static int Bye(Span<byte> d, ByeReason reason) { d[0] = (byte)TogetherMsg.Bye; d[1] = (byte)reason; return 2; }
    public static int Simple(Span<byte> d, TogetherMsg type) { d[0] = (byte)type; return 1; }

    // ---- Readers: false when the body is malformed ----------------------------------------------

    public static bool ReadHello(ReadOnlySpan<byte> b, out HelloMsg m)
    {
        m = default;
        if (b.Length < 7 || TypeOf(b) != TogetherMsg.Hello) return false;
        int o = 6;
        if (!ReadString(b, ref o, out var name)) return false;
        m = new HelloMsg(b[1], BinaryPrimitives.ReadUInt32BigEndian(b[2..]), name);
        return true;
    }

    public static bool ReadWelcome(ReadOnlySpan<byte> b, out WelcomeMsg m, out byte version)
    {
        m = default; version = 0;
        if (b.Length < 6 || TypeOf(b) != TogetherMsg.Welcome) return false;
        version = b[1];
        int o = 3;
        if (!ReadString(b, ref o, out var host) || !ReadString(b, ref o, out var game) || !ReadString(b, ref o, out var art)) return false;
        m = new WelcomeMsg(b[2], host, game, art);
        return true;
    }

    public static bool ReadVideo(ReadOnlySpan<byte> b, out VideoFragment f)
    {
        f = default;
        if (b.Length < VideoHeaderSize || TypeOf(b) != TogetherMsg.Video) return false;
        ushort index = BinaryPrimitives.ReadUInt16BigEndian(b[5..]), count = BinaryPrimitives.ReadUInt16BigEndian(b[7..]);
        if (count == 0 || index >= count) return false;
        f = new VideoFragment
        {
            FrameId = BinaryPrimitives.ReadUInt32BigEndian(b[1..]), Index = index, Count = count, Keyframe = (b[9] & 1) != 0,
            RtpTimestamp = BinaryPrimitives.ReadUInt32BigEndian(b[10..]), HostMs = BinaryPrimitives.ReadUInt32BigEndian(b[14..]),
            Payload = b[VideoHeaderSize..],
        };
        return true;
    }

    public static bool ReadAudio(ReadOnlySpan<byte> b, out ushort seq, out ReadOnlySpan<byte> opus)
    {
        seq = 0; opus = default;
        if (b.Length < 4 || TypeOf(b) != TogetherMsg.Audio) return false;
        seq = BinaryPrimitives.ReadUInt16BigEndian(b[1..]);
        opus = b[3..];
        return true;
    }

    /// <summary>The guest's controller. The index is left 0: the host puts it in the guest's slot.</summary>
    public static bool ReadInput(ReadOnlySpan<byte> b, out uint seq, out GamepadState s)
    {
        seq = 0; s = default;
        if (b.Length < 19 || TypeOf(b) != TogetherMsg.Input) return false;
        seq = BinaryPrimitives.ReadUInt32BigEndian(b[1..]);
        s = new GamepadState(0, (GamepadButtons)BinaryPrimitives.ReadUInt16BigEndian(b[5..]),
            FromAxis(BinaryPrimitives.ReadInt16BigEndian(b[7..])), FromAxis(BinaryPrimitives.ReadInt16BigEndian(b[9..])),
            FromAxis(BinaryPrimitives.ReadInt16BigEndian(b[11..])), FromAxis(BinaryPrimitives.ReadInt16BigEndian(b[13..])),
            BinaryPrimitives.ReadUInt16BigEndian(b[15..]) / 65535f, BinaryPrimitives.ReadUInt16BigEndian(b[17..]) / 65535f);
        return true;
    }

    public static bool ReadRumble(ReadOnlySpan<byte> b, out RumbleMsg r)
    {
        r = default;
        if (b.Length < 7 || TypeOf(b) != TogetherMsg.Rumble) return false;
        r = new RumbleMsg(b[1] / 255f, b[2] / 255f, b[3] / 255f, b[4] / 255f, BinaryPrimitives.ReadUInt16BigEndian(b[5..]));
        return true;
    }

    public static int ReadNack(ReadOnlySpan<byte> b, Span<NackEntry> into)
    {
        if (b.Length < 2 || TypeOf(b) != TogetherMsg.Nack) return 0;
        int n = Math.Min(Math.Min(b[1], (b.Length - 2) / 6), into.Length);
        for (int i = 0; i < n; i++)
            into[i] = new NackEntry(BinaryPrimitives.ReadUInt32BigEndian(b[(2 + i * 6)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(6 + i * 6)..]));
        return n;
    }

    public static bool ReadPing(ReadOnlySpan<byte> b, out uint id)
    {
        id = 0;
        if (b.Length < 5 || TypeOf(b) is not (TogetherMsg.Ping or TogetherMsg.Pong)) return false;
        id = BinaryPrimitives.ReadUInt32BigEndian(b[1..]);
        return true;
    }

    public static bool ReadStats(ReadOnlySpan<byte> b, out StatsMsg s)
    {
        s = default;
        if (b.Length < 9 || TypeOf(b) != TogetherMsg.Stats) return false;
        s = new StatsMsg(BinaryPrimitives.ReadUInt16BigEndian(b[1..]) / 100f, BinaryPrimitives.ReadUInt32BigEndian(b[3..]),
            BinaryPrimitives.ReadUInt16BigEndian(b[7..]) / 10f);
        return true;
    }

    public static ByeReason ReadBye(ReadOnlySpan<byte> b) => b.Length >= 2 ? (ByeReason)b[1] : ByeReason.Left;

    // ---- Helpers ---------------------------------------------------------------------------------

    private static int WriteString(Span<byte> d, string? s, int maxBytes)
    {
        s ??= "";
        // Trim by characters until it fits, so a multi-byte character is never cut in half.
        while (Encoding.UTF8.GetByteCount(s) > maxBytes) s = s[..^1];
        int n = Encoding.UTF8.GetBytes(s, d[2..]);
        BinaryPrimitives.WriteUInt16BigEndian(d, (ushort)n);
        return 2 + n;
    }

    private static bool ReadString(ReadOnlySpan<byte> b, ref int o, out string s)
    {
        s = "";
        if (o + 2 > b.Length) return false;
        int n = BinaryPrimitives.ReadUInt16BigEndian(b[o..]);
        if (o + 2 + n > b.Length) return false;
        s = Encoding.UTF8.GetString(b.Slice(o + 2, n));
        o += 2 + n;
        return true;
    }

    private static short Axis(float v) => (short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767);
    private static float FromAxis(short v) => Math.Max(-1f, v / 32767f);
    private static ushort Trigger(float v) => (ushort)Math.Round(Math.Clamp(v, 0f, 1f) * 65535);
    private static byte Unit(float v) => (byte)Math.Round(Math.Clamp(v, 0f, 1f) * 255);
}
