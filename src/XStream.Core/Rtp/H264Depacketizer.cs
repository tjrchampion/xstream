namespace XStream.Core.Rtp;

/// <summary>One complete H.264 access unit in Annex-B format, ready for a hardware decoder.</summary>
public sealed record AccessUnit(byte[] Data, uint RtpTimestamp, bool IsKeyframe, double CompletedAtMs);

/// <summary>
/// Reassembles H.264 RTP packets (RFC 6184: single NAL, STAP-A, FU-A) into frames, in order,
/// tolerating reordering and late retransmissions.
///
/// It is also the "decoder truth" for stability: if a frame can't be completed in time it is
/// declared lost, every following frame is held back until the next keyframe (a clean freeze
/// instead of smeared garbage), and <see cref="TakeDecoderError"/> tells the engine to ask for
/// a keyframe right away.
/// </summary>
public sealed class H264Depacketizer
{
    private sealed record Pkt(uint Ts, bool Marker, byte[] Payload, double Arrival);

    private readonly SortedDictionary<long, Pkt> _store = new();
    private bool _started;
    private long _highest;
    private long? _expectedStart;   // first seq of the next frame to emit; null = resyncing
    private long _resyncAfter = long.MinValue;
    private bool _chainOk;
    private bool _decoderError;

    /// <summary>How long to wait for a missing packet (retransmission) before giving up on the frame.</summary>
    public double MaxWaitMs { get; set; } = 120;

    public long FramesDelivered { get; private set; }
    public long FramesLost { get; private set; }
    public long FramesHeldBack { get; private set; }  // complete but undecodable (waiting for keyframe)
    public double LastDeliveredMs { get; private set; } = double.NaN;
    public bool PictureBroken => !_chainOk;

    /// <summary>True once after a frame was lost (consumed by the engine each tick).</summary>
    public bool TakeDecoderError() { bool e = _decoderError; _decoderError = false; return e; }

    public List<AccessUnit> Push(ushort seq, uint ts, bool marker, ReadOnlySpan<byte> payload, double nowMs)
    {
        long ext = Extend(seq);
        if (ext > _highest || !_started) _highest = ext;
        _started = true;
        if (ext > _resyncAfter && !_store.ContainsKey(ext)) _store[ext] = new Pkt(ts, marker, payload.ToArray(), nowMs);
        return Drain(nowMs);
    }

    /// <summary>Call periodically (e.g. every 5 ms) so lost frames are given up on even when no packets arrive.</summary>
    public List<AccessUnit> Tick(double nowMs) => Drain(nowMs);

    private List<AccessUnit> Drain(double now)
    {
        var output = new List<AccessUnit>();
        while (true)
        {
            if (_expectedStart is null && !Resync()) break;
            long s = _expectedStart!.Value;

            // Walk contiguous packets of the next frame.
            long cur = s;
            bool complete = false, gap = false;
            uint ts = 0;
            while (true)
            {
                if (!_store.TryGetValue(cur, out var p)) { gap = true; break; }
                if (cur == s) ts = p.Ts;
                else if (p.Ts != ts) { complete = true; cur--; break; } // marker lost but next frame began: treat as end
                if (p.Marker) { complete = true; break; }
                cur++;
            }

            if (complete && !gap)
            {
                Emit(s, cur, ts, now, output);
                _expectedStart = cur + 1;
                continue;
            }

            // Missing packet at 'cur'. Wait for a retransmission unless newer data has waited too long.
            double oldestAfter = _store.Where(kv => kv.Key > cur).Select(kv => kv.Value.Arrival).DefaultIfEmpty(double.NaN).Min();
            if (double.IsNaN(oldestAfter) || now - oldestAfter < MaxWaitMs) break;

            FramesLost++;
            _chainOk = false;
            _decoderError = true;
            _resyncAfter = cur;
            foreach (var k in _store.Keys.Where(k => k < cur).ToList()) _store.Remove(k);
            _expectedStart = null;
        }
        // Bound memory: anything older than 2 s is useless.
        if (_store.Count > 2000)
            foreach (var k in _store.Where(kv => now - kv.Value.Arrival > 2000).Select(kv => kv.Key).ToList()) _store.Remove(k);
        return output;
    }

    /// <summary>Find the next safe frame start after a loss: right after a marker we hold, or a keyframe start.</summary>
    private bool Resync()
    {
        foreach (var (seq, p) in _store)
        {
            if (seq <= _resyncAfter) continue;
            bool afterMarker = _store.TryGetValue(seq - 1, out var prev) && prev.Marker;
            if (afterMarker || H264.IsKeyframeStart(p.Payload))
            {
                // Frames skipped while resyncing can't be shown either; count them as held back.
                var skipped = _store.Where(kv => kv.Key < seq && kv.Key > _resyncAfter).Select(kv => kv.Value.Ts).Distinct().Count();
                FramesHeldBack += skipped;
                foreach (var k in _store.Keys.Where(k => k < seq).ToList()) _store.Remove(k);
                _expectedStart = seq;
                return true;
            }
        }
        return false;
    }

    private void Emit(long from, long to, uint ts, double now, List<AccessUnit> output)
    {
        using var ms = new MemoryStream();
        bool key = false;
        for (long q = from; q <= to; q++)
        {
            var pl = _store[q].Payload;
            _store.Remove(q);
            if (pl.Length == 0) continue;
            if (q == from && H264.IsKeyframeStart(pl)) key = true;
            AppendNalus(pl, ms, ref key);
        }
        if (key) _chainOk = true;
        if (!_chainOk) { FramesHeldBack++; return; }
        FramesDelivered++;
        LastDeliveredMs = now;
        output.Add(new AccessUnit(ms.ToArray(), ts, key, now));
    }

    private static readonly byte[] StartCode = { 0, 0, 0, 1 };

    private static void AppendNalus(byte[] pl, MemoryStream ms, ref bool key)
    {
        int type = pl[0] & 0x1F;
        if (type is >= 1 and <= 23)
        {
            if (type == 5) key = true;
            ms.Write(StartCode); ms.Write(pl);
        }
        else if (type == 24) // STAP-A
        {
            int i = 1;
            while (i + 2 <= pl.Length)
            {
                int size = (pl[i] << 8) | pl[i + 1];
                i += 2;
                if (size == 0 || i + size > pl.Length) break;
                if ((pl[i] & 0x1F) == 5) key = true;
                ms.Write(StartCode); ms.Write(pl, i, size);
                i += size;
            }
        }
        else if (type == 28 && pl.Length > 2) // FU-A
        {
            bool start = (pl[1] & 0x80) != 0;
            int inner = pl[1] & 0x1F;
            if (start)
            {
                if (inner == 5) key = true;
                ms.Write(StartCode);
                ms.WriteByte((byte)((pl[0] & 0xE0) | inner));
            }
            ms.Write(pl, 2, pl.Length - 2);
        }
    }

    private long Extend(ushort seq)
    {
        if (!_started) return seq + 65536L;
        long c = (_highest & ~0xFFFFL) | seq;
        if (c < _highest - 32768) c += 65536; else if (c > _highest + 32768) c -= 65536;
        return c;
    }
}
