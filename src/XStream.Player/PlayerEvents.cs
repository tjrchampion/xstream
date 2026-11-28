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
    /// <summary>Game audio waiting to play (how far the sound runs behind what arrived).</summary>
    public double AudioQueuedMs { get; init; }
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

/// <summary>Play together, for the launcher (and Discord's party size): who's in and the codes to show.</summary>
public sealed record TogetherStatus
{
    /// <summary>"host" (sharing this stream) or "guest" (watching a friend's).</summary>
    public string Role { get; init; } = "host";
    /// <summary>Everyone playing, the host included (1 = sharing but nobody has joined yet; 0 = not sharing).</summary>
    public int Players { get; init; }
    public string[] Guests { get; init; } = [];
    public string? Invite { get; init; }
    /// <summary>Guest: the code to send the host when the host's router needs both sides to connect at once.</summary>
    public string? ReplyCode { get; init; }
    /// <summary>Guest: whose stream it is.</summary>
    public string? HostName { get; init; }
    /// <summary>Guest: "Player 2" etc. once in.</summary>
    public int Slot { get; init; } = -1;
    /// <summary>Host: each guest with their slot and connection (newer children only; <see cref="Guests"/> stays for older readers).</summary>
    public TogetherPlayer[]? People { get; init; }
    /// <summary>Host: the console's bitrate cap while anyone is in (Mbps), null when not capped.</summary>
    public double? CapMbps { get; init; }
    /// <summary>Host: a passing message for whoever shows the panel ("Reply code accepted…", or why it wasn't).</summary>
    public string? Note { get; init; }
    /// <summary>The note is a problem (shown as a warning).</summary>
    public bool NoteIsProblem { get; init; }
}

/// <summary>One guest in the host's <see cref="TogetherStatus"/>. Ping is NaN until the first one comes back.</summary>
public sealed record TogetherPlayer
{
    /// <summary>Controller slot (0 is the host's first controller, so slot 1 is Player 2).</summary>
    public int Slot { get; init; }
    public string Name { get; init; } = "";
    public double PingMs { get; init; } = double.NaN;
    public double LossPct { get; init; }
}

/// <summary>
/// One line of the player's stdout when it runs as a child of the launcher (--events). The launcher reads
/// these to show progress and stats; it sends "stop" on stdin to end the session.
/// </summary>
public sealed record PlayerEvent
{
    public const string Prefix = "@xs ";

    public string Type { get; init; } = "";           // stage | log | stats | info | summary | ready | open-settings | returned | together
    public PlayerStage? Stage { get; init; }
    public string? Message { get; init; }
    public LiveStats? Stats { get; init; }
    public SessionInfo? Info { get; init; }
    public SessionSummary? Summary { get; init; }
    public TogetherStatus? Together { get; init; }

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
