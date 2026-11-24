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
///  * Recovers quickly to the last known-good rate after a short burst, then probes beyond it: slowly at
///    first, faster the longer the link stays completely quiet (no loss, empty queue), so one hiccup on a
///    clean link costs a couple of seconds of softer picture rather than ten or more.
/// </summary>
public sealed class AdaptiveBitrateController
{
    private readonly StabilityOptions _o;
    private readonly ProfileTuning _t;

    private double _lossEwma;
    private double _lastDecreaseMs = double.NegativeInfinity;
    private double _burstStartCap;
    private bool _burstRandom;
    private double _randomBurstUntilMs = double.NegativeInfinity;
    private readonly Queue<(double t, double rate)> _episodes = new(); // start of each congestion episode (40 s, yo-yo)
    private readonly Queue<double> _episodeTimes = new(); // same, kept 60 s: gates the fast climb
    private double _ceiling = double.PositiveInfinity;
    private double _ceilingUntilMs = double.NegativeInfinity;
    private double _lastStableKbps;
    private double _clearSinceMs = double.NaN;
    private double _quietSinceMs = double.NaN;
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

    /// <summary>Share of the cap kept when packets come back after an outage.</summary>
    public double OutageResumeFraction { get; init; } = 0.75;

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

        // Back after an outage: resume a step lower rather than halving (the gap isn't counted as loss, see
        // RtpReceiveMonitor.OutageGapMs). The old rate often still fits (home Wi-Fi dropping out for a moment), but
        // walking away from the router the link really is weaker afterwards, and resuming at the full rate there
        // froze more (simulator, handheld: Smooth 7.2 -> 8.1 freezes per run).
        if (s.OutageEnded && now - _lastDecreaseMs > 2_000)
            Decrease(now, CapKbps * OutageResumeFraction, "after outage");

        _lossEwma += (s.LossFraction - _lossEwma) * 0.3;
        double cq = _t.CongestionQueueMs;
        _recent.Enqueue(s.ReceiveKbps);
        _recentSum += s.ReceiveKbps;
        while (_recent.Count > 3) _recentSum -= _recent.Dequeue();
        double recv = Math.Max(_recentSum / _recent.Count, _o.MinKbps);

        // Recovering from a random-loss burst (below), the console sends a recovery keyframe: 200-400 KB at
        // 1440p, paced out over tens to hundreds of ms, with the frames behind it waiting. That reads as queueing
        // although the network is fine, and cutting the cap only makes the console pace it slower. Live: the
        // keyframe read as a 130 ms -> 1 s "queue" and drove the cap from 25 to 3.7 Mbps while 7-11 Mbps kept
        // arriving. Only after a random-loss burst: in a real collapse the app asks for keyframes too, and
        // masking the queue there hid real congestion (simulator, busy Wi-Fi: 15-30% more freezes).
        bool keyframePacing = s.KeyframeInFlight && now < _randomBurstUntilMs;
        double queue = keyframePacing ? 0 : s.QueueDelayMs;

        // A queue spike lasting one tick is a big frame (usually a keyframe) arriving in a burst, not a
        // full link: only treat queueing as congestion once it has lasted two ticks.
        _queueHighTicks = queue > cq ? _queueHighTicks + 1 : 0;
        bool queueBuilding = _queueHighTicks >= 2 && s.DelayTrendMs > 0.5;

        bool severe = s.LossFraction > 0.10 || (queue > 3 * cq && _queueHighTicks >= 2);
        // After a random-loss burst the smoothed loss stays high for ~1 s: that's the burst already handled,
        // not new congestion, so it holds the cap rather than cutting it again to a share of the (tiny)
        // post-burst throughput.
        bool congested = (_lossEwma > 0.03 && now >= _randomBurstUntilMs) || queueBuilding;
        bool lightLoss = _lossEwma > 0.01 || queue > cq * 0.6;

        if (severe && now - _lastDecreaseMs > 150)
        {
            // Heavy loss with no queue behind it isn't a full link (that queues first): it's interference or a
            // burst on the path. What got through then measures the loss, not the capacity, so cutting to 70% of
            // it overshoots (live: a 77% burst on wired cut 40 -> 2.5 Mbps, and the console then paced its 205 KB
            // recovery keyframe at 2.5 Mbps, adding ~1.5 s to the freeze). Cut like standard WebRTC's loss-based
            // controller instead: by half the loss, at most to half the rate before the burst.
            // Classified on the burst's first tick and kept for the whole burst: a tick later the recovery
            // keyframe is already queueing (see above).
            if (now - _lastDecreaseMs > 2_000)
            {
                _burstStartCap = CapKbps;
                _burstRandom = s.LossFraction > 0.10 && s.QueueDelayMs < cq;
            }
            bool randomLoss = _burstRandom;
            if (randomLoss) _randomBurstUntilMs = now + 3_000;
            double target = randomLoss
                ? _burstStartCap * Math.Max(0.5, 1 - 0.5 * s.LossFraction)
                : recv * 0.70;
            Decrease(now, Math.Min(CapKbps, target), randomLoss ? "burst loss" : "severe loss/queue"); // non-compounding: repeated hits in one burst don't spiral down
        }
        else if (congested && now - _lastDecreaseMs > 400)
        {
            Decrease(now, Math.Min(CapKbps, recv * _t.Headroom), "congestion");
        }
        else if (severe || congested || lightLoss)
        {
            _clearSinceMs = double.NaN;
            _quietSinceMs = double.NaN;
            LastReason = "hold";
        }
        else
        {
            if (double.IsNaN(_clearSinceMs)) _clearSinceMs = now;
            double clearFor = now - _clearSinceMs;

            // Quiet link: what a clean wired path looks like between hiccups. No loss at all, jitter under
            // 1.2 ms, queue near empty (real wired Series X sessions: jitter p99 < 1 ms, a lost packet in < 0.5%
            // of ticks). Busy Wi-Fi keeps a trickle of loss and jitter even between collapses, and there a
            // fast climb just reaches the next collapse sooner (simulator: ~8% more freezes), so it keeps the
            // steady climb. Also off once two congestion episodes land within 60 s.
            while (_episodeTimes.Count > 0 && now - _episodeTimes.Peek() > 60_000) _episodeTimes.Dequeue();
            bool quiet = _o.FastClimb && _episodeTimes.Count <= 1 && s.LossFraction <= 0 && _lossEwma < 0.002
                         && s.JitterMs < 1.2 && s.QueueDelayMs < cq * 0.3 && !CeilingActive(now);
            if (!quiet) _quietSinceMs = double.NaN;
            else if (double.IsNaN(_quietSinceMs)) _quietSinceMs = now;

            if (now - _lastDecreaseMs > 1_500 && clearFor > 500)
            {
                // Fast path back to the last known-good rate after a short burst.
                bool recovering = CapKbps < _lastStableKbps * 0.85;
                double rate = recovering ? Math.Max(0.20, _t.IncreasePerSec) : _t.IncreasePerSec;

                // On a quiet link the climb rate ramps up over 2 s to FastIncreasePerSec (about 1.8x per
                // second at 0.6). Queueing drops it straight back to the slow rate, and the yo-yo ceiling
                // still applies, so a link that keeps collapsing near one rate isn't pushed harder.
                if (quiet)
                {
                    // Count quiet time from the end of the post-cut settle, then wait (per profile) before speeding up.
                    double quietFor = now - Math.Max(_quietSinceMs, _lastDecreaseMs + 1_500);
                    double ramp = Math.Clamp((quietFor - _t.FastClimbWaitMs) / 2_000, 0, 1);
                    rate = Math.Max(rate, _t.IncreasePerSec + (_t.FastIncreasePerSec - _t.IncreasePerSec) * ramp);
                }
                bool fastClimb = quiet && rate > (recovering ? Math.Max(0.20, _t.IncreasePerSec) : _t.IncreasePerSec);
                double next = CapKbps * (1 + rate * dt / 1000.0);
                if (recovering) next = Math.Min(next, _lastStableKbps * 0.95);
                // Don't race far ahead of what the sender actually delivers (content-limited scenes). The console's
                // encoder averages ~75% of the cap, so 1.3x only lets the cap creep up by the +500 each tick; on a
                // quiet clean link give it room to actually climb.
                next = Math.Min(next, Math.Max(fastClimb ? recv * 2 + 1_000 : recv * 1.3 + 500, CapKbps));
                SetCap(next, now);
                LastReason = fastClimb ? "fast climb" : recovering ? "recover" : "probe";
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
            _episodeTimes.Enqueue(now);
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
        _quietSinceMs = double.NaN;
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
