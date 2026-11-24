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

    public long Requested { get; private set; }
    public long Recovered { get; private set; }
    public long GivenUp { get; private set; }
    /// <summary>Round-trip estimate from NACK/retransmission pairs; NaN until the first repair.</summary>
    public double RttEstimateMs { get; private set; } = double.NaN;

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
            // NACK -> retransmission time is a round-trip sample (a receiver has no other RTT signal).
            double sampleRtt = nowMs - m.LastAsked;
            if (sampleRtt > 0 && sampleRtt < 1000)
                RttEstimateMs = double.IsNaN(RttEstimateMs) ? sampleRtt : RttEstimateMs + (sampleRtt - RttEstimateMs) * 0.2;
        }
    }

    /// <summary>
    /// True if a gap is being tracked (cheap: call on every packet, then <see cref="Poll"/> only when it's true, so a
    /// loss is reported as soon as the reorder wait is over rather than at the next timer tick).
    /// </summary>
    public bool HasGaps => _missing.Count > 0;

    /// <summary>Sequence numbers to request now. Call every 5-20 ms, and on packet arrival while <see cref="HasGaps"/>.</summary>
    public IReadOnlyList<ushort> Poll(double nowMs, double rttMs)
    {
        if (_missing.Count == 0) return [];
        var due = new List<ushort>();
        var drop = new List<long>();
        double retryAfter = Math.Max(20, rttMs * 1.5);
        foreach (var (seq, m) in _missing)
        {
            double age = nowMs - m.FirstSeen;
            if (age > MaxAgeMs || (m.Tries >= MaxTries && nowMs - m.LastAsked > retryAfter)) { drop.Add(seq); continue; }
            if (m.Tries >= MaxTries || age < ReorderWaitMs || nowMs - m.LastAsked < retryAfter) continue;
            m.Tries++;
            m.LastAsked = nowMs;
            Requested++;
            due.Add((ushort)(seq & 0xFFFF));
        }
        foreach (var s in drop) { _missing.Remove(s); GivenUp++; }
        return due;
    }

    /// <summary>Builds one RTCP Generic NACK (RTPFB FMT=1) covering the given sequence numbers.</summary>
    public static byte[] BuildNack(uint senderSsrc, uint mediaSsrc, IReadOnlyList<ushort> seqs)
    {
        // Pack into (PID, BLP) pairs: PID = first lost, BLP bit i = PID+i+1 also lost.
        var pairs = new List<(ushort pid, ushort blp)>();
        foreach (var s in seqs.Distinct().OrderBy(x => x, Comparer<ushort>.Create((a, b) => (short)(a - b))))
        {
            if (pairs.Count > 0)
            {
                var (pid, blp) = pairs[^1];
                int diff = (ushort)(s - pid);
                if (diff >= 1 && diff <= 16) { pairs[^1] = (pid, (ushort)(blp | (1 << (diff - 1)))); continue; }
            }
            pairs.Add((s, 0));
        }
        var p = new byte[12 + 4 * pairs.Count];
        p[0] = 0x80 | 1; p[1] = 205; // RTPFB, FMT 1 = Generic NACK
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)(p.Length / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), senderSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), mediaSsrc);
        for (int i = 0; i < pairs.Count; i++)
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
