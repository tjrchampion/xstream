using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;
using XStream.Core.Config;
using XStream.Core.Native;
using XStream.Core.Stability;
using XStream.WebRtc;

namespace XStream.App.ViewModels;

public sealed partial class ProfileOption(StreamProfile profile, string title, string blurb, string detail, SettingsViewModel owner) : ObservableObject
{
    public StreamProfile Profile { get; } = profile;
    public string Title { get; } = title;
    public string Blurb { get; } = blurb;
    public string Detail { get; } = detail;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) owner.SelectProfile(Profile);
    }
}

public sealed partial class ClarityOption(ClarityLevel level, string title, SettingsViewModel owner) : ObservableObject
{
    public ClarityLevel Level { get; } = level;
    public string Title { get; } = title;
    [ObservableProperty] public partial bool IsSelected { get; set; }
    partial void OnIsSelectedChanged(bool value)
    {
        if (value) owner.SelectClarity(Level);
    }
}

public sealed record CheckItem(string Name, bool Ok, string Detail, string? Fix)
{
    public bool HasFix => Fix is not null;
}

/// <summary>Settings page. Every change is saved straight away; the next stream uses it.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private bool _loading;

    public event Action? SignOutRequested;

    public SettingsViewModel(SettingsStore store)
    {
        _store = store;
        Profiles =
        [
            new(StreamProfile.Competitive, "Competitive", "Lowest latency",
                "Almost no buffer and fast reactions. For shooters and fighting games on a good network.", this),
            new(StreamProfile.Balanced, "Balanced", "Recommended",
                "A small buffer that grows only when the network gets rough.", this),
            new(StreamProfile.Smooth, "Smooth", "Most stable picture",
                "Bigger buffer and more bitrate headroom. For RPGs and slower games, or busy Wi-Fi.", this),
        ];
        ClarityOptions =
        [
            new(ClarityLevel.Off, "Off", this), new(ClarityLevel.Low, "Low", this),
            new(ClarityLevel.Medium, "Medium", this), new(ClarityLevel.High, "High", this),
        ];
        Load(store.Current);
        store.Changed += cur => { if (!_saving) Load(cur); }; // changes made from the in-stream menu
        RunChecks();
    }

    public ObservableCollection<ProfileOption> Profiles { get; }
    public ObservableCollection<CheckItem> Checks { get; } = [];
    public ObservableCollection<ClarityOption> ClarityOptions { get; }
    public string DataFolder => AppPaths.Root;

    [ObservableProperty] public partial double MaxMbps { get; set; }
    [ObservableProperty] public partial double StartMbps { get; set; }
    [ObservableProperty] public partial bool Fullscreen { get; set; }
    [ObservableProperty] public partial bool VSync { get; set; }
    [ObservableProperty] public partial bool HardwareDecode { get; set; }
    [ObservableProperty] public partial int ResolutionIndex { get; set; }
    [ObservableProperty] public partial int ScalingIndex { get; set; }
    public string[] ScalingModes { get; } = ["Fit (whole picture, bars)", "Fill (trim edges, no bars)", "Stretch (no bars, slightly stretched)"];
    partial void OnScalingIndexChanged(int value) => Save(s => s with { Scaling = (XStream.Core.Video.VideoScaling)Math.Clamp(value, 0, 2) });
    // Listed in this order; the enum's order (Max, R1080, R720, Auto) is what's stored.
    public string[] Resolutions { get; } = ["Maximum (1440p / 4K where offered)", "Match display (1080p on 1080p screens)", "1080p", "720p (lowest delay, low bandwidth)"];
    private static readonly XStream.Core.Xbox.StreamResolution[] ResolutionOrder =
        [XStream.Core.Xbox.StreamResolution.Max, XStream.Core.Xbox.StreamResolution.Auto, XStream.Core.Xbox.StreamResolution.R1080, XStream.Core.Xbox.StreamResolution.R720];
    [ObservableProperty] public partial bool HideLauncherWhileStreaming { get; set; }
    [ObservableProperty] public partial bool ControllerNavigation { get; set; }
    [ObservableProperty] public partial bool LauncherFullscreen { get; set; }
    partial void OnLauncherFullscreenChanged(bool value) => Save(s => s with { LauncherFullscreen = value });
    [ObservableProperty] public partial bool MatchRefreshRate { get; set; }
    partial void OnMatchRefreshRateChanged(bool value) => Save(s => s with { MatchRefreshRate = value });
    [ObservableProperty] public partial bool Microphone { get; set; }
    partial void OnMicrophoneChanged(bool value) => Save(s => s with { Microphone = value });
    [ObservableProperty] public partial bool DiscordStatus { get; set; }
    partial void OnDiscordStatusChanged(bool value) => Save(s => s with { DiscordStatus = value });
    [ObservableProperty] public partial bool AutoUpdate { get; set; }
    partial void OnAutoUpdateChanged(bool value) => Save(s => s with { AutoUpdate = value });
    [ObservableProperty] public partial string UpdateStatus { get; set; } = "";
    public string VersionText => $"Version {Updater.Current}";
    /// <summary>Set by the shell.</summary>
    public Updater? Updates { get; set; }
    [RelayCommand] private async Task CheckForUpdates() { if (Updates is not null) await Updates.CheckAsync(userAsked: true); }
    /// <summary>A verified update is downloaded and can be installed now.</summary>
    [ObservableProperty] public partial bool CanInstallUpdate { get; set; }
    /// <summary>A newer version exists but this build can't install it (macOS, dev builds): link to the download.</summary>
    [ObservableProperty] public partial bool UpdateAvailableElsewhere { get; set; }
    public event Action? InstallUpdateRequested;
    [RelayCommand] private void InstallUpdate() => InstallUpdateRequested?.Invoke();
    [RelayCommand] private void OpenReleasesPage()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Updater.ReleasesPage) { UseShellExecute = true }); }
        catch { UpdateStatus = "Open " + Updater.ReleasesPage + " to download it."; }
    }
    [ObservableProperty] public partial bool ConnectionAlerts { get; set; }
    partial void OnConnectionAlertsChanged(bool value) => Save(s => s with { ConnectionAlerts = value });
    [ObservableProperty] public partial bool AutoConnect { get; set; }
    partial void OnAutoConnectChanged(bool value) => Save(s => s with { AutoConnect = value });
    public bool IsWindows => OperatingSystem.IsWindows();
    [ObservableProperty] public partial string ControllerStatus { get; set; } = "";
    [ObservableProperty] public partial bool Audio { get; set; }
    [ObservableProperty] public partial double Volume { get; set; }
    [ObservableProperty] public partial double RumbleStrength { get; set; }
    [ObservableProperty] public partial bool TriggerRumble { get; set; }

    public string VolumeText => $"{Volume:F0}%";
    public string RumbleText => RumbleStrength <= 0 ? "Off" : $"{RumbleStrength:F0}%";
    partial void OnAudioChanged(bool value) => Save(s => s with { Audio = value });
    partial void OnVolumeChanged(double value) { OnPropertyChanged(nameof(VolumeText)); Save(s => s with { Volume = (int)Math.Round(value) }); }
    partial void OnRumbleStrengthChanged(double value) { OnPropertyChanged(nameof(RumbleText)); Save(s => s with { RumbleStrength = (int)Math.Round(value) }); }
    partial void OnTriggerRumbleChanged(bool value) => Save(s => s with { TriggerRumble = value });

    // ---- Instant replay ---------------------------------------------------------------------------------
    [ObservableProperty] public partial bool InstantReplay { get; set; }
    partial void OnInstantReplayChanged(bool value) => Save(s => s with { InstantReplay = value });

    // ---- Sections: Settings shows one at a time ---------------------------------------------------------
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsStream), nameof(IsSound), nameof(IsLauncher), nameof(IsSystem))]
    public partial string Section { get; set; } = "Stream";
    public bool IsStream => Section == "Stream";
    public bool IsSound => Section == "Sound";
    public bool IsLauncher => Section == "Launcher";
    public bool IsSystem => Section == "System";
    [RelayCommand] private void ShowSection(string? name) { if (name is "Stream" or "Sound" or "Launcher" or "System") Section = name; }

    /// <summary>"Open Mapping": button layouts and gyro have their own page.</summary>
    public event Action? OpenMappingRequested;
    [RelayCommand] private void OpenMapping() => OpenMappingRequested?.Invoke();

    // ---- Shortcuts ---------------------------------------------------------------------------------------
    /// <summary>What to put in a shortcut or Steam's "Add a non-Steam game": this app and its launch options.</summary>
    public string ShortcutTarget => Environment.ProcessPath is { } exe && !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? exe : "Vexel.exe";
    public string ShortcutOptions => _store.Current.LastServerId is { Length: > 0 } ? "--stream" : "--stream \"Console name\"";
    [ObservableProperty] public partial string ShortcutStatus { get; set; } = "";

    internal void SelectClarity(ClarityLevel level)
    {
        foreach (var o in ClarityOptions) o.IsSelected = o.Level == level;
        Save(s => s with { Clarity = level });
    }

    public string MaxMbpsText => $"{MaxMbps:F0} Mbps";
    public string StartMbpsText => $"{Math.Min(StartMbps, MaxMbps):F0} Mbps";

    partial void OnMaxMbpsChanged(double value)
    {
        OnPropertyChanged(nameof(MaxMbpsText));
        OnPropertyChanged(nameof(StartMbpsText));
        Save(s => s with { MaxMbps = Math.Round(value) });
    }
    partial void OnStartMbpsChanged(double value)
    {
        OnPropertyChanged(nameof(StartMbpsText));
        Save(s => s with { StartMbps = Math.Round(value) });
    }
    partial void OnFullscreenChanged(bool value) => Save(s => s with { Fullscreen = value });
    partial void OnVSyncChanged(bool value) => Save(s => s with { VSync = value });
    partial void OnHardwareDecodeChanged(bool value) => Save(s => s with { HardwareDecode = value });
    partial void OnResolutionIndexChanged(int value) => Save(s => s with { Resolution = ResolutionOrder[Math.Clamp(value, 0, ResolutionOrder.Length - 1)] });
    partial void OnHideLauncherWhileStreamingChanged(bool value) => Save(s => s with { HideLauncherWhileStreaming = value });
    partial void OnControllerNavigationChanged(bool value) => Save(s => s with { ControllerNavigation = value });

    internal void SelectProfile(StreamProfile p)
    {
        foreach (var o in Profiles) o.IsSelected = o.Profile == p;
        Save(s => s with { Profile = p });
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        var keep = _store.Current.LastServerId;
        _store.Update(_ => new AppSettings { LastServerId = keep });
        Load(_store.Current);
    }

    [RelayCommand]
    private void RunChecks()
    {
        Checks.Clear();
        try
        {
            foreach (var c in SystemCheck.Run()) Checks.Add(new CheckItem(c.Name, c.Ok, c.Detail, c.Fix));
        }
        catch (Exception ex) { Checks.Add(new CheckItem("Native libraries", false, ex.Message, null)); }
        bool lowLatency = XboxPeer.UnreliableInputSupported;
        Checks.Add(new CheckItem("Low-latency input channel", lowLatency,
            lowLatency ? "Patched WebRTC library: lost controller packets never hold up later ones"
                       : "Using the standard reliable channel",
            lowLatency ? null : "Run scripts/get-sipsorcery.sh (Windows: scripts/get-sipsorcery.ps1) and rebuild"));
    }

    [RelayCommand] private void OpenDataFolder() => Platform.Open(AppPaths.Root);

    /// <summary>Set by the shell: the stream's log goes into the diagnostics zip.</summary>
    public StreamViewModel? Stream { get; set; }
    [ObservableProperty] public partial string DiagnosticsStatus { get; set; } = "";

    [RelayCommand]
    private void ExportDiagnostics()
    {
        var path = Stream?.ExportDiagnostics() ?? TryExport();
        DiagnosticsStatus = path is null ? "Couldn't create the diagnostics file." : "Saved " + Path.GetFileName(path) + " to " + Path.GetDirectoryName(path);
        static string? TryExport() { try { return Diagnostics.Export([]); } catch { return null; } }
    }
    [RelayCommand] private void SignOut() => SignOutRequested?.Invoke();

    private void Load(AppSettings s)
    {
        _loading = true;
        foreach (var o in Profiles) o.IsSelected = o.Profile == s.Profile;
        MaxMbps = s.MaxMbps;
        StartMbps = s.StartMbps;
        Fullscreen = s.Fullscreen;
        VSync = s.VSync;
        HardwareDecode = s.HardwareDecode;
        ResolutionIndex = Math.Max(0, Array.IndexOf(ResolutionOrder, s.Resolution));
        ScalingIndex = (int)s.Scaling;
        HideLauncherWhileStreaming = s.HideLauncherWhileStreaming;
        ControllerNavigation = s.ControllerNavigation;
        LauncherFullscreen = s.LauncherFullscreen;
        MatchRefreshRate = s.MatchRefreshRate;
        Microphone = s.Microphone;
        DiscordStatus = s.DiscordStatus;
        AutoConnect = s.AutoConnect;
        ConnectionAlerts = s.ConnectionAlerts;
        AutoUpdate = s.AutoUpdate;
        Audio = s.Audio;
        Volume = s.Volume;
        RumbleStrength = s.RumbleStrength;
        TriggerRumble = s.TriggerRumble;
        InstantReplay = s.InstantReplay;
        foreach (var o in ClarityOptions) o.IsSelected = o.Level == s.Clarity;
        _loading = false;
    }

    private bool _saving;
    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (_loading) return;
        _saving = true;
        try { _store.Update(change); } finally { _saving = false; }
    }
}
