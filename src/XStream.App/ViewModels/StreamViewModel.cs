using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;
using XStream.Core.Config;
using XStream.Core.Stability;
using XStream.Player;

namespace XStream.App.ViewModels;

public enum StepState { Pending, Active, Done, Failed }

public sealed partial class StepViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPending), nameof(IsActive), nameof(IsDone), nameof(IsFailed))]
    public partial StepState State { get; set; }
    public bool IsPending => State == StepState.Pending;
    public bool IsActive => State == StepState.Active;
    public bool IsDone => State == StepState.Done;
    public bool IsFailed => State == StepState.Failed;
}

/// <summary>The running (or just-finished) stream: progress, live numbers, and the end-of-session summary.</summary>
public sealed partial class StreamViewModel : ObservableObject
{
    private const int HistoryLength = 60; // one minute of once-a-second samples
    private readonly StreamLauncher _launcher;
    private readonly DispatcherTimer _clock;
    private DateTime _startedAt;
    private PlayerStage _stage = PlayerStage.Starting;

    public event Action<bool>? RunningChanged;

    public ObservableCollection<StepViewModel> Steps { get; } =
    [
        new("Sign-in"), new("Request session"), new("Wake console"), new("Connect"), new("Streaming"),
    ];
    public ObservableCollection<string> LogLines { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPanel), nameof(IsConnecting), nameof(ShowLoader), nameof(ShowStartScreen), nameof(ShowSummary), nameof(HasHero),
        nameof(CanShare), nameof(IsSharing), nameof(ShareNotReady), nameof(GuestText))] public partial bool IsRunning { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPanel), nameof(ShowSummary), nameof(HasHero))] public partial bool HasResult { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsConnecting), nameof(ShowLoader), nameof(ShowStartScreen), nameof(HasHero))] public partial bool IsStreaming { get; set; }
    [ObservableProperty] public partial bool IsReconnecting { get; set; }
    [ObservableProperty] public partial bool IsStopping { get; set; }
    /// <summary>The launcher's Settings page is open over a running stream (from "More settings…").</summary>
    [ObservableProperty] public partial bool IsAway { get; set; }
    public event Action? AwayChanged;
    partial void OnIsAwayChanged(bool value) => AwayChanged?.Invoke();
    [ObservableProperty] public partial string ConsoleName { get; set; } = "";
    [ObservableProperty] public partial string ServerId { get; set; } = "";
    [ObservableProperty] public partial string StageText { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string Elapsed { get; set; } = "0:00";
    [ObservableProperty] public partial string ProfileText { get; set; } = "";

    // Live numbers
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LiveDetail))] public partial LiveStats? Live { get; set; }
    /// <summary>Under the frame rate: the resolution the console is sending, and loss. "1440p  ·  0.1% loss".</summary>
    public string LiveDetail => Live is not { } l ? "" : string.Join("  ·  ",
        new[] { l.VideoHeight > 0 ? $"{l.VideoHeight}p" : null, $"{l.LossPct:F1}% loss" }.Where(x => x is not null));
    [ObservableProperty] public partial string Verdict { get; set; } = "";
    [ObservableProperty] public partial string VerdictKind { get; set; } = "good"; // good | warn | bad
    [ObservableProperty] public partial double[] BitrateHistory { get; set; } = [];
    [ObservableProperty] public partial double[] PingHistory { get; set; } = [];
    [ObservableProperty] public partial double[] LatencyHistory { get; set; } = [];
    [ObservableProperty] public partial SessionInfo? Info { get; set; }

    // After the session
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SummaryLine), nameof(HasSummaryNumbers))] public partial SessionSummary? Summary { get; set; }

    // "Now playing" header on the Consoles page: live while streaming, a short summary after (not while connecting:
    // the full-screen connecting screen covers that, and a second progress display under it only flickered).
    public bool ShowSummary => HasResult && !IsRunning && !StartFailed;
    public bool HasHero => IsStreaming || ShowSummary;
    public bool HasSummaryNumbers => Summary is not null;
    /// <summary>One line for the finished session: "23 min · no freezes · 27.4 Mbps · 4 ms ping · 0.1% loss".</summary>
    public string SummaryLine => Summary is not { } s ? "" : string.Join("  ·  ",
        s.Minutes >= 1 ? $"{s.Minutes:F0} min" : $"{s.Minutes * 60:F0} s",
        s.Freezes switch { 0 => "no freezes", 1 => "1 freeze", var n => $"{n} freezes" },
        $"{s.AvgMbps:F1} Mbps",
        $"{s.MedianRttMs:F0} ms ping",
        $"{s.AvgLossPct:F1}% loss");
    [ObservableProperty] public partial string SummaryPath { get; set; } = "";

    public bool HasPanel => IsRunning || HasResult;
    public bool IsConnecting => IsRunning && !IsStreaming;
    public bool HasError => Error.Length > 0;
    partial void OnErrorChanged(string value) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ShowLoader)); OnPropertyChanged(nameof(ShowStartScreen)); }

    // Full-screen connecting screen
    /// <summary>The full-screen connecting screen is up (until the picture arrives, or it fails).</summary>
    public bool ShowLoader => IsConnecting && !HasError;
    /// <summary>
    /// The stream never got its picture (it failed, or the stream process closed early). The connecting screen
    /// stays up and says why, with Try again / Close, on whatever page Stream was pressed. It used to vanish,
    /// leaving only a result card that exists on the Consoles page: on any other page the start just "flickered".
    /// </summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowStartScreen), nameof(ShowSummary), nameof(HasHero))] public partial bool StartFailed { get; set; }
    /// <summary>The full-screen connecting screen is up: connecting, or showing why the start failed.</summary>
    public bool ShowStartScreen => ShowLoader || StartFailed;
    private bool _everStreamed, _cancelled;
    /// <summary>Key art from recently played games for the connecting screen.</summary>
    [ObservableProperty] public partial IReadOnlyList<GameArt> Art { get; set; } = [];
    [ObservableProperty] public partial string Tip { get; set; } = "";
    /// <summary>The launcher sees no controller (shown as a warning on the connecting screen).</summary>
    [ObservableProperty] public partial bool NoController { get; set; }
    [ObservableProperty] public partial int StepNumber { get; set; } = 1;
    /// <summary>Connecting screen progress, 0-1. Fills smoothly within each stage, never goes backwards.</summary>
    [ObservableProperty] public partial double Progress { get; set; }
    /// <summary>Plain-language stage for the connecting screen.</summary>
    [ObservableProperty] public partial string LoaderText { get; set; } = "";
    /// <summary>0 waking, 1 connecting, 2 starting the stream (lights up the labels under the bar).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PhaseConnect), nameof(PhaseStream))] public partial int Phase { get; set; }
    public bool PhaseConnect => Phase >= 1;
    public bool PhaseStream => Phase >= 2;

    // Where each stage sits on the bar, and roughly how long it takes (sets how fast the bar creeps).
    private static (double from, double to, double seconds) Band(PlayerStage s) => s switch
    {
        PlayerStage.Starting => (0.00, 0.10, 1),
        PlayerStage.RequestingSession => (0.10, 0.20, 1.5),
        PlayerStage.WakingConsole => (0.20, 0.72, 10),
        PlayerStage.InQueue => (0.25, 0.70, 60),
        PlayerStage.Connecting => (0.72, 0.90, 3),
        PlayerStage.StartingVideo => (0.90, 0.99, 1.5),
        _ => (0.96, 1.0, 1),
    };
    private (double from, double to, double seconds) _band = Band(PlayerStage.Starting);
    private readonly DispatcherTimer _progressTimer;
    private readonly System.Diagnostics.Stopwatch _sinceStart = new();
    private readonly List<(string what, double at)> _marks = [];

    private static readonly string[] Tips =
    [
        "Hold {view} + {menu} during a game to open the stream menu.",
        "Tap the screen while streaming for quick controls.",
        "A wired console (Ethernet) gives the steadiest picture.",
        "Competitive keeps latency lowest. Smooth rides out a weaker network.",
        "Change bitrate, clarity and volume from the stream menu without leaving the game.",
        "Your console wakes from Sleep automatically. It can take a few seconds.",
    ];

    public StreamViewModel(StreamLauncher launcher)
    {
        _launcher = launcher;
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
            Elapsed = (DateTime.UtcNow - _startedAt).ToString(DateTime.UtcNow - _startedAt >= TimeSpan.FromHours(1) ? @"h\:mm\:ss" : @"m\:ss"));
        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => StepProgress(0.04));
        // This DispatcherTimer constructor starts the timer: neither should tick until a stream starts.
        _progressTimer.Stop();
        _clock.Stop();
        launcher.StageChanged += OnStage;
        launcher.Stats += OnStats;
        launcher.Info += i => Info = i;
        launcher.Summary += (s, path) => { Summary = s; SummaryPath = path; ShowTips(); };
        launcher.Log += AddLog;
        launcher.Exited += OnExited;
        launcher.OpenSettingsRequested += () => IsAway = true;
        launcher.ReturnedToGame += () => IsAway = false;
        launcher.Hung += reason => Error = reason;
        launcher.TogetherChanged += OnTogether;
    }

    // ---- Play together ------------------------------------------------------------------------------------
    /// <summary>Watching a friend's stream (Play together) rather than streaming a console.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TargetLabel), nameof(FirstPhaseLabel))] public partial bool IsGuest { get; set; }
    /// <summary>Guest: the code to send the host (their router needs both sides to connect at once). Empty when not needed.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasReplyCode))] public partial string ReplyCode { get; set; } = "";
    public bool HasReplyCode => ReplyCode.Length > 0;
    /// <summary>Everyone playing together on this stream, the host included (0: not sharing). Discord's party size.</summary>
    [ObservableProperty] public partial int TogetherPlayers { get; set; }
    /// <summary>The latest Play together status (who's in, the invite): the Play together page shows it.</summary>
    [ObservableProperty] public partial TogetherStatus? Together { get; set; }
    /// <summary>Raised whenever the number of players together changes (for Discord's party size).</summary>
    public event Action<int>? TogetherPlayersChanged;
    partial void OnTogetherPlayersChanged(int value) => TogetherPlayersChanged?.Invoke(value);
    private (string invite, string name)? _lastJoin;

    // ---- Play together host controls (the Play together page; the stream menu's panel does the same in the stream) ----
    /// <summary>This stream can be shared now: it's a console or cloud stream of ours and its share is ready (it said so).</summary>
    public bool CanShare => IsRunning && !IsGuest && Together is { Role: "host" };
    /// <summary>Sharing this stream (anyone may or may not have joined yet).</summary>
    public bool IsSharing => CanShare && Together is { Players: > 0 };
    /// <summary>Running, ours, but the share isn't ready yet (still connecting).</summary>
    public bool ShareNotReady => IsRunning && !IsGuest && Together is null;
    /// <summary>Share pressed, waiting for the stream to say it started.</summary>
    [ObservableProperty] public partial bool SharePending { get; set; }
    /// <summary>Guest: "You're in Alex's stream as Player 2" (or still joining).</summary>
    public string GuestText => Together is { Role: "guest" } g
        ? g.Slot >= 0 ? $"You're in {g.HostName ?? "your friend"}'s stream as Player {g.Slot + 1}" : $"Joining {g.HostName ?? "your friend"}'s stream…"
        : IsGuest && IsRunning ? "Joining your friend's stream…" : "";
    public event Action? TogetherPageRequested;
    private bool _demoTogether;

    partial void OnTogetherChanged(TogetherStatus? value) => NotifyShare();
    partial void OnIsGuestChanged(bool value) => NotifyShare();
    private void NotifyShare()
    {
        OnPropertyChanged(nameof(CanShare)); OnPropertyChanged(nameof(IsSharing)); OnPropertyChanged(nameof(ShareNotReady)); OnPropertyChanged(nameof(GuestText));
    }

    [RelayCommand]
    private void ShareTogether()
    {
        if (!CanShare || IsSharing) return;
        if (_demoTogether) { DemoStatus(sharing: true, guests: []); return; }
        SharePending = _launcher.ShareTogether();
    }

    [RelayCommand]
    private void StopSharing()
    {
        if (!IsSharing) return;
        if (_demoTogether) { DemoStatus(sharing: false, guests: []); return; }
        _launcher.StopSharing();
    }

    [RelayCommand]
    private void RemoveGuest(int slot)
    {
        if (!IsSharing || slot is < 1 or > 3) return;
        if (_demoTogether) { DemoStatus(sharing: true, guests: Together!.People?.Where(p => p.Slot != slot).ToArray() ?? []); return; }
        _launcher.RemoveGuest(slot);
    }

    /// <summary>A guest's reply code (already checked by the page). The stream's answer comes back in the status note.</summary>
    public void AcceptReply(string code)
    {
        if (!IsSharing) return;
        if (_demoTogether) { Together = Together! with { Note = "Reply code accepted: connecting to your friend…", NoteIsProblem = false }; return; }
        _launcher.AcceptReply(code);
    }

    [RelayCommand] private void OpenTogether() => TogetherPageRequested?.Invoke();

    /// <summary>
    /// XSTREAM_DEMO_TOGETHER=ready|host|guest (testing): the Play together page with a pretend stream, not shared yet, shared
    /// with Sam as Player 2, or watching Alex's. The page's buttons act on the pretend status; nothing is sent anywhere.
    /// </summary>
    public void DemoTogether(string mode)
    {
        _demoTogether = true;
        IsCloud = false;
        IsGuest = mode == "guest";
        ConsoleName = IsGuest ? "Alex's stream" : "Living room Xbox";
        ProfileText = IsGuest ? "Play together" : "Balanced";
        Elapsed = "12:48";
        IsRunning = true;
        IsStreaming = true;
        if (IsGuest) Together = new TogetherStatus { Role = "guest", HostName = "Alex", Slot = 1, Players = 2 };
        else DemoStatus(sharing: mode == "host", guests: mode == "host" ? [new TogetherPlayer { Slot = 1, Name = "Sam", PingMs = 23, LossPct = 0.2 }] : []);
    }

    private void DemoStatus(bool sharing, TogetherPlayer[] guests)
    {
        Together = new TogetherStatus
        {
            Role = "host", Players = sharing ? guests.Length + 1 : 0, Guests = guests.Select(g => g.Name).ToArray(), People = guests,
            Invite = sharing ? "VX1-06FW5V588ZF27SQFC2S7311GYW9HMWDV4PT0PXBQEJZ4ZERKH6PF4G1V3XT04060N123VJ270HZG0001S13R6E0" : null,
            CapMbps = sharing && guests.Length > 0 ? 15 : null,
        };
        TogetherPlayers = Together.Players;
    }

    private void OnTogether(TogetherStatus t)
    {
        SharePending = false;
        Together = t;
        TogetherPlayers = t.Players;
        if (t.Role == "guest")
        {
            ReplyCode = t.ReplyCode ?? "";
            if (t.HostName is { Length: > 0 } h) ConsoleName = $"{h}'s stream";
        }
    }

    /// <summary>Join a friend's stream with their invite (no sign-in needed).</summary>
    public void StartGuest(string invite, string name)
    {
        if (IsRunning) return;
        _lastJoin = (invite, name);
        IsCloud = false;
        IsGuest = true;
        ReplyCode = "";
        Begin("", "your friend's stream", ProfileProvider(), launch: () => _launcher.StartGuest(invite, name));
        ProfileText = "Play together";
        Tip = GuestTips[Random.Shared.Next(GuestTips.Length)];
    }

    [RelayCommand]
    private async Task CopyReplyCode()
    {
        if (ReplyCode.Length == 0) return;
        await Platform.CopyAsync(ReplyCode);
        AddLog("Reply code copied");
    }

    private static readonly string[] GuestTips =
    [
        "Your controller becomes the next player on your friend's console.",
        "Hold {view} + {menu} for your own menu: volume, stats, full screen, Leave.",
        "Your friend's upload carries the picture: it's capped at about 15 Mbps while you play together.",
    ];

    public void Start(ConsoleItemViewModel console, StreamProfile profile)
    {
        IsCloud = false;
        IsGuest = false;
        Begin(console.ServerId, console.Name, profile, console.WirelessWarning);
    }

    // ---- Connection advice: this device's link while connecting, tips from the stream's numbers afterwards ----
    private readonly List<XStream.Core.Diagnostics.CheckSample> _samples = [];
    private Task<XStream.Core.Diagnostics.LocalLink>? _link;
    private bool _consoleOnWifi;
    /// <summary>Shown on the connecting screen: things about this device's or the console's connection worth fixing.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> LinkWarnings { get; } = [];
    public bool HasLinkWarnings => LinkWarnings.Count > 0;
    /// <summary>After the stream: what got in the way, worst first (empty when it went fine).</summary>
    public System.Collections.ObjectModel.ObservableCollection<FindingItem> SummaryTips { get; } = [];
    public bool HasSummaryTips => SummaryTips.Count > 0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanApplyTips))] public partial string TipsApplyText { get; set; } = "";
    public bool CanApplyTips => TipsApplyText.Length > 0;
    private XStream.Core.Diagnostics.CheckResult? _tips;
    /// <summary>Applies the suggested settings (the shell saves them).</summary>
    public Action<Func<AppSettings, AppSettings>>? SaveSettings { get; set; }
    public Func<AppSettings>? CurrentSettings { get; set; }

    private async void CheckLinkAsync(bool consoleOnWifi)
    {
        LinkWarnings.Clear();
        OnPropertyChanged(nameof(HasLinkWarnings));
        _consoleOnWifi = consoleOnWifi;
        var task = _link = Task.Run(XStream.Core.Diagnostics.LocalLink.Detect);
        var l = await task;
        if (_link != task) return;
        if (l.IsWifi && l.BandGHz is > 0 and < 3) LinkWarnings.Add("This device is on 2.4 GHz Wi-Fi: join your router's 5 GHz network for a smoother stream");
        if (l.IsWifi && l.SignalQuality is >= 0 and < 50) LinkWarnings.Add($"Weak Wi-Fi signal ({l.SignalQuality}%): move closer to the router if the picture stutters");
        if (consoleOnWifi) LinkWarnings.Add("The console is on Wi-Fi: an Ethernet cable to the router helps most");
        OnPropertyChanged(nameof(HasLinkWarnings));
    }

    private void ShowTips()
    {
        SummaryTips.Clear();
        TipsApplyText = "";
        _tips = null;
        if (_samples.Count >= 15 && _link is { IsCompletedSuccessfully: true } link)
        {
            var r = XStream.Core.Diagnostics.ConnectionCheck.Analyze(_samples, link.Result, _consoleOnWifi, Info?.Route ?? "");
            if (r.Level != XStream.Core.Diagnostics.CheckLevel.Good)
            {
                _tips = r;
                foreach (var f in r.Findings.Where(f => f.Level != XStream.Core.Diagnostics.CheckLevel.Good).Take(4))
                    SummaryTips.Add(new FindingItem(f.Title, f.Detail, f.Level == XStream.Core.Diagnostics.CheckLevel.Poor ? "poor" : "fair"));
                if (CurrentSettings?.Invoke() is { } s && ConnectionAdvisor.Suggestion(r, s) is { Length: > 0 } sug)
                    TipsApplyText = "Use suggested settings: " + sug;
            }
        }
        OnPropertyChanged(nameof(HasSummaryTips));
    }

    [RelayCommand]
    private void ApplyTips()
    {
        if (_tips is not { } r) return;
        SaveSettings?.Invoke(s => ConnectionAdvisor.Apply(r, s));
        TipsApplyText = "";
        AddLog("Applied the suggested settings");
    }

    /// <summary>Start an Xbox Cloud Gaming session for a game. <paramref name="heroArt"/>: its wide art for the connecting screen.</summary>
    public void StartCloud(XStream.Core.Xbox.CloudGame game, string? heroArt)
    {
        if (IsRunning) return;
        IsCloud = true;
        IsGuest = false;
        if (heroArt is not null) Art = [new GameArt(game.Name, heroArt, game.TitleId, game.HeroUrl ?? ""), .. Art.Where(a => a.Path != heroArt)];
        Begin(PlayerHost.CloudPrefix + game.TitleId, game.Name, ProfileProvider());
    }

    /// <summary>Set by the shell: the profile setting (cloud starts don't go through the Consoles page).</summary>
    public Func<StreamProfile> ProfileProvider { get; set; } = () => StreamProfile.Balanced;

    /// <summary>Streaming a cloud game (vs one of your consoles): changes the connecting screen's wording.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TargetLabel))] public partial bool IsCloud { get; set; }
    public string TargetLabel => IsGuest ? "Joining" : IsCloud ? "Starting" : "Connecting to";
    /// <summary>First label under the connecting screen's progress bar.</summary>
    public string FirstPhaseLabel => IsGuest ? "Find your friend" : IsCloud ? "Get a server" : "Wake console";
    partial void OnIsCloudChanged(bool value) => OnPropertyChanged(nameof(FirstPhaseLabel));

    private void Begin(string serverId, string name, StreamProfile profile, bool consoleOnWifi = false, Action? launch = null)
    {
        if (IsRunning) return;
        Reset();
        _samples.Clear();
        SummaryTips.Clear(); TipsApplyText = ""; OnPropertyChanged(nameof(HasSummaryTips));
        CheckLinkAsync(consoleOnWifi);
        _everStreamed = _cancelled = false;
        StartFailed = false;
        ConsoleName = name;
        ServerId = serverId;
        ProfileText = profile.ToString();
        Tip = Tips[Random.Shared.Next(Tips.Length)];
        _startedAt = DateTime.UtcNow;
        IsRunning = true;
        SetStep(0, StepState.Active);
        StageText = "Starting";
        LoaderText = "Getting ready";
        _band = Band(PlayerStage.Starting);
        _sinceStart.Restart();
        _marks.Clear();
        _progressTimer.Start();
        try { if (launch is not null) launch(); else _launcher.Start(serverId, name); }
        catch (Exception ex)
        {
            IsRunning = false;
            HasResult = true;
            Error = "Couldn't start the stream: " + ex.Message;
            SetStep(0, StepState.Failed);
            StartFailed = true;
            AddLog(Error);
            return;
        }
        RunningChanged?.Invoke(true);
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsRunning) return;
        _cancelled = true;
        IsStopping = true;
        StageText = "Ending the session";
        _launcher.Stop();
    }

    /// <summary>Back to the game from the launcher's Settings page.</summary>
    [RelayCommand]
    private void ReturnToGame()
    {
        if (!IsAway) return;
        _launcher.ReturnToGame();
        IsAway = false;
    }

    /// <summary>Retry the console that just failed (the Consoles page starts it).</summary>
    public event Action<string>? StreamAgainRequested;

    [RelayCommand]
    private void StreamAgain()
    {
        if (IsRunning) return;
        if (IsGuest && _lastJoin is { } j) { StartFailed = false; StartGuest(j.invite, j.name); return; }
        if (ServerId.Length == 0) return;
        StartFailed = false;
        StreamAgainRequested?.Invoke(ServerId);
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (IsRunning) return;
        HasResult = false;
        StartFailed = false;
        Reset();
    }

    /// <summary>Zip logs, crash reports and device info for troubleshooting; shows the file. Returns its path.</summary>
    [RelayCommand]
    private void ExportDiagnosticsNow() => ExportDiagnostics();

    public string? ExportDiagnostics()
    {
        try
        {
            var path = Diagnostics.Export(LogLines.ToList(), Error);
            Platform.Reveal(path);
            return path;
        }
        catch (Exception ex) { AddLog("Couldn't export diagnostics: " + ex.Message); return null; }
    }

    [RelayCommand]
    private void OpenLog()
    {
        var path = Info?.LogPath is { Length: > 0 } l ? l : SummaryPath;
        if (path.Length > 0) Platform.Open(Path.GetDirectoryName(path)!);
    }

    /// <summary>Ease toward the end of the current stage's band: quick at first, slowing as it nears the end.</summary>
    private void StepProgress(double dt)
    {
        if (!ShowLoader) { _progressTimer.Stop(); return; }
        var (from, to, secs) = _band;
        double p = Math.Max(Progress, from);
        p += (to - p) * Math.Min(1, dt / Math.Max(0.2, secs * 0.45));
        Progress = Math.Max(Progress, Math.Min(to, p)); // never backwards
    }

    private void OnStage(PlayerStage stage, string message)
    {
        if (stage != _stage || _marks.Count == 0) _marks.Add((stage.ToString(), _sinceStart.Elapsed.TotalSeconds));
        _stage = stage;
        if (stage is PlayerStage.Starting or PlayerStage.RequestingSession or PlayerStage.WakingConsole or PlayerStage.InQueue
            or PlayerStage.Connecting or PlayerStage.StartingVideo)
        {
            _band = Band(stage);
            Phase = stage switch { PlayerStage.Connecting => 1, PlayerStage.StartingVideo => 2, _ => 0 };
            LoaderText = stage switch
            {
                PlayerStage.Connecting or PlayerStage.StartingVideo or PlayerStage.Starting when IsGuest => message,
                PlayerStage.WakingConsole => IsCloud ? "Starting the game in the cloud" : "Waking your console",
                PlayerStage.InQueue => message.Replace("In the queue for a cloud server: ", "In the queue: "),
                PlayerStage.RequestingSession when IsCloud => "Finding a cloud server",
                PlayerStage.Connecting => "Connecting video and controller",
                PlayerStage.StartingVideo => "Starting the picture",
                _ => "Getting ready",
            };
        }
        else if (stage == PlayerStage.Streaming && Progress < 1)
        {
            Phase = 2;
            Progress = 1;
            LogTimings();
        }
        StageText = message;
        IsReconnecting = stage == PlayerStage.Reconnecting;
        int step = stage switch
        {
            PlayerStage.Starting => 0,
            PlayerStage.RequestingSession => 1,
            PlayerStage.WakingConsole or PlayerStage.InQueue => 2,
            PlayerStage.Connecting or PlayerStage.StartingVideo => 3,
            PlayerStage.Streaming or PlayerStage.Reconnecting => 4,
            _ => -1,
        };
        if (step >= 0)
        {
            StepNumber = Math.Min(step + 1, Steps.Count);
            for (int i = 0; i < Steps.Count; i++) Steps[i].State = i < step ? StepState.Done : i == step ? StepState.Active : StepState.Pending;
            if (step == 4) { Steps[4].State = StepState.Done; _everStreamed = true; IsStreaming = true; _clock.Start(); _startedAt = DateTime.UtcNow; }
        }
        if (stage == PlayerStage.Failed)
        {
            Error = message;
            var active = Steps.FirstOrDefault(s => s.State == StepState.Active);
            if (active is not null) active.State = StepState.Failed;
            if (!_everStreamed && !_cancelled) StartFailed = true;
        }
        AddLog(message);
    }

    private void OnStats(LiveStats s)
    {
        Live = s;
        Verdict = s.Verdict;
        if (s.Link is "Connected" && IsStreaming) _samples.Add(new(s.Mbps, s.CapMbps, s.LossPct, s.PingMs, s.JitterMs));
        VerdictKind = s.Link is "Connected"
            ? (s.LossPct > 2 || s.PingMs > 80 || s.JitterMs > 15 ? "warn" : "good")
            : s.Link is "Failed" ? "bad" : "warn";
        BitrateHistory = Push(BitrateHistory, s.Mbps);
        PingHistory = Push(PingHistory, s.PingMs);
        LatencyHistory = Push(LatencyHistory, s.NetToScreenP50Ms);
    }

    /// <summary>How long each startup step took, so the slow one can be fixed (shown in the log and diagnostics).</summary>
    private void LogTimings()
    {
        if (_marks.Count == 0) return;
        var parts = new List<string>();
        for (int i = 0; i < _marks.Count; i++)
        {
            double end = i + 1 < _marks.Count ? _marks[i + 1].at : _sinceStart.Elapsed.TotalSeconds;
            if (_marks[i].what == nameof(PlayerStage.Streaming)) break;
            parts.Add($"{_marks[i].what} {end - _marks[i].at:F1}s");
        }
        double first = _marks[0].at;
        AddLog($"Startup took {_sinceStart.Elapsed.TotalSeconds:F1} s: process {first:F1}s · {string.Join(" · ", parts)}");
    }

    private void OnExited(bool failed)
    {
        _clock.Stop();
        _progressTimer.Stop();
        IsAway = false;
        IsRunning = false;
        IsStreaming = false;
        IsStopping = false;
        IsReconnecting = false;
        ReplyCode = "";
        TogetherPlayers = 0;
        Together = null;
        SharePending = false;
        if (failed && Error.Length == 0 && _stage != PlayerStage.Ended)
            Error = "The stream stopped unexpectedly. The log below may say why.";
        if (!_everStreamed && !_cancelled)
        {
            // Closed before the picture came up, without saying why: never just make the screen disappear.
            if (Error.Length == 0) Error = "The stream closed before the picture came up. Try again, or save diagnostics so we can see why.";
            StartFailed = true;
        }
        HasResult = true;
        RunningChanged?.Invoke(false);
    }

    private void Reset()
    {
        foreach (var s in Steps) s.State = StepState.Pending;
        LogLines.Clear();
        Error = "";
        Live = null;
        Info = null;
        Summary = null;
        SummaryPath = "";
        Verdict = "";
        BitrateHistory = PingHistory = LatencyHistory = [];
        Elapsed = "0:00";
        StepNumber = 1;
        Progress = 0;
        Phase = 0;
        _stage = PlayerStage.Starting;
    }

    private void SetStep(int i, StepState state) => Steps[i].State = state;

    /// <summary>
    /// XSTREAM_DEMO_STREAM=live|ended|failed (testing): show the Consoles page's "Now playing" header with sample
    /// numbers, to check the layout without streaming.
    /// </summary>
    public void Demo(string mode, string serverId, string name)
    {
        Reset();
        ServerId = serverId; ConsoleName = name; ProfileText = "Balanced"; Elapsed = "12:48";
        Info = new SessionInfo { Decoder = "d3d11va (hardware)", Renderer = "Vexel Direct3D 11", Controller = "XInput", Audio = "Speakers, Opus",
            LowLatencyInput = true, Remb = true, Nack = true, Route = "Home network (host -> 192.168.68.58:9002)" };
        var rnd = new Random(4);
        for (int i = 0; i < 60; i++)
        {
            BitrateHistory = Push(BitrateHistory, 38 + rnd.NextDouble() * 8);
            PingHistory = Push(PingHistory, 4 + rnd.NextDouble() * 3);
            LatencyHistory = Push(LatencyHistory, 9 + rnd.NextDouble() * 4);
        }
        AddLog("Video stream: 2560x1440 H.264 High level 5.1, BT.709, limited range");
        if (mode == "live")
        {
            IsRunning = true;
            IsStreaming = true;
            Live = new LiveStats { Mbps = 41.3, PingMs = 5, Fps = 60, NetToScreenP50Ms = 10.4, LossPct = 0.1, BufferMs = 4, PadHz = 1000, VideoHeight = 1440, Link = "Connected" };
            Verdict = "Stable"; VerdictKind = "good";
        }
        else
        {
            HasResult = true;
            Summary = new SessionSummary { Minutes = 23.4, Freezes = mode == "failed" ? 3 : 0, AvgMbps = 39.6, MedianRttMs = 4.4, AvgLossPct = 0.11 };
            if (mode == "failed") Error = "Lost the connection to Xbox and couldn't get it back after 4 tries. Check the network, then stream again.";
        }
    }

    private void AddLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {Controls.HintText.Plain(line)}");
        while (LogLines.Count > 200) LogLines.RemoveAt(0);
    }

    private static double[] Push(double[] a, double v)
    {
        var next = a.Length < HistoryLength ? new double[a.Length + 1] : new double[HistoryLength];
        Array.Copy(a, a.Length < HistoryLength ? 0 : 1, next, 0, next.Length - 1);
        next[^1] = double.IsFinite(v) ? v : 0;
        return next;
    }
}
