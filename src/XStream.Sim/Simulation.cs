using XStream.Core.Rtp;
using XStream.Core.Stability;

namespace XStream.Sim;

/// <summary>Receiver-side logic under test: ours (StabilityEngine) or the baseline.</summary>
public interface IReceiverBrain
{
    string Name { get; }
    StabilityDecision Update(in NetworkSample s, bool decoderError);
    /// <summary>True if this receiver never restarts ICE itself (waits for the browser to give up).</summary>
    bool Passive { get; }
}

public sealed class EngineBrain(StabilityOptions o) : IReceiverBrain
{
    private readonly StabilityEngine _e = new(o);
    public string Name => "XStream " + o.Profile;
    public bool Passive => false;
    public StabilityDecision Update(in NetworkSample s, bool decoderError) => _e.Update(s, decoderError);
}

/// <summary>
/// Simplified model of a standard browser WebRTC receiver as used by web-based Xbox players:
/// GCC-style delay-based REMB (8%/s ramp, cut to 85% of incoming on overuse), a jitter buffer
/// sized from current jitter, keyframe request every 5 s plus a rate-limited PLI on decode errors,
/// and no ICE restart (a path change ends in "connection lost").
/// </summary>
public sealed class BaselineBrain : IReceiverBrain
{
    private double _cap = 10_000, _holdUntil, _lastPeriodic, _lastPli = double.NegativeInfinity, _last = double.NaN;
    public string Name => "Standard WebRTC receiver";
    public bool Passive => true;

    public StabilityDecision Update(in NetworkSample s, bool decoderError)
    {
        double dt = double.IsNaN(_last) ? 100 : s.TimeMs - _last;
        _last = s.TimeMs;
        bool overuse = s.DelayTrendMs > 1.0 && s.QueueDelayMs > 12;
        if (s.ReceiveKbps > 0)
        {
            if (overuse && s.TimeMs >= _holdUntil) { _cap = Math.Max(1_000, 0.85 * s.ReceiveKbps); _holdUntil = s.TimeMs + 1_000; }
            else if (!overuse && s.TimeMs >= _holdUntil)
                _cap = Math.Min(Math.Min(20_000, 1.5 * s.ReceiveKbps + 1_000), _cap * (1 + 0.08 * dt / 1000));
        }
        bool key = false;
        if (s.TimeMs - _lastPeriodic >= 5_000) { key = true; _lastPeriodic = s.TimeMs; }
        if (decoderError && s.TimeMs - _lastPli >= 1_000) { key = true; _lastPli = s.TimeMs; }
        double buffer = Math.Clamp(s.JitterMs * 3, 0, 50);
        return new StabilityDecision(_cap, true, key, buffer, RecoveryAction.None, overuse ? "overuse" : "normal");
    }
}

public sealed record TimelinePoint(double T, double CapacityKbps, double CapKbps, double DeliveredKbps, double LatencyMs, double FrozenMs, double BufferMs);

public sealed record RunResult(
    string Scenario, string Receiver, int Seed, bool CapsHonoured,
    double FrozenSec, int Freezes, int Stutters, double AvgKbps, double QualitySwing,
    double AvgLatencyMs, double P95LatencyMs, int SessionDrops, int KeyframeRequests,
    List<TimelinePoint> Timeline)
{
    /// <summary>Seconds where the cap sat under 75% of what the link could carry (capped at the sender's 20 Mbps
    /// maximum): picture softer than it needed to be. Outages don't count.</summary>
    public double LowQualitySec => Timeline.Count(p => p.CapacityKbps > 0 && p.CapKbps < 0.75 * Math.Min(p.CapacityKbps, 20_000)) * 0.5;
}

/// <summary>
/// Millisecond-step simulation of console -> network -> player. The console (sender) model is the
/// same for every receiver: a loss-based encoder rate controller that also obeys REMB caps
/// (unless <c>capsHonoured</c> is false), keyframes on request, 60 fps, 1200-byte packets, one
/// NACK retransmission. Only the receiver brain differs between runs.
/// </summary>
public sealed class Simulation(Scenario sc, IReceiverBrain brain, int seed, bool capsHonoured)
{
    private const double FrameMs = 1000.0 / 60, MaxQueueMs = 200, TickMs = 100;

    public RunResult Run()
    {
        // Stable per-scenario salt: string.GetHashCode() is randomised per process, which made runs unrepeatable.
        var rng = new Random(seed * 7919 + sc.Id.Aggregate(17, (h, c) => h * 31 + c) % 1000);
        var monitor = new RtpReceiveMonitor();
        var arrivals = new PriorityQueue<(ushort seq, uint ts, bool marker, int bytes, bool key, double at), double>();
        var feedback = new PriorityQueue<Action, double>();
        var decoderErrors = new Queue<double>();
        var displays = new List<(double at, double latency, double bits)>();
        var timeline = new List<TimelinePoint>();

        double busyUntil = 0, lastArrival = 0;
        double lossBased = 10_000, rembCap = double.PositiveInfinity, rembAt = double.NegativeInfinity;
        bool keyPending = true; double lastKeyAt = double.NegativeInfinity;
        bool chainOk = false; double lastDisplay = 0;
        ushort seq = 0; double nextFrame = 0; double complexity = 1;
        int sentWin = 0, lostWin = 0, keyRequests = 0, sessionDrops = 0;
        bool pathAlive = true; double pathRestoreAt = double.PositiveInfinity; int nextPathChange = 0;
        StabilityDecision d = new(10_000, false, false, 0, RecoveryAction.None, "");

        for (double t = 0; t < sc.DurationMs; t += 1)
        {
            var link = sc.At(t);
            double owd = sc.BaseOwdMs;

            // Path changes (Wi-Fi roam / IP change)
            if (nextPathChange < sc.PathChanges.Count && t >= sc.PathChanges[nextPathChange])
            {
                nextPathChange++;
                pathAlive = false;
                // Passive receivers wait for ICE to fail (~30 s), then the user relaunches the stream (~15 s).
                pathRestoreAt = t + 45_000;
                if (brain.Passive) sessionDrops++;
            }
            if (!pathAlive && t >= pathRestoreAt) { pathAlive = true; pathRestoreAt = double.PositiveInfinity; keyPending = true; }
            bool connected = pathAlive && !link.Outage;

            while (feedback.TryPeek(out _, out var at) && at <= t) feedback.Dequeue()();

            // ---- Sender: one frame every 16.7 ms
            if (t >= nextFrame)
            {
                bool rembValid = capsHonoured && t - rembAt < 3_000;
                double target = Math.Clamp(rembValid ? Math.Min(lossBased, rembCap) : lossBased, 1_000, 20_000);
                bool isKey = keyPending && t - lastKeyAt > 100;
                if (isKey) { keyPending = false; lastKeyAt = t; }
                complexity = Math.Clamp(complexity + (rng.NextDouble() - 0.5) * 0.08, 0.75, 1.25);
                double bytes = target * 1000 / 8 / 60 * complexity * (isKey ? 4 : 1);
                int n = Math.Max(1, (int)Math.Ceiling(bytes / 1200));
                uint ts = (uint)(nextFrame * 90);
                double complete = 0; bool broken = false;

                for (int i = 0; i < n; i++)
                {
                    double send = t + i * 0.05;
                    int size = (int)Math.Min(1200, bytes - i * 1200) + 40;
                    sentWin++;
                    double arrival = double.NaN;
                    bool lost;
                    if (!connected) lost = true;
                    else
                    {
                        double tx = size * 8 / Math.Max(1, link.CapacityKbps); // ms
                        double start = Math.Max(send, busyUntil);
                        bool overflow = start - send > MaxQueueMs;
                        if (!overflow) busyUntil = start + tx;
                        double jitter = Math.Abs(Gauss(rng)) * link.JitterMs * 0.6;
                        arrival = Math.Max(busyUntil + owd + jitter, lastArrival + 0.01);
                        lost = overflow || rng.NextDouble() < link.LossP;
                        if (!lost) lastArrival = arrival;
                        else if (rng.NextDouble() > link.LossP && !overflow)
                        {
                            arrival += 2 * owd + 8; // NACK -> retransmission
                            lost = false;
                        }
                        else if (overflow && rng.NextDouble() > 0.5)
                        {
                            arrival = send + MaxQueueMs + 2 * owd + 8;
                            lost = false;
                        }
                    }
                    ushort mySeq = seq++;
                    if (lost) { lostWin++; broken = true; continue; } // gap in sequence numbers
                    bool keyStart = isKey && i == 0;
                    arrivals.Enqueue((mySeq, ts, i == n - 1, size - 40, keyStart, arrival), arrival);
                    complete = Math.Max(complete, arrival);
                }

                if (isKey && !broken) chainOk = true;
                else if (broken) chainOk = false;

                double deadline = t + owd + 3 + d.BufferTargetMs;
                if (chainOk && !broken)
                {
                    double at = Math.Max(Math.Max(deadline, complete), lastDisplay);
                    displays.Add((at, at - t, bytes * 8));
                    lastDisplay = at;
                }
                else decoderErrors.Enqueue(Math.Max(deadline, broken ? deadline : complete));
                nextFrame += FrameMs;
            }

            while (arrivals.TryPeek(out var p, out var pat) && pat <= t)
            {
                arrivals.Dequeue();
                monitor.OnPacket(p.seq, p.ts, p.marker, p.bytes, p.at, p.key);
            }

            // ---- Receiver brain: every 100 ms
            if (t % TickMs == 0 && t > 0)
            {
                monitor.RttMs = 2 * owd + Math.Max(0, busyUntil - t);
                var s = monitor.TakeSample(t);
                bool decErr = false;
                while (decoderErrors.Count > 0 && decoderErrors.Peek() <= t) { decoderErrors.Dequeue(); decErr = true; }
                d = brain.Update(s, decErr);

                if (connected)
                {
                    var cap = d.CapKbps;
                    if (d.SendRemb) feedback.Enqueue(() => { rembCap = cap; rembAt = t + owd; }, t + owd);
                    if (d.RequestKeyframe) { keyRequests++; feedback.Enqueue(() => keyPending = true, t + owd); }
                }
                else if (d.RequestKeyframe) keyRequests++;

                if (!pathAlive)
                {
                    double restore = d.Recovery switch
                    {
                        RecoveryAction.RestartIce => t + 1_000,
                        RecoveryAction.Renegotiate => t + 3_000,
                        RecoveryAction.StartNewSession => t + 10_000,
                        _ => double.PositiveInfinity,
                    };
                    pathRestoreAt = Math.Min(pathRestoreAt, restore);
                }
            }

            // ---- Receiver reports to sender every 250 ms (loss-based encoder control)
            if (t % 250 == 0 && t > 0)
            {
                double loss = sentWin > 0 ? (double)lostWin / sentWin : 0;
                sentWin = lostWin = 0;
                if (connected)
                    feedback.Enqueue(() =>
                    {
                        if (loss > 0.10) lossBased *= 1 - 0.5 * loss;
                        else if (loss < 0.02) lossBased *= 1.02;
                        lossBased = Math.Clamp(lossBased, 1_000, 20_000);
                    }, t + owd);
            }

            // ---- Timeline (500 ms buckets)
            if (t % 500 == 0 && t > 0)
            {
                var bucket = displays.Where(x => x.at > t - 500 && x.at <= t).ToList();
                double frozen = FrozenWithin(displays, t - 500, t);
                timeline.Add(new TimelinePoint(t / 1000.0, link.Outage || !pathAlive ? 0 : link.CapacityKbps, d.CapKbps,
                    bucket.Sum(x => x.bits) / 500.0, bucket.Count > 0 ? bucket.Average(x => x.latency) : double.NaN,
                    frozen, d.BufferTargetMs));
            }
        }

        // ---- Metrics
        var gaps = new List<double>();
        for (int i = 1; i < displays.Count; i++) gaps.Add(displays[i].at - displays[i - 1].at);
        if (displays.Count > 0) gaps.Add(sc.DurationMs - displays[^1].at);
        double frozenMs = gaps.Where(g => g > 100).Sum();
        int freezes = gaps.Count(g => g > 250);
        int stutters = gaps.Count(g => g > 50 && g <= 250);
        var lat = displays.Select(x => x.latency).OrderBy(x => x).ToArray();
        var perSec = Enumerable.Range(0, (int)(sc.DurationMs / 1000))
            .Select(sec => displays.Where(x => x.at >= sec * 1000 && x.at < sec * 1000 + 1000).Sum(x => x.bits) / 1000.0).ToArray();
        double mean = perSec.Average();
        double swing = mean > 0 ? Math.Sqrt(perSec.Select(v => (v - mean) * (v - mean)).Average()) / mean : 0;

        return new RunResult(sc.Id, brain.Name, seed, capsHonoured, frozenMs / 1000, freezes, stutters,
            displays.Sum(x => x.bits) / sc.DurationMs, swing,
            lat.Length > 0 ? lat.Average() : 0, lat.Length > 0 ? lat[(int)(lat.Length * 0.95)] : 0,
            sessionDrops, keyRequests, timeline);
    }

    private static double FrozenWithin(List<(double at, double latency, double bits)> displays, double from, double to)
    {
        // time in [from,to) covered by display gaps longer than 100 ms
        double frozen = 0, prev = double.NaN;
        int i = displays.FindLastIndex(x => x.at <= from);
        prev = i >= 0 ? displays[i].at : 0;
        for (int k = Math.Max(0, i + 1); k < displays.Count && displays[k].at <= to + 1000; k++)
        {
            double a = prev, b = displays[k].at;
            if (b - a > 100) frozen += Math.Max(0, Math.Min(b, to) - Math.Max(a, from));
            prev = b;
            if (b >= to) break;
        }
        if (prev < to && to - prev > 100) frozen += to - Math.Max(prev, from);
        return Math.Min(frozen, to - from);
    }

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}
