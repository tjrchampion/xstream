using System.Buffers.Binary;

namespace XStream.Core.Rtp;

/// <summary>Builds RTCP feedback packets the stability engine sends back to the console.</summary>
public static class Rtcp
{
    /// <summary>
    /// Receiver Estimated Max Bitrate (draft-alvestrand-rmcat-remb-03): tells the sender
    /// "don't send more than this". Requires goog-remb in the negotiated SDP.
    /// </summary>
    public static byte[] BuildRemb(uint senderSsrc, double bitrateKbps, params uint[] mediaSsrcs)
    {
        if (mediaSsrcs.Length is 0 or > 255) throw new ArgumentException("1..255 SSRCs required", nameof(mediaSsrcs));
        ulong bps = (ulong)Math.Max(0, bitrateKbps * 1000);
        int exp = 0;
        while (bps > 0x3FFFF) { bps >>= 1; exp++; } // 18-bit mantissa, 6-bit exponent

        int len = 20 + 4 * mediaSsrcs.Length;
        var p = new byte[len];
        p[0] = 0x80 | 15;                    // V=2, P=0, FMT=15 (application layer feedback)
        p[1] = 206;                          // PT=PSFB
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)(len / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), 0);         // media SSRC unused
        p[12] = (byte)'R'; p[13] = (byte)'E'; p[14] = (byte)'M'; p[15] = (byte)'B';
        p[16] = (byte)mediaSsrcs.Length;
        p[17] = (byte)((exp << 2) | (int)((bps >> 16) & 0x3));
        p[18] = (byte)((bps >> 8) & 0xFF);
        p[19] = (byte)(bps & 0xFF);
        for (int i = 0; i < mediaSsrcs.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(20 + 4 * i), mediaSsrcs[i]);
        return p;
    }

    /// <summary>Decode a REMB bitrate (used by tests and the simulator).</summary>
    public static double ParseRembKbps(ReadOnlySpan<byte> p)
    {
        if (p.Length < 20 || p[1] != 206 || (p[0] & 0x1F) != 15 || p[12] != 'R') throw new FormatException("not REMB");
        int exp = p[17] >> 2;
        ulong mant = ((ulong)(p[17] & 0x3) << 16) | ((ulong)p[18] << 8) | p[19];
        return (mant << exp) / 1000.0;
    }

    /// <summary>Picture Loss Indication: "I lost the picture, send a keyframe".</summary>
    public static byte[] BuildPli(uint senderSsrc, uint mediaSsrc)
    {
        var p = new byte[12];
        p[0] = 0x80 | 1; p[1] = 206;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), mediaSsrc);
        return p;
    }
}
