using System.Buffers.Binary;

namespace XStream.Core.Rtp;

/// <summary>
/// Receiver-side packet loss repair (RFC 4585 generic NACK). Tracks sequence gaps and asks the
/// console to resend missing packets: once straight away, again after a round trip if still
/// missing, up to <see cref="MaxTries"/> times, and gives up once a packet is too old to help.
///
/// Browsers do this automatically; SIPSorcery does not, so without it every lost packet breaks
/// the picture until the next keyframe.
/// </summary>
public sealed class NackGenerator
{
    private sealed class Missing { public double FirstSeen; public double LastAsked = double.NegativeInfinity; public int Tries; }

    private readonly SortedDictionary<long, Missing> _missing = new();
    private bool _started;
    private long _highest;

    public int MaxTries { get; init; } = 3;
    /// <summary>A packet older than this can't be displayed in time any more; stop asking.</summary>
    public double MaxAgeMs { get; init; } = 350;
    /// <summary>Wait this long before the first ask, so simple reordering doesn't trigger a NACK.</summary>
    public double ReorderWaitMs { get; init; } = 4;
    /// <summary>Above this many gaps it's an outage, not loss: leave it to keyframes and reconnects.</summary>
    public int MaxTracked { get; init; } = 500;
    /// <summary>
    /// Shortest wait before asking again for a packet whose resend hasn't come. Was 20 ms, which on a 4-5 ms home
    /// link made a lost resend cost ~20 ms instead of ~8 (and every later frame waits behind the missing packet).
    /// </summary>
    public double MinRetryMs { get; init; } = 6;
    /// <summary>
    /// Double the wait before each further try (8, 16, 32 ms on a home link). Loss comes in bursts: tries spaced
    /// evenly and close together all land inside the same burst, spread out the later ones get past it.
    /// </summary>
    public bool Backoff { get; init; }

    public long Requested { get; private set; }
    public long Recovered { get; private set; }
    public long GivenUp { get; private set; }
    /// <summary>Round-trip estimate from NACK/retransmission pairs; NaN until the first repair.</summary>
    public double RttEstimateMs { get; private set; } = double.NaN;
    /// <summary>Packets dropped from tracking because the frame they belonged to was already given up on.</summary>
    public long Abandoned { get; private set; }

    /// <summary>
    /// How long to wait for a resend before asking again: a round trip (the measured one, or the NACK -> resend time
    /// if that is longer, since it includes the console's own queueing) plus margin.
    /// </summary>
    public double RetryAfterMs(double rttMs)
    {
        double resend = double.IsNaN(RttEstimateMs) ? rttMs : Math.Max(rttMs, RttEstimateMs);
        return Math.Max(MinRetryMs, resend * 1.5 + 2);
    }

    /// <summary>When every try for a packet has had its chance: how long a frame should wait for its resends.</summary>
    public double GiveUpAfterMs(double rttMs) => Backoff
        ? ReorderWaitMs + RetryAfterMs(rttMs) * (1 << (MaxTries - 1)) // asks at r, r+b, r+3b...; the last one's answer by r+2^(n-1)b
        : ReorderWaitMs + MaxTries * RetryAfterMs(rttMs);

    private double WaitBeforeTry(int tries, double retryAfter) => Backoff && tries > 1 ? retryAfter * (1 << (tries - 1)) : retryAfter;

    public void OnPacket(ushort seq, double nowMs)
    {
        long ext = Extend(seq);
        if (!_started) { _started = true; _highest = ext; return; }
        if (ext > _highest)
        {
            long gap = ext - _highest - 1;
            if (gap > MaxTracked) _missing.Clear();
            else for (long s = _highest + 1; s < ext; s++) _missing[s] = new Missing { FirstSeen = nowMs };
            _highest = ext;
        }
        else if (_missing.Remove(ext, out var m) && m.Tries > 0)
        {
            Recovered++;
            // NACK -> retransmission time is a round-trip sample. Only from packets asked for once: after a retry we
            // can't tell which ask the resend answers, and timing it from the last ask would read short, which would
            // make retries come sooner and read shorter still.
            double sampleRtt = nowMs - m.LastAsked;
            if (m.Tries == 1 && sampleRtt > 0 && sampleRtt < 1000)
                RttEstimateMs = double.IsNaN(RttEstimateMs) ? sampleRtt : RttEstimateMs + (sampleRtt - RttEstimateMs) * 0.2;
        }
    }

    /// <summary>
    /// True if a gap is being tracked (cheap: call on every packet, then <see cref="Poll"/> only when it's true, so a
    /// loss is reported as soon as the reorder wait is over rather than at the next timer tick).
    /// </summary>
    public bool HasGaps => _missing.Count > 0;

    /// <summary>Sequence numbers to request now. Call every 5-20 ms, and on packet arrival while <see cref="HasGaps"/>.</summary>
    /// <remarks>The list is reused: use it (e.g. <see cref="BuildNack"/>) before the next call.</remarks>
    public IReadOnlyList<ushort> Poll(double nowMs, double rttMs)
    {
        var due = _due; due.Clear();
        if (_missing.Count == 0) return due;
        var drop = _drop; drop.Clear();
        double retryAfter = RetryAfterMs(rttMs);
        foreach (var (seq, m) in _missing)
        {
            double age = nowMs - m.FirstSeen;
            if (age > MaxAgeMs || (m.Tries >= MaxTries && nowMs - m.LastAsked > retryAfter)) { drop.Add(seq); continue; }
            if (m.Tries >= MaxTries || age < ReorderWaitMs || nowMs - m.LastAsked < WaitBeforeTry(m.Tries, retryAfter)) continue;
            m.Tries++;
            m.LastAsked = nowMs;
            Requested++;
            due.Add((ushort)(seq & 0xFFFF));
        }
        foreach (var s in drop) { _missing.Remove(s); GivenUp++; }
        return due;
    }

    private readonly List<ushort> _due = [];
    private readonly List<long> _drop = [];

    /// <summary>
    /// The frame assembler gave up on everything up to and including <paramref name="seq"/>: stop asking for those.
    /// Resends of them would be thrown away on arrival, and in a loss burst they compete for the link with the
    /// keyframe that was just asked for.
    /// </summary>
    public void ForgetThrough(ushort seq)
    {
        if (!_started || _missing.Count == 0) return;
        long ext = Extend(seq);
        var drop = _drop; drop.Clear();
        foreach (var k in _missing.Keys) { if (k > ext) break; drop.Add(k); }
        foreach (var k in drop) _missing.Remove(k);
        Abandoned += drop.Count;
        drop.Clear();
    }

    /// <summary>Builds one RTCP Generic NACK (RTPFB FMT=1) covering the given sequence numbers.</summary>
    public static byte[] BuildNack(uint senderSsrc, uint mediaSsrc, IReadOnlyList<ushort> seqs)
    {
        // Pack into (PID, BLP) pairs: PID = first lost, BLP bit i = PID+i+1 also lost.
        // Sorted in wrap-around order (65535 before 0). No LINQ: this runs on the receive thread during loss.
        Span<ushort> sorted = seqs.Count <= 256 ? stackalloc ushort[seqs.Count] : new ushort[seqs.Count];
        for (int i = 0; i < seqs.Count; i++) sorted[i] = seqs[i];
        sorted.Sort(static (a, b) => (short)(a - b));
        Span<(ushort pid, ushort blp)> pairs = sorted.Length <= 256 ? stackalloc (ushort, ushort)[sorted.Length] : new (ushort, ushort)[sorted.Length];
        int count = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            ushort s = sorted[i];
            if (i > 0 && s == sorted[i - 1]) continue; // duplicate
            if (count > 0)
            {
                var (pid, blp) = pairs[count - 1];
                int diff = (ushort)(s - pid);
                if (diff >= 1 && diff <= 16) { pairs[count - 1] = (pid, (ushort)(blp | (1 << (diff - 1)))); continue; }
            }
            pairs[count++] = (s, 0);
        }
        pairs = pairs[..count];
        var p = new byte[12 + 4 * pairs.Length];
        p[0] = 0x80 | 1; p[1] = 205; // RTPFB, FMT 1 = Generic NACK
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)(p.Length / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), mediaSsrc);
        for (int i = 0; i < pairs.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(12 + 4 * i), pairs[i].pid);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(14 + 4 * i), pairs[i].blp);
        }
        return p;
    }

    /// <summary>Expands a Generic NACK back into sequence numbers (tests and the simulator).</summary>
    public static List<ushort> ParseNack(ReadOnlySpan<byte> p)
    {
        var list = new List<ushort>();
        for (int o = 12; o + 4 <= p.Length; o += 4)
        {
            ushort pid = BinaryPrimitives.ReadUInt16BigEndian(p[o..]), blp = BinaryPrimitives.ReadUInt16BigEndian(p[(o + 2)..]);
            list.Add(pid);
            for (int b = 0; b < 16; b++) if ((blp & (1 << b)) != 0) list.Add((ushort)(pid + b + 1));
        }
        return list;
    }

    private long Extend(ushort seq)
    {
        if (!_started) return seq + 65536L;
        long c = (_highest & ~0xFFFFL) | seq;
        if (c < _highest - 32768) c += 65536; else if (c > _highest + 32768) c -= 65536;
        return c;
    }
}
