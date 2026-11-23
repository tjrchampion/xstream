using System.Text.Json;
using System.Text.Json.Serialization;
using XStream.Core.Stability;
using XStream.Core.Video;
using XStream.Core.Xbox;

namespace XStream.Core.Config;

public enum ClarityLevel { Off, Low, Medium, High }

/// <summary>User settings shared by the launcher UI and the CLI. Stored in ~/.xstream/settings.json.</summary>
public sealed record AppSettings
{
    public StreamProfile Profile { get; init; } = StreamProfile.Balanced;
    /// <summary>Ceiling the stability engine never asks above.</summary>
    public double MaxMbps { get; init; } = 30;
    /// <summary>First request before anything is measured. Lower it if the console ignores bitrate caps.</summary>
    public double StartMbps { get; init; } = 10;
    public bool Fullscreen { get; init; } = true;
    public bool VSync { get; init; }
    public bool HardwareDecode { get; init; } = true;
    /// <summary>Highest resolution to ask for. Max lets the console pick its top tier (1440p, or 4K where offered).</summary>
    public StreamResolution Resolution { get; init; } = StreamResolution.Max;
    public string Locale { get; init; } = "en-GB";
    /// <summary>Hide the launcher while a stream is running.</summary>
    public bool HideLauncherWhileStreaming { get; init; } = true;
    /// <summary>Open the launcher full screen. On by default on Windows (ROG Ally and other handhelds).</summary>
    public bool LauncherFullscreen { get; init; } = OperatingSystem.IsWindows();
    /// <summary>Move around the launcher with a controller (D-pad/stick, A select, B back, LB/RB switch page).</summary>
    public bool ControllerNavigation { get; init; } = true;
    public string? LastServerId { get; init; }
    /// <summary>Controller rumble strength, 0-100%.</summary>
    public int RumbleStrength { get; init; } = 100;
    /// <summary>Impulse-trigger rumble. Off by default, like the xbox.com client (racing games buzz the triggers constantly).</summary>
    public bool TriggerRumble { get; init; }
    /// <summary>Small stats readout over the video (toggle from the in-stream menu too).</summary>
    public bool ShowStatsHud { get; init; }
    public bool Audio { get; init; } = true;
    /// <summary>Game audio volume, 0-100%.</summary>
    public int Volume { get; init; } = 100;
    /// <summary>Sharpening applied before the picture is scaled up to the display.</summary>
    public ClarityLevel Clarity { get; init; } = ClarityLevel.Low;
    /// <summary>How a 16:9 stream fills a screen of another shape (e.g. a 16:10 Mac): Fit (bars), Fill (trim edges), Stretch.</summary>
    public VideoScaling Scaling { get; init; } = VideoScaling.Fit;

    /// <summary>Switch the display to 60 Hz for the stream (the console sends 60 fps) and back afterwards. Windows.</summary>
    public bool MatchRefreshRate { get; init; } = OperatingSystem.IsWindows();
    /// <summary>Send the microphone to the console for party and game chat.</summary>
    public bool Microphone { get; init; }
    /// <summary>Show "Playing … via XStream" on Discord while streaming.</summary>
    public bool DiscordStatus { get; init; } = true;
    /// <summary>Discord application id for the status (overrides the built-in one).</summary>
    public string? DiscordAppId { get; init; }
    /// <summary>Short in-stream messages when the connection or frame rate suffers (and when it recovers), and low battery.</summary>
    public bool ConnectionAlerts { get; init; } = true;
    /// <summary>Check for, download and offer new versions automatically (Windows build).</summary>
    public bool AutoUpdate { get; init; } = true;
    /// <summary>Release feed (latest.json) override; empty uses the built-in one.</summary>
    public string? UpdateFeedUrl { get; init; }
    /// <summary>
    /// STUN/TURN servers for reaching the console from outside the home network ("url" or "url;username;password").
    /// Empty uses the built-in public STUN servers. Add a TURN server for networks where a direct path is impossible.
    /// </summary>
    public string[]? IceServers { get; init; }
    /// <summary>Connect to the last console as soon as XStream opens.</summary>
    public bool AutoConnect { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.Settings;
        for (int attempt = 0; ; attempt++)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new(); }
            catch (JsonException) { return new(); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(15); } // being replaced by the other process
            catch (IOException) { return new(); }
        }
    }

    /// <summary>
    /// The launcher and a running stream both write this file, so replace it atomically: the other side
    /// reads either the old or the new settings, never a half-written file (which would read as defaults).
    /// </summary>
    public void Save(string? path = null)
    {
        path ??= AppPaths.Settings;
        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(15); }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(15); }
        }
    }

    public StabilityOptions ToStabilityOptions() => new()
    {
        Profile = Profile,
        MaxKbps = MaxMbps * 1000,
        StartKbps = Math.Min(StartMbps, MaxMbps) * 1000,
    };
}
