namespace XStream.Core.Stability;

/// <summary>How the player trades latency for smoothness.</summary>
public enum StreamProfile
{
    /// <summary>Lowest latency. Minimal buffer, fast reactions. For shooters and fighting games.</summary>
    Competitive,
    /// <summary>Default. Small buffer that grows only when the network gets rough.</summary>
    Balanced,
    /// <summary>Most stable picture. Bigger buffer and more bitrate headroom. For RPGs and slower games.</summary>
    Smooth,
}

public sealed record StabilityOptions
{
    public StreamProfile Profile { get; init; } = StreamProfile.Balanced;

    /// <summary>Hard ceiling the user picks ("cap at 12 Mbps"). The engine never asks for more.</summary>
    public double MaxKbps { get; init; } = 20_000;

    /// <summary>Floor below which the picture becomes unusable; we never request less.</summary>
    public double MinKbps { get; init; } = 2_000;

    /// <summary>Starting request before anything is measured.</summary>
    public double StartKbps { get; init; } = 10_000;

    /// <summary>Send a bitrate cap (REMB) at least this often; senders expire stale estimates.</summary>
    public double RembRefreshMs { get; init; } = 1_000;

    // Reconnect ladder (ms of no media before each step)
    public double DegradedAfterMs { get; init; } = 700;
    public double IceRestartAfterMs { get; init; } = 2_500;
    public double RenegotiateAfterMs { get; init; } = 8_000;
    public double NewSessionAfterMs { get; init; } = 20_000;
    public int MaxIceRestarts { get; init; } = 3;

    /// <summary>Climb faster on a quiet link (see <see cref="ProfileTuning.FastIncreasePerSec"/>). Off = the old
    /// steady climb; kept for A/B in the simulator.</summary>
    public bool FastClimb { get; init; } = true;

    /// <summary>
    /// While waiting for a recovery keyframe and the picture breaks again (the request or the keyframe itself was
    /// lost), ask again after this long (at least 3 round trips). 250 ms suited the 30 ms round trip that used to be
    /// assumed; a measured 4-5 ms home link gets a keyframe back in ~40 ms.
    /// </summary>
    public double KeyframeRetryMs { get; init; } = 250;
    /// <summary>
    /// Still no keyframe (and nothing new broke): ask again after this long (at least 4 round trips). Was 500 ms:
    /// when the recovery keyframe was lost in a loss burst, the frames after the burst arrived intact but held
    /// back, nothing else re-asked, and the picture stayed frozen that much longer.
    /// </summary>
    public double KeyframeKeepAskingMs { get; init; } = 250;

    /// <summary>Profile-specific tuning, derived from <see cref="Profile"/>.</summary>
    public ProfileTuning Tuning => Profile switch
    {
        StreamProfile.Competitive => new(Headroom: 0.90, MinBufferMs: 0, MaxBufferMs: 35, JitterMultiplier: 1.5, IncreasePerSec: 0.08, CongestionQueueMs: 25, FastIncreasePerSec: 0.60, FastClimbWaitMs: 1_000),
        StreamProfile.Smooth => new(Headroom: 0.80, MinBufferMs: 25, MaxBufferMs: 150, JitterMultiplier: 3.0, IncreasePerSec: 0.05, CongestionQueueMs: 40, FastIncreasePerSec: 0.35, FastClimbWaitMs: 2_500),
        _ => new(Headroom: 0.85, MinBufferMs: 4, MaxBufferMs: 80, JitterMultiplier: 2.2, IncreasePerSec: 0.07, CongestionQueueMs: 30, FastIncreasePerSec: 0.50, FastClimbWaitMs: 1_500),
    };
}

/// <param name="Headroom">Fraction of measured throughput we ask for after congestion (leaves room for Wi-Fi spikes).</param>
/// <param name="CongestionQueueMs">Queueing delay that counts as "the link is filling up".</param>
/// <param name="FastIncreasePerSec">Top climb rate once a clean link (no loss, jitter under ~1 ms) has been quiet for a
/// few seconds after a one-off hiccup. Lower for Smooth, which puts fewer freezes first.</param>
/// <param name="FastClimbWaitMs">Quiet time (after the post-cut settle) before the climb starts speeding up.</param>
public readonly record struct ProfileTuning(
    double Headroom, double MinBufferMs, double MaxBufferMs, double JitterMultiplier,
    double IncreasePerSec, double CongestionQueueMs, double FastIncreasePerSec, double FastClimbWaitMs);
