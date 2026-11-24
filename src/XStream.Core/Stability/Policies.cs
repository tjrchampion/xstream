using XStream.Core.Rtp;

namespace XStream.Core.Stability;

/// <summary>
/// Chooses how much video to hold back before display. Rises fast when the network gets rough
/// and falls slowly once it calms down, so a single Wi-Fi hiccup doesn't cause repeated stutter.
/// </summary>
public sealed class BufferPolicy
{
    private readonly ProfileTuning _t;
    private readonly Queue<(double t, double jitter)> _jitter = new();
    private double _boostMs;
    private double _lastMs = double.NaN;

    public double TargetMs { get; private set; }
    public double ExtraBaseMs { get; set; } // raised by the engine when bitrate caps are ignored

    public BufferPolicy(ProfileTuning tuning)
    {
        _t = tuning;
        TargetMs = tuning.MinBufferMs;
    }

    public double Update(in NetworkSample s)
    {
        double dt = double.IsNaN(_lastMs) ? 100 : Math.Clamp(s.TimeMs - _lastMs, 1, 2000);
        _lastMs = s.TimeMs;

        // Interarrival jitter = how unevenly frames arrive.
        double unevenness = s.JitterMs;
        _jitter.Enqueue((s.TimeMs, unevenness));
        while (_jitter.Count > 0 && s.TimeMs - _jitter.Peek().t > 10_000) _jitter.Dequeue();
        var sorted = _jitter.Select(j => j.jitter).OrderBy(x => x).ToArray();
        double p95 = sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.95))];
        // React to a fresh spike right away instead of waiting for it to reach the 95th percentile.
        double now = s.TimeMs;
        double recent = _jitter.Where(j => now - j.t <= 500).Max(j => j.jitter);
        double level = Math.Max(p95, recent);

        // Spikes: loss bursts or runs of broken frames add a boost that decays with a ~8 s half-life.
        // (One retransmitted packet is normal and doesn't count.)
        if (s.LossFraction > 0.02 || s.BrokenFrames >= 3)
            _boostMs = Math.Max(_boostMs, Math.Min(_t.MaxBufferMs * 0.5, 10 + s.JitterMs * 2));
        _boostMs *= Math.Pow(0.5, dt / 8_000.0);

        double target = level * _t.JitterMultiplier + _boostMs + ExtraBaseMs;
        target = Math.Clamp(target, _t.MinBufferMs, _t.MaxBufferMs);

        // Up immediately, down gently (max 2 ms per 100 ms) to avoid visible speed changes.
        TargetMs = target >= TargetMs ? target : Math.Max(target, TargetMs - 0.02 * dt);
        return TargetMs;
    }
}

/// <summary>
/// Requests a fresh keyframe as soon as the picture breaks, instead of waiting for a timer.
/// Rate-limited by round-trip time so we don't flood the console.
/// </summary>
public sealed class KeyframeRecovery
{
    private double _lastRequestMs = double.NegativeInfinity;
    private bool _awaiting;
    private double _awaitSinceMs;

    public int Requests { get; private set; }
    public bool Awaiting => _awaiting;
    /// <summary>Re-ask this soon (at least 3 round trips) when the picture breaks again while waiting.</summary>
    public double RetryMs { get; init; } = 250;
    /// <summary>Re-ask this soon (at least 4 round trips) while still waiting, even if nothing new broke.</summary>
    public double KeepAskingMs { get; init; } = 500;

    /// <param name="decoderError">Set by the decoder when it can't decode (reference frame missing).</param>
    public bool Update(in NetworkSample s, bool decoderError = false)
    {
        double now = s.TimeMs;
        if (s.KeyframeSeen) _awaiting = false;

        // Decoder errors are authoritative. Packet-level gaps alone are often repaired by retransmission,
        // so only act on them when several frames in a row arrive broken.
        bool broken = decoderError || s.BrokenFrames >= 3;
        if (broken && OnPictureLost(now, s.RttMs)) return true;
        // No complete keyframe yet. Once a loss is declared every later frame is held back until one arrives, and
        // those frames arrive intact, so "broken" never fires again: if the keyframe itself was lost the picture
        // stayed frozen for good (live: 24 s, to the end of the session, after a 400 ms loss burst). Keep asking.
        if (_awaiting && now - _lastRequestMs > Math.Max(KeepAskingMs, s.RttMs * 4))
            return Fire(now);
        return false;
    }

    /// <summary>
    /// The picture just broke (a frame was given up on, or the decoder failed): ask for a keyframe now if the rate
    /// limits allow. Called the moment it happens rather than on the next 100 ms tick, which added 0-100 ms (50 on
    /// average) to every freeze.
    /// </summary>
    public bool OnPictureLost(double nowMs, double rttMs)
    {
        if (!_awaiting && nowMs - _lastRequestMs > Math.Max(150, rttMs * 2.5))
            return Fire(nowMs);
        if (_awaiting && nowMs - _lastRequestMs > Math.Max(RetryMs, rttMs * 3))
            return Fire(nowMs); // still broken: the request or the keyframe itself got lost
        return false;
    }

    private bool Fire(double now)
    {
        _lastRequestMs = now;
        if (!_awaiting) _awaitSinceMs = now;
        _awaiting = true;
        Requests++;
        return true;
    }
}

public enum LinkState { Connecting, Connected, Degraded, IceRestarting, Renegotiating, NewSession, Failed }

public enum RecoveryAction { None, RequestKeyframe, RestartIce, Renegotiate, StartNewSession, GiveUp }

/// <summary>
/// Escalating reconnect ladder. A short Wi-Fi drop should be invisible, a longer one should resume
/// without the user re-launching anything:
///   no media 0.7 s  -> Degraded (ask for keyframe, keep the last frame on screen)
///   2.5 s           -> ICE restart (re-pick the network path, same session)
///   8 s             -> renegotiate a new peer connection on the same Xbox session
///   20 s            -> start a new streaming session to the same console
/// </summary>
public sealed class ReconnectSupervisor
{
    private readonly StabilityOptions _o;
    private int _iceRestarts;
    private bool _renegotiated, _newSession;
    private double _lastIceRestartMs = double.NegativeInfinity;

    public LinkState State { get; private set; } = LinkState.Connecting;
    public int Reconnects { get; private set; }

    public ReconnectSupervisor(StabilityOptions o) => _o = o;

    public RecoveryAction Update(in NetworkSample s)
    {
        double gap = s.MsSinceLastFrame;
        bool media = s.CompleteFrames > 0;

        if (media && gap < _o.DegradedAfterMs)
        {
            if (State is not (LinkState.Connected or LinkState.Connecting)) Reconnects++;
            State = LinkState.Connected;
            _iceRestarts = 0;
            _renegotiated = _newSession = false;
            return RecoveryAction.None;
        }
        if (State == LinkState.Connecting) return RecoveryAction.None;

        if (gap >= _o.NewSessionAfterMs && !_newSession)
        {
            _newSession = true;
            State = LinkState.NewSession;
            return RecoveryAction.StartNewSession;
        }
        if (gap >= _o.NewSessionAfterMs * 3)
        {
            State = LinkState.Failed;
            return RecoveryAction.GiveUp;
        }
        if (gap >= _o.RenegotiateAfterMs && !_renegotiated && !_newSession)
        {
            _renegotiated = true;
            State = LinkState.Renegotiating;
            return RecoveryAction.Renegotiate;
        }
        if (gap >= _o.IceRestartAfterMs && !_renegotiated && _iceRestarts < _o.MaxIceRestarts
            && s.TimeMs - _lastIceRestartMs > 2_000)
        {
            _iceRestarts++;
            _lastIceRestartMs = s.TimeMs;
            State = LinkState.IceRestarting;
            return RecoveryAction.RestartIce;
        }
        if (gap >= _o.DegradedAfterMs && State == LinkState.Connected)
        {
            State = LinkState.Degraded;
            return RecoveryAction.RequestKeyframe;
        }
        return RecoveryAction.None;
    }
}
