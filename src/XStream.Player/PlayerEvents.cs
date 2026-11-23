using System.Text.Json;
using System.Text.Json.Serialization;
using XStream.Core.Stability;

namespace XStream.Player;

public enum PlayerStage { Starting, RequestingSession, WakingConsole, InQueue, Connecting, StartingVideo, Streaming, Reconnecting, Stopping, Ended, Failed }

/// <summary>Once-a-second numbers for the launcher's live panel and the CLI's stats line.</summary>
public sealed record LiveStats
{
    public double TimeS { get; init; }
    public double Mbps { get; init; }
    public double CapMbps { get; init; }
    public double LossPct { get; init; }
    public double PingMs { get; init; }
    public double JitterMs { get; init; }
    public double BufferMs { get; init; }
    public double Fps { get; init; }
    public double NetToScreenP50Ms { get; init; }
    public double NetToScreenP99Ms { get; init; }
    public double DecodeMs { get; init; }
    public string Link { get; init; } = "";
    public string Caps { get; init; } = "";
    public string Verdict { get; init; } = "";
    public int Pads { get; init; }
    public double PadHz { get; init; }
    public double PadP99Us { get; init; }
    /// <summary>Size of the stream's pictures (0 until the first one).</summary>
    public int VideoWidth { get; init; }
    public int VideoHeight { get; init; }
}

/// <summary>What the session is running with (shown once, after connecting).</summary>
public sealed record SessionInfo
{
    public string Decoder { get; init; } = "none";
    public string Renderer { get; init; } = "none";
    public string Controller { get; init; } = "none";
    public string Audio { get; init; } = "off";
    public bool LowLatencyInput { get; init; }
    public bool Remb { get; init; }
    public bool Nack { get; init; }
    public string LogPath { get; init; } = "";
    /// <summary>How the stream reached the console: home network, internet direct, or relayed.</summary>
    public string Route { get; init; } = "";
}

/// <summary>
/// One line of the player's stdout when it runs as a child of the launcher (--events). The launcher reads
/// these to show progress and stats; it sends "stop" on stdin to end the session.
/// </summary>
public sealed record PlayerEvent
{
    public const string Prefix = "@xs ";

    public string Type { get; init; } = "";           // stage | log | stats | info | summary | ready | open-settings | returned
    public PlayerStage? Stage { get; init; }
    public string? Message { get; init; }
    public LiveStats? Stats { get; init; }
    public SessionInfo? Info { get; init; }
    public SessionSummary? Summary { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ToLine() => Prefix + JsonSerializer.Serialize(this, Json);

    public static PlayerEvent? TryParse(string? line)
    {
        if (line is null || !line.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<PlayerEvent>(line.AsSpan(Prefix.Length), Json); }
        catch (JsonException) { return null; }
    }
}
