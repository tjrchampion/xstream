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
/// a keyframe right away. A lost frame that no other frame refers to (nal_ref_idc 0) is skipped
/// instead: the picture carries on.
/// </summary>
public sealed class H264Depacketizer
{
    // Payloads live in pooled buffers (returned when the packet leaves the store), so steady-state streaming
    // allocates nothing per packet.
    private readonly record struct Pkt(uint Ts, bool Marker, byte[] Buf, int Len, double Arrival)
    {
        public ReadOnlySpan<byte> Payload => Buf.AsSpan(0, Len);
    }

    private static readonly AccessUnit[] None = [];
    private readonly PacketRing _store = new();
    private bool _started;
    private long _highest;
    private long? _expectedStart;   // first seq of the next frame to emit; null = resyncing
    private long _resyncAfter = long.MinValue;
    private bool _chainOk;
    private bool _decoderError;
    private bool _keyframeDelivered;
    private long _abandonedThrough = long.MinValue;
    private bool _abandonedNew;
    // Arrival of the oldest packet held behind the gap at _gapAt (worked out once per gap: it can't change while the
    // gap stays put, because anything arriving later arrives later). Was a full scan of the store on every packet.
    private long _gapAt = long.MinValue;
    private double _gapOldestMs;

    /// <summary>How long to wait for a missing packet (retransmission) before giving up on the frame.</summary>
    public double MaxWaitMs { get; set; } = 120;

    public long FramesDelivered { get; private set; }
    public long FramesLost { get; private set; }
    public long FramesHeldBack { get; private set; }  // complete but undecodable (waiting for keyframe)
    /// <summary>Lost frames that nothing refers to (nal_ref_idc 0): skipped without freezing the picture.</summary>
    public long FramesSkipped { get; private set; }
    public double LastDeliveredMs { get; private set; } = double.NaN;
    public bool PictureBroken => !_chainOk;

    /// <summary>True once after a frame was lost (consumed by the engine each tick).</summary>
    public bool TakeDecoderError() { bool e = _decoderError; _decoderError = false; return e; }

    /// <summary>True once after a complete keyframe went to the decoder (the picture is whole again).</summary>
    public bool TakeKeyframeDelivered() { bool k = _keyframeDelivered; _keyframeDelivered = false; return k; }

    /// <summary>
    /// True once after packets were given up on: every sequence number up to <paramref name="seqThrough"/> is no use
    /// any more (a resend of one would be thrown away), so stop asking for them.
    /// </summary>
    public bool TakeAbandoned(out ushort seqThrough)
    {
        seqThrough = (ushort)(_abandonedThrough & 0xFFFF);
        bool n = _abandonedNew; _abandonedNew = false; return n;
    }

    private void Abandon(long through)
    {
        if (through <= _abandonedThrough) return;
        _abandonedThrough = through;
        _abandonedNew = true;
    }

    public IReadOnlyList<AccessUnit> Push(ushort seq, uint ts, bool marker, ReadOnlySpan<byte> payload, double nowMs)
    {
        long ext = Extend(seq);
        if (ext > _highest || !_started) _highest = ext;
        _started = true;
        // Not: packets of frames already handed on or given up on (a late duplicate resend), or already held.
        if (ext > _resyncAfter && !(ext < _expectedStart) && !_store.Contains(ext) && _store.CanStore(ext))
        {
            var buf = ArrayPool<byte>.Shared.Rent(Math.Max(1, payload.Length));
            payload.CopyTo(buf);
            _store.Put(ext, new Pkt(ts, marker, buf, payload.Length, nowMs));
        }
        return Drain(nowMs);
    }

    /// <summary>Call periodically (e.g. every 5 ms) so lost frames are given up on even when no packets arrive.</summary>
    public IReadOnlyList<AccessUnit> Tick(double nowMs) => Drain(nowMs);

    private void RemoveBefore(long seq) => _store.RemoveBefore(seq);

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
                if (!_store.TryGet(cur, out var p)) { gap = true; break; }
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
            if (cur != _gapAt || double.IsNaN(_gapOldestMs))
            {
                _gapAt = cur;
                _gapOldestMs = _store.OldestArrivalAfter(cur);
            }
            if (double.IsNaN(_gapOldestMs) || now - _gapOldestMs < MaxWaitMs) break;

            if (TrySkipDisposable(s, cur, out long next))
            {
                // Nothing refers to the lost frame: drop it and carry on with the next one. The picture stays whole.
                FramesSkipped++;
                RemoveBefore(next);
                Abandon(next - 1);
                _expectedStart = next;
                continue;
            }

            FramesLost++;
            _chainOk = false;
            _decoderError = true;
            _resyncAfter = cur;
            RemoveBefore(cur);
            Abandon(cur);
            _expectedStart = null;
        }
        // Bound memory: anything older than 2 s is useless.
        if (_store.Count > 2000) { _store.RemoveOlderThan(now - 2000); _gapAt = long.MinValue; }
        return output ?? (IReadOnlyList<AccessUnit>)None;
    }

    /// <summary>
    /// The frame starting at <paramref name="s"/> is missing packet <paramref name="cur"/>. If every packet of it we
    /// hold says nal_ref_idc 0 (no later frame refers to it) and the next frame's start is known for sure (its first
    /// packet directly follows one of this frame's), the frame can be skipped: <paramref name="next"/> is that start.
    /// Not when the frame's first packet is the lost one (we can't tell which frame the gap belongs to).
    /// </summary>
    private bool TrySkipDisposable(long s, long cur, out long next)
    {
        next = 0;
        if (cur == s || !_store.TryGet(s, out var first)) return false;
        uint ts = first.Ts;
        bool prevIsOurs = false, sawSlice = false;
        for (long k = s; k <= _store.Highest; k++)
        {
            if (!_store.TryGet(k, out var p)) { prevIsOurs = false; continue; }
            if (p.Ts != ts)
            {
                if (!prevIsOurs || !sawSlice) return false; // the gap may hold the next frame's first packet
                next = k;
                return true;
            }
            switch (NonReference(p.Payload))
            {
                case null: return false;        // carries reference data (or can't tell)
                case true: sawSlice = true; break;
            }
            prevIsOurs = true;
            if (p.Marker)
            {
                if (!sawSlice) return false;
                next = k + 1; // the frame's last packet is here, so the next frame starts right after it
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// For one RTP payload: null if any NAL unit in it has nal_ref_idc != 0 (other frames may refer to it), true if it
    /// holds a non-reference slice, false if it only holds other non-reference units (AUD, SEI: they say nothing
    /// about the frame).
    /// </summary>
    private static bool? NonReference(ReadOnlySpan<byte> pl)
    {
        if (pl.Length == 0) return null;
        if ((pl[0] & 0x60) != 0) return null; // NRI of the unit (FU-A/STAP-A: the highest of what they carry)
        int type = pl[0] & 0x1F;
        if (type is >= 1 and <= 5) return true;
        if (type == 28) return pl.Length > 1 && (pl[1] & 0x1F) is >= 1 and <= 5;
        if (type == 24)
        {
            bool slice = false;
            for (int i = 1; i + 2 < pl.Length;)
            {
                int size = (pl[i] << 8) | pl[i + 1];
                if (size == 0 || i + 2 + size > pl.Length) return null;
                byte h = pl[i + 2];
                if ((h & 0x60) != 0) return null;
                if ((h & 0x1F) is >= 1 and <= 5) slice = true;
                i += 2 + size;
            }
            return slice;
        }
        return false;
    }

    /// <summary>Find the next safe frame start after a loss: right after a marker we hold, or a keyframe start.</summary>
    private bool Resync()
    {
        if (_store.Count == 0) return false;
        for (long seq = Math.Max(_store.Lowest, _resyncAfter + 1); seq <= _store.Highest; seq++)
        {
            if (!_store.TryGet(seq, out var p)) continue;
            bool afterMarker = _store.TryGet(seq - 1, out var prev) && prev.Marker;
            if (afterMarker || H264.IsKeyframeStart(p.Payload))
            {
                // Frames skipped while resyncing can't be shown either; count them as held back.
                // (frames = distinct timestamps; packets of a frame are contiguous, so count timestamp changes)
                int skipped = 0; uint? lastTs = null;
                for (long k = Math.Max(_store.Lowest, _resyncAfter + 1); k < seq; k++)
                    if (_store.TryGet(k, out var v) && v.Ts != lastTs) { skipped++; lastTs = v.Ts; }
                FramesHeldBack += skipped;
                RemoveBefore(seq);
                if (_resyncAfter != long.MinValue) Abandon(seq - 1); // after a loss (not the stream's first frame)
                _expectedStart = seq;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Packets by extended sequence number in a fixed ring (slot = seq mod size): O(1) lookups and no per-packet
    /// allocation (a SortedDictionary allocated a node per packet). 8192 slots is ~3 s at 1440p; anything that far
    /// behind the newest packet is useless and is dropped.
    /// </summary>
    private sealed class PacketRing
    {
        private const int Size = 8192, Mask = Size - 1;
        private readonly long[] _seq = new long[Size];
        private readonly Pkt[] _pkt = new Pkt[Size];
        private long _lo, _hi; // stored packets are within [_lo, _hi] (valid while Count > 0)

        public PacketRing() => Array.Fill(_seq, long.MinValue);

        public int Count { get; private set; }
        public long Lowest => _lo;
        public long Highest => Count > 0 ? _hi : long.MinValue;

        public bool Contains(long seq) => _seq[(int)(seq & Mask)] == seq;

        public bool TryGet(long seq, out Pkt p)
        {
            int i = (int)(seq & Mask);
            if (_seq[i] == seq) { p = _pkt[i]; return true; }
            p = default;
            return false;
        }

        /// <summary>False if the packet is too far behind the newest one to keep.</summary>
        public bool CanStore(long seq) => Count == 0 || seq > _hi - Size;

        public void Put(long seq, Pkt p)
        {
            if (Count == 0) _lo = _hi = seq;
            else if (seq >= _hi + Size) { Clear(); _lo = _hi = seq; }
            else if (seq > _hi)
            {
                // Make room: whatever would share a slot with the new range is too old to matter.
                long floor = seq - Size + 1;
                for (; _lo < floor && Count > 0; _lo++) Evict(_lo);
                if (Count == 0) _lo = seq;
                _hi = seq;
            }
            else if (seq < _lo) _lo = seq;
            int i = (int)(seq & Mask);
            _seq[i] = seq;
            _pkt[i] = p;
            Count++;
        }

        public void Remove(long seq)
        {
            if (!Evict(seq)) return;
            if (Count == 0) return;
            if (seq == _lo) while (_lo < _hi && !Contains(_lo)) _lo++;
        }

        private bool Evict(long seq)
        {
            int i = (int)(seq & Mask);
            if (_seq[i] != seq) return false;
            ArrayPool<byte>.Shared.Return(_pkt[i].Buf);
            _seq[i] = long.MinValue;
            _pkt[i] = default;
            Count--;
            return true;
        }

        public void RemoveBefore(long seq)
        {
            if (Count == 0) return;
            long end = Math.Min(seq - 1, _hi);
            for (long k = _lo; k <= end && Count > 0; k++) Evict(k);
            if (Count > 0) { _lo = Math.Max(_lo, seq); while (_lo < _hi && !Contains(_lo)) _lo++; }
        }

        public void RemoveOlderThan(double arrivalMs)
        {
            if (Count == 0) return;
            for (long k = _lo; k <= _hi && Count > 0; k++)
                if (TryGet(k, out var p) && p.Arrival < arrivalMs) Evict(k);
            if (Count > 0) while (_lo < _hi && !Contains(_lo)) _lo++;
        }

        /// <summary>Earliest arrival among packets after <paramref name="seq"/> (NaN if there are none).</summary>
        public double OldestArrivalAfter(long seq)
        {
            double oldest = double.NaN;
            if (Count == 0) return oldest;
            for (long k = Math.Max(seq + 1, _lo); k <= _hi; k++)
                if (TryGet(k, out var p) && !(p.Arrival >= oldest)) oldest = p.Arrival;
            return oldest;
        }

        private void Clear()
        {
            for (long k = _lo; k <= _hi && Count > 0; k++) Evict(k);
            Count = 0;
        }
    }

    private void Emit(long from, long to, uint ts, double now, List<AccessUnit> output)
    {
        // Two passes: measure, then write into one exact-size array (no growing/copying buffers).
        bool key = false;
        int size = 0;
        for (long q = from; q <= to; q++)
        {
            _store.TryGet(q, out var pk);
            var pl = pk.Payload;
            if (q == from && H264.IsKeyframeStart(pl)) key = true;
            size += AppendNalus(pl, default, measure: true, ref key);
        }
        var data = ArrayPool<byte>.Shared.Rent(Math.Max(1, size));
        var dst = data.AsSpan(0, size);
        for (long q = from; q <= to; q++)
        {
            _store.TryGet(q, out var pk);
            int n = AppendNalus(pk.Payload, dst, measure: false, ref key);
            dst = dst[n..];
            _store.Remove(q);
        }
        if (key) _chainOk = true;
        if (!_chainOk) { FramesHeldBack++; ArrayPool<byte>.Shared.Return(data); return; }
        FramesDelivered++;
        if (key) _keyframeDelivered = true;
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
