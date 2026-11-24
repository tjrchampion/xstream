using XStream.Core.Rtp;

namespace XStream.Core.Stability;

/// <summary>What the engine wants the transport to do after one sample.</summary>
public readonly record struct StabilityDecision(
    double CapKbps,
    bool SendRemb,
    bool RequestKeyframe,
    double BufferTargetMs,
    RecoveryAction Recovery,
    string Reason);

/// <summary>Whether the console actually follows our bitrate caps (REMB). Learned at runtime.</summary>
public enum CapCompliance { Unknown, Honoured, Ignored }

/// <summary>Everything the on-screen stats overlay needs.</summary>
public readonly record struct StabilitySnapshot(
    double TimeMs, double ReceiveKbps, double CapKbps, double LossPct, double JitterMs, double QueueDelayMs,
    double RttMs, double BufferMs, LinkState Link, CapCompliance Caps, int KeyframeRequests, int Reconnects,
    string Reason, string Verdict);

/// <summary>
/// The stability brain. Pure and deterministic: it takes a <see cref="NetworkSample"/> (every ~100 ms)
/// and returns a <see cref="StabilityDecision"/>. It never touches sockets, so the same code runs
/// against the real stream and inside the network simulator.
/// </summary>
public sealed class StabilityEngine
{
    private StabilityOptions _o;
    private AdaptiveBitrateController _abr;
    private BufferPolicy _buffer;
    private readonly object _gate = new();
    private readonly KeyframeRecovery _keyframes;
    private readonly ReconnectSupervisor _reconnect;

    private double _lastRembMs = double.NegativeInfinity;
    private double _lastRembKbps;

    // Cap-compliance probe: after we cap below what's arriving, does the rate follow within 3 s?
    private double _probeStartMs = double.NaN;
    private double _probeCap;
    private int _probeHits, _probeMisses;

    public StabilitySnapshot Snapshot { get; private set; }
    public CapCompliance Caps { get; private set; } = CapCompliance.Unknown;
    public StabilityOptions Options => _o;

    public StabilityEngine(StabilityOptions? options = null)
    {
        _o = options ?? new StabilityOptions();
        _abr = new AdaptiveBitrateController(_o);
        _buffer = new BufferPolicy(_o.Tuning);
        _reconnect = new ReconnectSupervisor(_o);
        _keyframes = new KeyframeRecovery { RetryMs = _o.KeyframeRetryMs, KeepAskingMs = _o.KeyframeKeepAskingMs };
    }

    /// <summary>
    /// Change profile and/or bitrate ceiling mid-stream (from the in-stream menu). The bitrate controller and
    /// buffer policy are rebuilt for the new tuning, starting from the current cap so the picture doesn't jump.
    /// Thread-safe. Note the SDP ceiling was fixed at connect time: raising the maximum above the session's
    /// starting maximum takes full effect on the next stream.
    /// </summary>
    public void Retune(StreamProfile profile, double maxKbps)
    {
        lock (_gate)
        {
            if (profile == _o.Profile && Math.Abs(maxKbps - _o.MaxKbps) < 1) return;
            double current = _abr.CapKbps;
            _o = _o with { Profile = profile, MaxKbps = maxKbps, StartKbps = Math.Clamp(current, _o.MinKbps, maxKbps) };
            _abr = new AdaptiveBitrateController(_o);
            var extra = _buffer.ExtraBaseMs;
            _buffer = new BufferPolicy(_o.Tuning) { ExtraBaseMs = extra };
        }
    }

    /// <summary>
    /// A frame was just given up on or failed to decode: true if a keyframe should be asked for right now (same
    /// rate limits as the tick). Lets the receive path ask at once instead of waiting up to 100 ms for <see cref="Update"/>.
    /// </summary>
    public bool OnPictureLost(double nowMs, double rttMs)
    {
        lock (_gate) return _keyframes.OnPictureLost(nowMs, rttMs);
    }

    public StabilityDecision Update(in NetworkSample s, bool decoderError = false)
    {
        lock (_gate) return UpdateLocked(s, decoderError);
    }

    private StabilityDecision UpdateLocked(in NetworkSample s, bool decoderError)
    {
        double cap = _abr.Update(s);
        double buffer = _buffer.Update(s);
        bool keyframe = _keyframes.Update(s, decoderError);
        var recovery = _reconnect.Update(s);
        if (recovery == RecoveryAction.RequestKeyframe) { keyframe = true; recovery = RecoveryAction.None; }
        if (recovery is RecoveryAction.RestartIce or RecoveryAction.Renegotiate or RecoveryAction.StartNewSession)
            keyframe = true; // first thing we want on a new path

        bool sendRemb = s.TimeMs - _lastRembMs >= _o.RembRefreshMs
                        || Math.Abs(cap - _lastRembKbps) > _lastRembKbps * 0.03;
        if (sendRemb) { _lastRembMs = s.TimeMs; _lastRembKbps = cap; }

        TrackCompliance(s, cap);

        Snapshot = new StabilitySnapshot(s.TimeMs, s.ReceiveKbps, cap, s.LossFraction * 100, s.JitterMs,
            s.QueueDelayMs, s.RttMs, buffer, _reconnect.State, Caps, _keyframes.Requests, _reconnect.Reconnects,
            _abr.LastReason, Verdict(s));

        return new StabilityDecision(cap, sendRemb, keyframe, buffer, recovery, _abr.LastReason);
    }

    private void TrackCompliance(in NetworkSample s, double cap)
    {
        if (Caps != CapCompliance.Unknown || s.ReceiveKbps <= 0) return;
        if (double.IsNaN(_probeStartMs))
        {
            if (cap < s.ReceiveKbps * 0.9) { _probeStartMs = s.TimeMs; _probeCap = cap; }
            return;
        }
        if (s.ReceiveKbps <= _probeCap * 1.1) { _probeHits++; _probeStartMs = double.NaN; }
        else if (s.TimeMs - _probeStartMs > 3_000) { _probeMisses++; _probeStartMs = double.NaN; }

        if (_probeHits >= 2) Caps = CapCompliance.Honoured;
        else if (_probeMisses >= 3)
        {
            // Can't steer the encoder mid-stream. The overlay suggests a lower start bitrate instead
            // (sent in the SDP at connect time, which the console does respect).
            Caps = CapCompliance.Ignored;
        }
    }

    /// <summary>Plain-language "what's wrong" line for the overlay.</summary>
    private string Verdict(in NetworkSample s)
    {
        if (_reconnect.State is LinkState.IceRestarting or LinkState.Renegotiating or LinkState.NewSession)
            return "Connection lost - reconnecting";
        if (s.QueueDelayMs > 60) return "Network is congested (upload or Wi-Fi airtime full)";
        if (s.LossFraction > 0.05 && s.QueueDelayMs < 15) return "Packets dropping without congestion - likely Wi-Fi interference";
        if (s.JitterMs > 12) return "Uneven arrival - Wi-Fi or a busy network";
        if (s.RttMs > 80) return "High ping - far from console or slow route";
        return "Stable";
    }
}
