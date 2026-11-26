using System.Text.Json;
using System.Text.Json.Serialization;
using XStream.Core.Stability;
using XStream.Core.Video;
using XStream.Core.Xbox;

namespace XStream.Core.Config;

public enum ClarityLevel { Off, Low, Medium, High }

/// <summary>User settings shared by the launcher UI and the CLI. Stored in ~/.xstream/settings.json.</summary>
/// <summary>A controller setup for one game (or the default, with an empty key).</summary>
public sealed record ControllerProfile(string GameKey, string GameName, string ButtonMapping = "", bool GyroAim = false,
    double GyroSensitivity = 1, string GyroHoldButton = "")
{
    public bool NeedsExtras => GyroAim || ButtonMapping.Contains("Paddle", StringComparison.OrdinalIgnoreCase)
                               || GyroHoldButton.StartsWith("Paddle", StringComparison.OrdinalIgnoreCase);

    /// <summary>Same controls, ignoring which game it's for.</summary>
    public bool SameSetup(ControllerProfile o) =>
        ButtonMapping == o.ButtonMapping && GyroAim == o.GyroAim && GyroSensitivity == o.GyroSensitivity && GyroHoldButton == o.GyroHoldButton;

    /// <summary>A short description ("Swap A/B, gyro 1.5x while LB").</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            parts.Add(ButtonMapping.Length == 0 ? "standard buttons" : "remapped (" + ButtonMapping + ")");
            if (GyroAim) parts.Add($"gyro {GyroSensitivity:0.##}x" + (GyroHoldButton.Length > 0 ? " while " + GyroHoldButton + " is held" : ""));
            return string.Join(", ", parts);
        }
    }
}

public sealed record AppSettings
{
    public StreamProfile Profile { get; init; } = StreamProfile.Balanced;
    /// <summary>Ceiling the stability engine never asks above.</summary>
    /// <summary>
    /// Was 30: on a ROG Ally on good Wi-Fi (4 ms, ~0.1% loss) the stream sat at that cap for a whole 29-minute session,
    /// i.e. Vexel was holding the console back. The bitrate controller still steers below this on a congested link.
    /// </summary>
    public double MaxMbps { get; init; } = 50;
    /// <summary>First request before anything is measured. Lower it if the console ignores bitrate caps.</summary>
    public double StartMbps { get; init; } = 20;
    public bool Fullscreen { get; init; } = true;
    public bool VSync { get; init; }
    public bool HardwareDecode { get; init; } = true;
    /// <summary>Highest resolution to ask for. Max lets the console pick its top tier (1440p, or 4K where offered).</summary>
    /// <summary>
    /// Max: the console's top tier (1440p, or 4K where offered), on every device. 0.4.2 asked handhelds for 1080p
    /// ("Match display"); on a ROG Ally the Xbox app still looked better, so the default is back to the highest.
    /// </summary>
    public StreamResolution Resolution { get; init; } = StreamResolution.Max;
    /// <summary>Which defaults this file was written with (see <see cref="Migrate"/>). 0: before 0.4.2.</summary>
    public int SettingsVersion { get; init; }
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
    /// <summary>The stats readout as a detailed panel (latency breakdown, network, picture) instead of one line.</summary>
    public bool StatsDetailed { get; init; }
    public bool Audio { get; init; } = true;
    /// <summary>Game audio volume, 0-100%.</summary>
    public int Volume { get; init; } = 100;
    /// <summary>Sharpening applied before the picture is scaled up to the display.</summary>
    public ClarityLevel Clarity { get; init; } = ClarityLevel.Low;
    /// <summary>
    /// Enhance: when the stream is smaller than the screen (720p on a 1080p+ display, 1440p on 4K), upscale it
    /// edge-aware (FSR 1 style) instead of bilinear, so text and edges stay sharp.
    /// </summary>
    public bool Enhance { get; init; } = true;
    /// <summary>How a 16:9 stream fills a screen of another shape (e.g. a 16:10 Mac): Fit (bars), Fill (trim edges), Stretch.</summary>
    public VideoScaling Scaling { get; init; } = VideoScaling.Fit;

    /// <summary>Switch the display to 60 Hz for the stream (the console sends 60 fps) and back afterwards. Windows.</summary>
    public bool MatchRefreshRate { get; init; } = OperatingSystem.IsWindows();
    /// <summary>Send the microphone to the console for party and game chat.</summary>
    public bool Microphone { get; init; }
    /// <summary>Push to talk: the mic only sends while <see cref="MicButton"/> (or F9) is held. Off: open mic, F9 / the button mutes.</summary>
    public bool PushToTalk { get; init; }
    /// <summary>Controller button for the mic ("" = none): toggles mute, or is held to talk. Paddle1-4 suit it best (they aren't sent to the game).</summary>
    public string MicButton { get; init; } = "";
    /// <summary>Microphone to use (its name as the system lists it); "" = the system default.</summary>
    public string MicDevice { get; init; } = "";
    /// <summary>Show "Playing … via Vexel" on Discord while streaming.</summary>
    public bool DiscordStatus { get; init; } = true;
    /// <summary>Discord application id for the status (overrides the built-in one).</summary>
    public string? DiscordAppId { get; init; }
    /// <summary>Short in-stream messages when the connection or frame rate suffers (and when it recovers), and low battery.</summary>
    public bool ConnectionAlerts { get; init; } = true;
    /// <summary>"Achievement unlocked: Name · 20G" over the game when one unlocks during a stream.</summary>
    public bool AchievementAlerts { get; init; } = true;
    /// <summary>Check for, download and offer new versions automatically (Windows build).</summary>
    public bool AutoUpdate { get; init; } = true;
    /// <summary>Release feed (latest.json) override; empty uses the built-in one.</summary>
    public string? UpdateFeedUrl { get; init; }
    /// <summary>
    /// STUN/TURN servers for reaching the console from outside the home network ("url" or "url;username;password").
    /// Empty uses the built-in public STUN servers. Add a TURN server for networks where a direct path is impossible.
    /// </summary>
    public string[]? IceServers { get; init; }
    /// <summary>Cloud page: show all games as a list instead of a grid of covers.</summary>
    public bool CloudListView { get; init; }
    /// <summary>Connect to the last console as soon as Vexel opens.</summary>
    public bool AutoConnect { get; init; }
    /// <summary>Instant replay: keep the last <see cref="ReplaySeconds"/> so "Save last 30 seconds" (F10, or the stream menu) can save them.</summary>
    public bool InstantReplay { get; init; }
    public int ReplaySeconds { get; init; } = 30;
    /// <summary>
    /// Controller remapping, "From=To" pairs separated by commas (e.g. "A=B,B=A,Paddle1=LeftThumb"). Sources are
    /// any Xbox button or Paddle1-4 (Elite / DualSense Edge back buttons); "To" may be None to turn a button off.
    /// </summary>
    public string ButtonMapping { get; init; } = "";
    /// <summary>Aim with the controller's gyro (DualSense, DualShock 4, Switch Pro): tilting moves the right stick.</summary>
    public bool GyroAim { get; init; }
    /// <summary>Gyro strength (1 = default; 0.25 to 4).</summary>
    public double GyroSensitivity { get; init; } = 1;
    /// <summary>Gyro only while this button is held ("" = always on). Usually LeftShoulder or a paddle.</summary>
    public string GyroHoldButton { get; init; } = "";
    /// <summary>Play with keyboard and mouse in the stream window (a virtual controller). Auto: only while no controller is connected.</summary>
    public XStream.Core.Input.KeyboardMouseMode KeyboardMouse { get; init; } = XStream.Core.Input.KeyboardMouseMode.Auto;
    /// <summary>Keyboard-and-mouse keys, as changes from the default layout ("A=Space,RightTrigger=MouseLeft"; see KeyboardBindings).</summary>
    public string KeyboardMouseBindings { get; init; } = "";
    /// <summary>Mouse aim speed on the right stick (1 = default; 0.25 to 4).</summary>
    public double MouseSensitivity { get; init; } = 1;
    /// <summary>Mouse forward looks down.</summary>
    public bool MouseInvertY { get; init; }
    /// <summary>
    /// Per-game controller setups: used instead of the ones above while that game is running (the stream switches
    /// when the game changes). Keyed by Xbox title id, or "cloud:ID" for a cloud game.
    /// </summary>
    public ControllerProfile[]? GameProfiles { get; init; }

    /// <summary>The default controller setup (the fields above) as a profile.</summary>
    public ControllerProfile DefaultController => new("", "All games", ButtonMapping, GyroAim, GyroSensitivity, GyroHoldButton);

    /// <summary>The controller setup for a game: its own profile if it has one, else the default.</summary>
    public ControllerProfile ControllerFor(string? gameKey) =>
        gameKey is { Length: > 0 } && GameProfiles?.FirstOrDefault(p => p.GameKey.Equals(gameKey, StringComparison.OrdinalIgnoreCase)) is { } p
            ? p : DefaultController;

    /// <summary>Add or replace a game's profile.</summary>
    public AppSettings WithProfile(ControllerProfile profile) => this with
    {
        GameProfiles = [.. (GameProfiles ?? []).Where(p => !p.GameKey.Equals(profile.GameKey, StringComparison.OrdinalIgnoreCase)), profile],
    };

    public AppSettings WithoutProfile(string gameKey) => this with
    {
        GameProfiles = (GameProfiles ?? []).Where(p => !p.GameKey.Equals(gameKey, StringComparison.OrdinalIgnoreCase)).ToArray() is { Length: > 0 } rest ? rest : null,
    };

    /// <summary>Whether any setup (default or a game's) needs gyro or paddles, which only the SDL2 controller backend reads.</summary>
    public bool NeedsControllerExtras => new[] { DefaultController }.Concat(GameProfiles ?? []).Any(p => p.NeedsExtras);

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
            try { return Migrate(File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new()); }
            catch (JsonException) { return new(); }
            catch (IOException) when (attempt < 5) { Thread.Sleep(15); } // being replaced by the other process
            catch (IOException) { return new(); }
        }
    }

    public const int CurrentVersion = 5;

    /// <summary>
    /// Every setting is written to the file, so a changed default never reached anyone who already had one. Settings
    /// still at an old default move to the new one; anything the user chose is kept.
    /// </summary>
    public static AppSettings Migrate(AppSettings s)
    {
        if (s.SettingsVersion >= CurrentVersion) return s;
        if (s.SettingsVersion < 2) // 0.4.2: picture quality to match the Xbox app
            s = s with
            {
                MaxMbps = s.MaxMbps == 30 ? 50 : s.MaxMbps,
                StartMbps = s.StartMbps == 10 ? 20 : s.StartMbps,
                Resolution = s.Resolution == StreamResolution.Max ? StreamResolution.Auto : s.Resolution,
            };
        if (s.SettingsVersion < 3 && s.Resolution == StreamResolution.Auto) // 0.4.3: back to the highest resolution
            s = s with { Resolution = StreamResolution.Max };
        if (s.SettingsVersion < 4) // 0.4.8: gyro aim off by default (it could be saved as on without being chosen)
            s = s with { GyroAim = false };
        if (s.SettingsVersion < 5 && s.Resolution is StreamResolution.R1080 or StreamResolution.Auto) // 0.5.0: Quality is Sharpest or Fastest
            s = s with { Resolution = StreamResolution.Max }; // the console ignored 1080p at home and sent its top tier anyway
        return s with { SettingsVersion = CurrentVersion };
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
