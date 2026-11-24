using System.Buffers;

namespace XStream.Core.Rtp;

/// <summary>One complete H.264 access unit in Annex-B format, ready for a hardware decoder.</summary>
public sealed class AccessUnit(byte[] buffer, int length, uint rtpTimestamp, bool isKeyframe, double completedAtMs, bool pooled = false)
{
    private byte[]? _buffer = buffer;
    private readonly bool _pooled = pooled;

    public AccessUnit(byte[] data, uint rtpTimestamp, bool isKeyframe, double completedAtMs)
        : this(data, data.Length, rtpTimestamp, isKeyframe, completedAtMs) { }

    /// <summary>The frame's bytes (empty once <see cref="Release"/> has been called).</summary>
    public ReadOnlySpan<byte> Data => _buffer is { } b ? b.AsSpan(0, Length) : default;
    public int Length { get; } = length;
    public uint RtpTimestamp { get; } = rtpTimestamp;
    public bool IsKeyframe { get; } = isKeyframe;
    public double CompletedAtMs { get; } = completedAtMs;

    /// <summary>
    /// Done with the bytes (the decoder has copied them): a pooled buffer goes back for the next frame. Frames are
    /// often over 85 KB at 1440p, and fresh arrays that size land on the large-object heap, whose clean-up is a full
    /// (slow) garbage collection. A frame that's dropped without being released is simply collected as usual.
    /// </summary>
    public void Release()
    {
        var b = Interlocked.Exchange(ref _buffer, null);
        if (b is not null && _pooled) System.Buffers.ArrayPool<byte>.Shared.Return(b);
    }
}

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
    // Payloads live in pooled buffers (returned when the packet leaves the store), so steady-state streaming
    // allocates one exact-size array per frame and little else: fewer garbage collections, fewer frame hitches.
    private readonly record struct Pkt(uint Ts, bool Marker, byte[] Buf, int Len, double Arrival)
    {
        public ReadOnlySpan<byte> Payload => Buf.AsSpan(0, Len);
    }

    private static readonly AccessUnit[] None = [];
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

    public IReadOnlyList<AccessUnit> Push(ushort seq, uint ts, bool marker, ReadOnlySpan<byte> payload, double nowMs)
    {
        long ext = Extend(seq);
        if (ext > _highest || !_started) _highest = ext;
        _started = true;
        if (ext > _resyncAfter && !_store.ContainsKey(ext))
        {
            var buf = ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length));
            payload.CopyTo(buf);
            _store[ext] = new Pkt(ts, marker, buf, payload.Length, nowMs);
        }
        return Drain(nowMs);
    }

    /// <summary>Call periodically (e.g. every 5 ms) so lost frames are given up on even when no packets arrive.</summary>
    public IReadOnlyList<AccessUnit> Tick(double nowMs) => Drain(nowMs);

    private void Remove(long seq)
    {
        if (_store.Remove(seq, out var p)) ArrayPool<byte>.Shared.Return(p.Buf);
    }

    // Explicit loops rather than a predicate: a lambda capturing a local allocates on every call, even when unused.
    private readonly List<long> _doomed = [];

    private void RemoveBefore(long seq)
    {
        foreach (var k in _store.Keys) { if (k >= seq) break; _doomed.Add(k); }
        foreach (var k in _doomed) Remove(k);
        _doomed.Clear();
    }

    private void RemoveOlderThan(double arrivalMs)
    {
        foreach (var (k, v) in _store) if (v.Arrival < arrivalMs) _doomed.Add(k);
        foreach (var k in _doomed) Remove(k);
        _doomed.Clear();
    }

    private IReadOnlyList<AccessUnit> Drain(double now)
    {
        List<AccessUnit>? output = null;
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
                Emit(s, cur, ts, now, output ??= []);
                _expectedStart = cur + 1;
                continue;
            }

            // The frame simply isn't finished yet (nothing newer has arrived): the usual case, mid-frame.
            if (cur > _highest) break;

            // Missing packet at 'cur' with newer ones behind it. Wait for a retransmission unless they've waited too long.
            double oldestAfter = double.NaN;
            foreach (var (k, v) in _store)
                if (k > cur && !(v.Arrival >= oldestAfter)) oldestAfter = v.Arrival;
            if (double.IsNaN(oldestAfter) || now - oldestAfter < MaxWaitMs) break;

            FramesLost++;
            _chainOk = false;
            _decoderError = true;
            _resyncAfter = cur;
            RemoveBefore(cur);
            _expectedStart = null;
        }
        // Bound memory: anything older than 2 s is useless.
        if (_store.Count > 2000) RemoveOlderThan(now - 2000);
        return output ?? (IReadOnlyList<AccessUnit>)None;
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
                // (frames = distinct timestamps; packets of a frame are contiguous, so count timestamp changes)
                int skipped = 0; uint? lastTs = null;
                foreach (var (k, v) in _store)
                {
                    if (k >= seq) break;
                    if (k > _resyncAfter && v.Ts != lastTs) { skipped++; lastTs = v.Ts; }
                }
                FramesHeldBack += skipped;
                RemoveBefore(seq);
                _expectedStart = seq;
                return true;
            }
        }
        return false;
    }

    private void Emit(long from, long to, uint ts, double now, List<AccessUnit> output)
    {
        // Two passes: measure, then write into one exact-size array (no growing/copying buffers).
        bool key = false;
        int size = 0;
        for (long q = from; q <= to; q++)
        {
            var pl = _store[q].Payload;
            if (q == from && H264.IsKeyframeStart(pl)) key = true;
            size += AppendNalus(pl, default, measure: true, ref key);
        }
        var data = ArrayPool<byte>.Shared.Rent(Math.Max(1, size));
        var dst = data.AsSpan(0, size);
        for (long q = from; q <= to; q++)
        {
            int n = AppendNalus(_store[q].Payload, dst, measure: false, ref key);
            dst = dst[n..];
            Remove(q);
        }
        if (key) _chainOk = true;
        if (!_chainOk) { FramesHeldBack++; ArrayPool<byte>.Shared.Return(data); return; }
        FramesDelivered++;
        LastDeliveredMs = now;
        output.Add(new AccessUnit(data, size, ts, key, now, pooled: true));
    }

    private static readonly byte[] StartCode = { 0, 0, 0, 1 };

    /// <summary>
    /// The Annex-B bytes for one RTP payload: writes them to <paramref name="dst"/> (unless only measuring) and
    /// returns how many there are.
    /// </summary>
    private static int AppendNalus(ReadOnlySpan<byte> pl, Span<byte> dst, bool measure, ref bool key)
    {
        if (pl.Length == 0) return 0;
        int w = 0;
        void Put(ReadOnlySpan<byte> src, Span<byte> d, ref int at) { if (!measure) src.CopyTo(d[at..]); at += src.Length; }
        int type = pl[0] & 0x1F;
        if (type is >= 1 and <= 23)
        {
            if (type == 5) key = true;
            Put(StartCode, dst, ref w); Put(pl, dst, ref w);
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
                Put(StartCode, dst, ref w); Put(pl.Slice(i, size), dst, ref w);
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
                Put(StartCode, dst, ref w);
                Span<byte> hdr = [(byte)((pl[0] & 0xE0) | inner)];
                Put(hdr, dst, ref w);
            }
            Put(pl[2..], dst, ref w);
        }
        return w;
    }

    private long Extend(ushort seq)
    {
        if (!_started) return seq + 65536L;
        long c = (_highest & ~0xFFFFL) | seq;
        if (c < _highest - 32768) c += 65536; else if (c > _highest + 32768) c -= 65536;
        return c;
    }
}
