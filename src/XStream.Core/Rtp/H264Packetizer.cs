namespace XStream.Core.Rtp;

/// <summary>One RTP packet's worth of H.264 payload.</summary>
public readonly record struct RtpPayload(ushort Seq, uint Timestamp, bool Marker, byte[] Payload);

/// <summary>
/// Turns an Annex-B H.264 stream (a .h264 file) into RTP payloads (RFC 6184: single NAL units and
/// FU-A fragments), one access unit per timestamp. Used by `xstream video-test` and the tests to
/// drive the real receive path (frame assembler -> decoder -> window) without a console.
/// </summary>
public static class H264Packetizer
{
    /// <summary>Split an Annex-B stream into access units (lists of NAL units without start codes).</summary>
    public static List<List<byte[]>> AccessUnits(byte[] annexB)
    {
        var nals = new List<byte[]>();
        int i = 0, start = -1;
        while (i + 3 <= annexB.Length)
        {
            int sc = annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1 ? 3
                   : i + 4 <= annexB.Length && annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1 ? 4 : 0;
            if (sc > 0)
            {
                if (start >= 0) nals.Add(annexB[start..i]);
                i += sc;
                start = i;
            }
            else i++;
        }
        if (start >= 0 && start < annexB.Length) nals.Add(annexB[start..]);

        var aus = new List<List<byte[]>>();
        var cur = new List<byte[]>();
        bool curHasSlice = false;
        foreach (var n in nals.Where(n => n.Length > 0))
        {
            int type = n[0] & 0x1F;
            bool isSlice = type is 1 or 5;
            bool firstMbZero = isSlice && n.Length > 1 && (n[1] & 0x80) != 0; // first_mb_in_slice ue(v) == 0
            bool startsNew = curHasSlice && (type is 9 or 7 or 8 or 6 || firstMbZero);
            if (startsNew) { aus.Add(cur); cur = new List<byte[]>(); curHasSlice = false; }
            cur.Add(n);
            curHasSlice |= isSlice;
        }
        if (cur.Count > 0) aus.Add(cur);
        return aus;
    }

    /// <summary>Packetize access units; <paramref name="mtu"/> is the largest payload per packet.</summary>
    public static IEnumerable<RtpPayload> Packetize(IEnumerable<List<byte[]>> accessUnits, int fps = 60, int mtu = 1200, ushort firstSeq = 1)
    {
        ushort seq = firstSeq;
        uint ts = 0, step = (uint)(90000 / fps);
        foreach (var au in accessUnits)
        {
            for (int k = 0; k < au.Count; k++)
            {
                var nal = au[k];
                bool lastNal = k == au.Count - 1;
                if (nal.Length <= mtu)
                {
                    yield return new RtpPayload(seq++, ts, lastNal, nal);
                    continue;
                }
                byte indicator = (byte)((nal[0] & 0xE0) | 28), type = (byte)(nal[0] & 0x1F);
                int pos = 1;
                while (pos < nal.Length)
                {
                    int len = Math.Min(mtu - 2, nal.Length - pos);
                    bool first = pos == 1, last = pos + len >= nal.Length;
                    var p = new byte[len + 2];
                    p[0] = indicator;
                    p[1] = (byte)((first ? 0x80 : 0) | (last ? 0x40 : 0) | type);
                    Buffer.BlockCopy(nal, pos, p, 2, len);
                    yield return new RtpPayload(seq++, ts, last && lastNal, p);
                    pos += len;
                }
            }
            ts += step;
        }
    }
}
