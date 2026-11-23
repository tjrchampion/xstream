using XStream.Core.Rtp;

namespace XStream.Core.Stability;

/// <summary>
/// Receiver-side bitrate cap tuned for a steady picture rather than maximum throughput.
///
/// Differences from a standard WebRTC receiver estimator:
///  * Reacts to queue build-up (delay trend) before packets are lost, so the cut happens before the freeze.
///  * After congestion it asks for a fraction of what actually got through (headroom), not what was sent.
///  * Detects yo-yo behaviour (repeated cut/recover cycles, typical of Wi-Fi interference) and holds a
///    lower ceiling for a while: a steady 10 Mbps looks better than one swinging between 20 and 3.
///  * Recovers quickly to the last known-good rate after a short burst, then probes slowly beyond it.
/// </summary>
public sealed class AdaptiveBitrateController
{
    private readonly StabilityOptions _o;
    private readonly ProfileTuning _t;

    private double _lossEwma;
    private double _lastDecreaseMs = double.NegativeInfinity;
    private readonly Queue<(double t, double rate)> _episodes = new(); // start of each congestion episode
    private double _ceiling = double.PositiveInfinity;
    private double _ceilingUntilMs = double.NegativeInfinity;
    private double _lastStableKbps;
    private double _clearSinceMs = double.NaN;
    private double _lastSampleMs = double.NaN;
    // Last 300 ms of per-tick receive rates. A single 100 ms window swings 2x on a healthy link (frame sizes
    // vary, keyframes arrive in bursts), so cut targets use a short average. Longer windows lag a real
    // collapse and cut too shallow (simulator: busy Wi-Fi freezes rose 3x with a 1 s window).
    private readonly Queue<double> _recent = new();
    private double _recentSum;
    private int _queueHighTicks;

    public double CapKbps { get; private set; }
    public string LastReason { get; private set; } = "start";
    public bool CeilingActive(double nowMs) => nowMs < _ceilingUntilMs;
    public double Ceiling => _ceiling;
    public int DecreaseCount { get; private set; }

    public AdaptiveBitrateController(StabilityOptions options)
    {
        _o = options;
        _t = options.Tuning;
        CapKbps = Math.Clamp(options.StartKbps, options.MinKbps, options.MaxKbps);
        _lastStableKbps = CapKbps;
    }

    public double Update(in NetworkSample s)
    {
        double dt = double.IsNaN(_lastSampleMs) ? 100 : Math.Clamp(s.TimeMs - _lastSampleMs, 1, 2000);
        _lastSampleMs = s.TimeMs;
        double now = s.TimeMs;

        // No media at all: leave the cap alone, the reconnect supervisor owns this situation.
        if (s.ReceiveKbps <= 0.01 && s.CompleteFrames == 0 && s.BrokenFrames == 0)
        {
            LastReason = "no media";
            return CapKbps;
        }

        _lossEwma += (s.LossFraction - _lossEwma) * 0.3;
        double cq = _t.CongestionQueueMs;
        _recent.Enqueue(s.ReceiveKbps);
        _recentSum += s.ReceiveKbps;
        while (_recent.Count > 3) _recentSum -= _recent.Dequeue();
        double recv = Math.Max(_recentSum / _recent.Count, _o.MinKbps);

        // A queue spike lasting one tick is a big frame (usually a keyframe) arriving in a burst, not a
        // full link: only treat queueing as congestion once it has lasted two ticks.
        _queueHighTicks = s.QueueDelayMs > cq ? _queueHighTicks + 1 : 0;
        bool queueBuilding = _queueHighTicks >= 2 && s.DelayTrendMs > 0.5;

        bool severe = s.LossFraction > 0.10 || (s.QueueDelayMs > 3 * cq && _queueHighTicks >= 2);
        bool congested = _lossEwma > 0.03 || queueBuilding;
        bool lightLoss = _lossEwma > 0.01 || s.QueueDelayMs > cq * 0.6;

        if (severe && now - _lastDecreaseMs > 150)
        {
            Decrease(now, Math.Min(CapKbps, recv * 0.70), "severe loss/queue"); // non-compounding: repeated hits in one burst don't spiral down
        }
        else if (congested && now - _lastDecreaseMs > 400)
        {
            Decrease(now, Math.Min(CapKbps, recv * _t.Headroom), "congestion");
        }
        else if (severe || congested || lightLoss)
        {
            _clearSinceMs = double.NaN;
            LastReason = "hold";
        }
        else
        {
            if (double.IsNaN(_clearSinceMs)) _clearSinceMs = now;
            double clearFor = now - _clearSinceMs;
            if (now - _lastDecreaseMs > 1_500 && clearFor > 500)
            {
                // Fast path back to the last known-good rate after a short burst.
                bool recovering = CapKbps < _lastStableKbps * 0.85;
                double rate = recovering ? 0.20 : _t.IncreasePerSec;
                double next = CapKbps * (1 + rate * dt / 1000.0);
                if (recovering) next = Math.Min(next, _lastStableKbps * 0.95);
                // Don't race far ahead of what the sender actually delivers (content-limited scenes).
                next = Math.Min(next, Math.Max(recv * 1.3 + 500, CapKbps));
                SetCap(next, now);
                LastReason = recovering ? "recover" : "probe";
            }
            else LastReason = "settle";

            if (clearFor > 5_000) _lastStableKbps = Math.Max(_lastStableKbps * 0.9995, CapKbps); // ~0.5%/s decay
        }
        return CapKbps;
    }

    private void Decrease(double now, double target, string reason)
    {
        // A new congestion episode starts after 2 s without cuts. Several cuts within one burst
        // are one episode.
        if (now - _lastDecreaseMs > 2_000)
        {
            _episodes.Enqueue((now, CapKbps));
            while (_episodes.Count > 0 && now - _episodes.Peek().t > 40_000) _episodes.Dequeue();

            // Yo-yo detection: 3+ episodes in 40 s means the link keeps collapsing near the same rate.
            // Hold a ceiling just under that rate so we stop climbing into the next collapse.
            if (_episodes.Count >= 3)
            {
                var rates = _episodes.Select(d => d.rate).OrderBy(r => r).ToArray();
                double median = rates[rates.Length / 2];
                _ceiling = Math.Max(_o.MinKbps, median * _t.Headroom);
                _ceilingUntilMs = now + 20_000;
            }
        }

        _lastDecreaseMs = now;
        _clearSinceMs = double.NaN;
        DecreaseCount++;
        SetCap(target, now);
        LastReason = reason;
    }

    private void SetCap(double kbps, double now)
    {
        double limit = _o.MaxKbps;
        if (now < _ceilingUntilMs) limit = Math.Min(limit, _ceiling);
        CapKbps = Math.Clamp(kbps, _o.MinKbps, Math.Max(_o.MinKbps, limit));
    }
}
