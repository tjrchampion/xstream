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

    /// <summary>Profile-specific tuning, derived from <see cref="Profile"/>.</summary>
    public ProfileTuning Tuning => Profile switch
    {
        StreamProfile.Competitive => new(Headroom: 0.90, MinBufferMs: 0, MaxBufferMs: 35, JitterMultiplier: 1.5, IncreasePerSec: 0.08, CongestionQueueMs: 25),
        StreamProfile.Smooth => new(Headroom: 0.80, MinBufferMs: 25, MaxBufferMs: 150, JitterMultiplier: 3.0, IncreasePerSec: 0.05, CongestionQueueMs: 40),
        _ => new(Headroom: 0.85, MinBufferMs: 4, MaxBufferMs: 80, JitterMultiplier: 2.2, IncreasePerSec: 0.07, CongestionQueueMs: 30),
    };
}

/// <param name="Headroom">Fraction of measured throughput we ask for after congestion (leaves room for Wi-Fi spikes).</param>
/// <param name="CongestionQueueMs">Queueing delay that counts as "the link is filling up".</param>
public readonly record struct ProfileTuning(
    double Headroom, double MinBufferMs, double MaxBufferMs, double JitterMultiplier,
    double IncreasePerSec, double CongestionQueueMs);
