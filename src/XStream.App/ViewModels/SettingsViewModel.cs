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
    /// <summary>Quality: Sharpest (the console's top tier) or Fastest (720p). The console ignores 1080p at home, so only these two mean anything.</summary>
    [ObservableProperty] public partial bool QualitySharpest { get; set; }
    [ObservableProperty] public partial bool QualityFastest { get; set; }
    [ObservableProperty] public partial bool Enhance { get; set; }
    partial void OnEnhanceChanged(bool value) => Save(s => s with { Enhance = value });
    [ObservableProperty] public partial int ScalingIndex { get; set; }
    public string[] ScalingModes { get; } = ["Fit (whole picture, bars)", "Fill (trim edges, no bars)", "Stretch (no bars, slightly stretched)"];
    partial void OnScalingIndexChanged(int value) => Save(s => s with { Scaling = (XStream.Core.Video.VideoScaling)Math.Clamp(value, 0, 2) });
    [ObservableProperty] public partial int ColorFilterIndex { get; set; }
    public string[] ColorFilterModes { get; } = ["Off", "Red-green (deuteranopia)", "Red-green (protanopia)", "Blue-yellow (tritanopia)"];
    public bool ColorFilterOn => ColorFilterIndex > 0;
    partial void OnColorFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(ColorFilterOn));
        Save(s => s with { ColorFilter = (XStream.Core.Video.ColorFilter)Math.Clamp(value, 0, 3) });
    }
    /// <summary>Colour filter strength, 0-100% (stored as 0-1).</summary>
    [ObservableProperty] public partial double ColorFilterStrength { get; set; }
    public string ColorFilterStrengthText => $"{ColorFilterStrength:F0}%";
    partial void OnColorFilterStrengthChanged(double value)
    {
        OnPropertyChanged(nameof(ColorFilterStrengthText));
        Save(s => s with { ColorFilterStrength = Math.Round(Math.Clamp(value, 0, 100)) / 100 });
    }
    [ObservableProperty] public partial bool HideLauncherWhileStreaming { get; set; }
    [ObservableProperty] public partial bool ControllerNavigation { get; set; }
    [ObservableProperty] public partial bool LauncherFullscreen { get; set; }
    partial void OnLauncherFullscreenChanged(bool value) => Save(s => s with { LauncherFullscreen = value });
    [ObservableProperty] public partial bool MatchRefreshRate { get; set; }
    partial void OnMatchRefreshRateChanged(bool value) => Save(s => s with { MatchRefreshRate = value });
    [ObservableProperty] public partial bool Microphone { get; set; }
    partial void OnMicrophoneChanged(bool value) { Save(s => s with { Microphone = value }); if (value) LoadMicDevices(); else StopMicTest(); }

    // ---- Microphone: which one, push to talk, the controller button, and a level test ------------------
    public System.Collections.ObjectModel.ObservableCollection<string> MicDevices { get; } = [DefaultMic];
    private const string DefaultMic = "System default";
    [ObservableProperty] public partial string? MicDevice { get; set; } = DefaultMic;
    partial void OnMicDeviceChanged(string? value)
    {
        if (value is null) return;
        Save(s => s with { MicDevice = value == DefaultMic ? "" : value });
        if (MicTesting) { StopMicTest(); TestMic(); } // test the newly chosen one
    }
    [ObservableProperty] public partial bool PushToTalk { get; set; }
    partial void OnPushToTalkChanged(bool value) => Save(s => s with { PushToTalk = value });
    public string[] MicButtons { get; } = ["None", "Paddle 1 (upper right)", "Paddle 2 (lower right)", "Paddle 3 (upper left)", "Paddle 4 (lower left)", "Left stick click", "Right stick click", "View", "D-pad down"];
    private static readonly string[] MicButtonNames = ["", "Paddle1", "Paddle2", "Paddle3", "Paddle4", "LeftThumb", "RightThumb", "View", "DPadDown"];
    [ObservableProperty] public partial int MicButtonIndex { get; set; }
    partial void OnMicButtonIndexChanged(int value) => Save(s => s with { MicButton = MicButtonNames[Math.Clamp(value, 0, MicButtonNames.Length - 1)] });

    [ObservableProperty] public partial bool MicTesting { get; set; }
    [ObservableProperty] public partial double MicLevel { get; set; }
    [ObservableProperty] public partial string MicTestText { get; set; } = "";
    private XStream.Core.Audio.Microphone? _micTest;
    private Avalonia.Threading.DispatcherTimer? _micTimer;
    private DateTime _micTestEnds;
    private double _micPeak;

    private void LoadMicDevices()
    {
        string want = _store.Current.MicDevice is { Length: > 0 } d ? d : DefaultMic;
        try
        {
            var list = XStream.Core.Audio.Microphone.Devices();
            bool was = _loading;
            _loading = true; // filling the list must not save anything
            MicDevices.Clear();
            MicDevices.Add(DefaultMic);
            foreach (var n in list) MicDevices.Add(n);
            if (!MicDevices.Contains(want)) MicDevices.Add(want); // chosen before, not plugged in now
            MicDevice = want;
            _loading = was;
        }
        catch { /* no audio library: keep the default only */ }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void TestMic()
    {
        if (MicTesting) { StopMicTest(); return; }
        try
        {
            var m = new XStream.Core.Audio.Microphone();
            m.Start(_store.Current.MicDevice);
            _micTest = m;
            MicTesting = true;
            _micPeak = 0;
            _micTestEnds = DateTime.UtcNow.AddSeconds(15);
            MicTestText = $"Listening on {m.Name}. Say something…";
            _micTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(50), Avalonia.Threading.DispatcherPriority.Normal, (_, _) =>
            {
                if (_micTest is not { } t || DateTime.UtcNow > _micTestEnds) { StopMicTest(); return; }
                double level = Math.Min(100, Math.Sqrt(t.Level) * 100); // square root: speech sits mid-scale
                MicLevel = Math.Max(level, MicLevel * 0.85);
                _micPeak = Math.Max(_micPeak, level);
                MicTestText = _micPeak > 25 ? $"Hearing you on {t.Name}." : $"Listening on {t.Name}. Say something…";
            });
        }
        catch (Exception ex) { MicTesting = true; MicLevel = 0; MicTestText = "Couldn't open the microphone: " + ex.Message; _micTestEnds = DateTime.UtcNow; }
    }

    private void StopMicTest()
    {
        _micTimer?.Stop(); _micTimer = null;
        _micTest?.Dispose(); _micTest = null;
        MicTesting = false;
        MicLevel = 0;
    }
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
    [ObservableProperty] public partial bool AchievementAlerts { get; set; }
    partial void OnAchievementAlertsChanged(bool value) => Save(s => s with { AchievementAlerts = value });
    [ObservableProperty] public partial bool AchievementClips { get; set; }
    partial void OnAchievementClipsChanged(bool value) => Save(s => s with { AchievementClips = value });
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
    partial void OnQualitySharpestChanged(bool value) { if (value) Save(s => s with { Resolution = XStream.Core.Xbox.StreamResolution.Max }); }
    partial void OnQualityFastestChanged(bool value) { if (value) Save(s => s with { Resolution = XStream.Core.Xbox.StreamResolution.R720 }); }
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
        QualityFastest = s.Resolution == XStream.Core.Xbox.StreamResolution.R720;
        QualitySharpest = !QualityFastest;
        Enhance = s.Enhance;
        ScalingIndex = (int)s.Scaling;
        ColorFilterIndex = (int)s.ColorFilter;
        ColorFilterStrength = Math.Round(s.ColorFilterStrength * 100);
        HideLauncherWhileStreaming = s.HideLauncherWhileStreaming;
        ControllerNavigation = s.ControllerNavigation;
        LauncherFullscreen = s.LauncherFullscreen;
        MatchRefreshRate = s.MatchRefreshRate;
        Microphone = s.Microphone;
        PushToTalk = s.PushToTalk;
        MicButtonIndex = Math.Max(0, Array.IndexOf(MicButtonNames, s.MicButton));
        if (s.Microphone && MicDevices.Count == 1) LoadMicDevices();
        DiscordStatus = s.DiscordStatus;
        AutoConnect = s.AutoConnect;
        ConnectionAlerts = s.ConnectionAlerts;
        AchievementAlerts = s.AchievementAlerts;
        AchievementClips = s.AchievementClips;
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
