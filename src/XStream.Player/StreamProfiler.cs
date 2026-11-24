using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace XStream.Player;

/// <summary>
/// What the stream costs this device, once a second: memory allocated (drives garbage collections), GC pauses
/// (each one can delay a frame), process CPU, and how much of the network-to-decoder path is spent in the WebRTC
/// library vs our own code. Used to decide where the receive path needs work (and to compare implementations).
/// </summary>
public sealed class StreamProfiler
{
    private readonly Process _proc = Process.GetCurrentProcess();
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private long _lastAlloc = GC.GetTotalAllocatedBytes();
    private TimeSpan _lastPause = GC.GetTotalPauseDuration();
    private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private int _lastGen0 = GC.CollectionCount(0), _lastGen1 = GC.CollectionCount(1), _lastGen2 = GC.CollectionCount(2);
    private double _lastWall;
    private long _packets, _lastPackets;
    private double _callbackTicks;   // time spent in our per-packet code (Stopwatch ticks)
    private long _ourBytes, _libBytes;

    private readonly List<Sample> _samples = [];
    private AllocationListener? _allocs;

    /// <summary>Also record which types are allocated (sampled by the runtime about every 100 KB); costs a little.</summary>
    public void TrackAllocationTypes() => _allocs ??= new AllocationListener();

    /// <summary>The most-allocated types (share of sampled bytes), or null when not tracking.</summary>
    public string? TopAllocations(int n = 12) => _allocs?.Top(n);

    public readonly record struct Sample(double TimeS, double AllocMBps, double GcPauseMsPerS, int Gen0, int Gen1, int Gen2,
        double CpuPct, double PacketsPerS, double OurUsPerPacket, double OurBytesPerPacket, double LibraryBytesPerPacket);

    /// <summary>Call for each RTP packet handled (receive thread).</summary>
    public void Packet(XStream.WebRtc.XboxPeer.PacketCost c)
    {
        Interlocked.Increment(ref _packets);
        _callbackTicks += c.Ticks; _ourBytes += c.OurBytes;
        // Ignore huge gaps (thread pool thread that did other work in between).
        if (c.LibraryBytes is > 0 and < 1_000_000) _libBytes += c.LibraryBytes;
    }

    /// <summary>Take a once-a-second sample.</summary>
    public Sample Tick()
    {
        double now = _wall.Elapsed.TotalSeconds, dt = Math.Max(0.001, now - _lastWall);
        long alloc = GC.GetTotalAllocatedBytes();
        var pause = GC.GetTotalPauseDuration();
        _proc.Refresh();
        var cpu = _proc.TotalProcessorTime;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long packets = Interlocked.Read(ref _packets);
        long dp = packets - _lastPackets;
        var s = new Sample(now,
            (alloc - _lastAlloc) / dt / 1e6,
            (pause - _lastPause).TotalMilliseconds / dt,
            g0 - _lastGen0, g1 - _lastGen1, g2 - _lastGen2,
            (cpu - _lastCpu).TotalSeconds / dt / Environment.ProcessorCount * 100,
            dp / dt,
            dp > 0 ? _callbackTicks / dp * 1e6 / Stopwatch.Frequency : 0,
            dp > 0 ? (double)_ourBytes / dp : 0,
            dp > 0 ? (double)_libBytes / dp : 0);
        _lastAlloc = alloc; _lastPause = pause; _lastCpu = cpu; _lastGen0 = g0; _lastGen1 = g1; _lastGen2 = g2;
        _lastWall = now; _lastPackets = packets; _callbackTicks = 0; _ourBytes = 0; _libBytes = 0;
        lock (_samples) _samples.Add(s);
        return s;
    }

    /// <summary>One line for the log: averages over the session, worst second for GC.</summary>
    public string Summary()
    {
        List<Sample> s;
        lock (_samples) s = _samples.Skip(3).ToList(); // skip connection setup
        if (s.Count == 0) return "profile: not enough data";
        return $"profile over {s.Count} s: alloc {s.Average(x => x.AllocMBps):F1} MB/s, " +
               $"GC {s.Sum(x => x.Gen0)} gen0 / {s.Sum(x => x.Gen1)} gen1 / {s.Sum(x => x.Gen2)} gen2, " +
               $"pauses avg {s.Average(x => x.GcPauseMsPerS):F2} ms/s (worst second {s.Max(x => x.GcPauseMsPerS):F1} ms), " +
               $"CPU {s.Average(x => x.CpuPct):F1}% of all cores, {s.Average(x => x.PacketsPerS):F0} packets/s, " +
               $"our code {s.Average(x => x.OurUsPerPacket):F1} us/packet; allocated per packet: " +
               $"{s.Average(x => x.AllocMBps * 1e6 / Math.Max(1, x.PacketsPerS)):F0} B in total, " +
               $"{s.Average(x => x.LibraryBytesPerPacket):F0} B WebRTC library receive, {s.Average(x => x.OurBytesPerPacket):F0} B our packet handling";
    }

    private sealed class AllocationListener : EventListener
    {
        private readonly Dictionary<string, long> _bytes = [];
        private long _total;

        protected override void OnEventSourceCreated(EventSource source)
        {
            // GC keyword (0x1) at verbose level includes GCAllocationTick.
            if (source.Name == "Microsoft-Windows-DotNETRuntime") EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName is not { } name || !name.StartsWith("GCAllocationTick") || e.PayloadNames is null) return;
            int ti = e.PayloadNames.IndexOf("TypeName"), ai = e.PayloadNames.IndexOf("AllocationAmount64");
            if (ti < 0) return;
            string type = e.Payload![ti] as string ?? "?";
            long amount = ai >= 0 ? Convert.ToInt64(e.Payload[ai]) : 100_000;
            lock (_bytes) { _bytes[type] = _bytes.GetValueOrDefault(type) + amount; _total += amount; }
        }

        public string Top(int n)
        {
            lock (_bytes)
            {
                if (_total == 0) return "allocations: no samples";
                return "allocations by type: " + string.Join(", ", _bytes.OrderByDescending(kv => kv.Value).Take(n)
                    .Select(kv => $"{kv.Key} {kv.Value * 100.0 / _total:F0}%"));
            }
        }
    }
}
