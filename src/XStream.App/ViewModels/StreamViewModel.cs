using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;
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

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPanel), nameof(IsConnecting), nameof(ShowLoader))] public partial bool IsRunning { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPanel))] public partial bool HasResult { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsConnecting), nameof(ShowLoader))] public partial bool IsStreaming { get; set; }
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
    [ObservableProperty] public partial LiveStats? Live { get; set; }
    [ObservableProperty] public partial string Verdict { get; set; } = "";
    [ObservableProperty] public partial string VerdictKind { get; set; } = "good"; // good | warn | bad
    [ObservableProperty] public partial double[] BitrateHistory { get; set; } = [];
    [ObservableProperty] public partial double[] PingHistory { get; set; } = [];
    [ObservableProperty] public partial double[] LatencyHistory { get; set; } = [];
    [ObservableProperty] public partial SessionInfo? Info { get; set; }

    // After the session
    [ObservableProperty] public partial SessionSummary? Summary { get; set; }
    [ObservableProperty] public partial string SummaryPath { get; set; } = "";

    public bool HasPanel => IsRunning || HasResult;
    public bool IsConnecting => IsRunning && !IsStreaming;
    public bool HasError => Error.Length > 0;
    partial void OnErrorChanged(string value) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ShowLoader)); }

    // Full-screen connecting screen
    /// <summary>The full-screen connecting screen is up (until the picture arrives, or it fails).</summary>
    public bool ShowLoader => IsConnecting && !HasError;
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
        launcher.Summary += (s, path) => { Summary = s; SummaryPath = path; };
        launcher.Log += AddLog;
        launcher.Exited += OnExited;
        launcher.OpenSettingsRequested += () => IsAway = true;
        launcher.ReturnedToGame += () => IsAway = false;
        launcher.Hung += reason => Error = reason;
    }

    public void Start(ConsoleItemViewModel console, StreamProfile profile)
    {
        IsCloud = false;
        Begin(console.ServerId, console.Name, profile);
    }

    /// <summary>Start an Xbox Cloud Gaming session for a game. <paramref name="heroArt"/>: its wide art for the connecting screen.</summary>
    public void StartCloud(XStream.Core.Xbox.CloudGame game, string? heroArt)
    {
        if (IsRunning) return;
        IsCloud = true;
        if (heroArt is not null) Art = [new GameArt(game.Name, heroArt, game.TitleId, game.HeroUrl ?? ""), .. Art.Where(a => a.Path != heroArt)];
        Begin(PlayerHost.CloudPrefix + game.TitleId, game.Name, ProfileProvider());
    }

    /// <summary>Set by the shell: the profile setting (cloud starts don't go through the Consoles page).</summary>
    public Func<StreamProfile> ProfileProvider { get; set; } = () => StreamProfile.Balanced;

    /// <summary>Streaming a cloud game (vs one of your consoles): changes the connecting screen's wording.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TargetLabel))] public partial bool IsCloud { get; set; }
    public string TargetLabel => IsCloud ? "Starting" : "Connecting to";
    /// <summary>First label under the connecting screen's progress bar.</summary>
    public string FirstPhaseLabel => IsCloud ? "Get a server" : "Wake console";
    partial void OnIsCloudChanged(bool value) => OnPropertyChanged(nameof(FirstPhaseLabel));

    private void Begin(string serverId, string name, StreamProfile profile)
    {
        if (IsRunning) return;
        Reset();
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
        try { _launcher.Start(serverId, name); }
        catch (Exception ex)
        {
            IsRunning = false;
            HasResult = true;
            Error = "Couldn't start the stream: " + ex.Message;
            SetStep(0, StepState.Failed);
            return;
        }
        RunningChanged?.Invoke(true);
    }

    [RelayCommand]
    private void Stop()
    {
        if (!IsRunning) return;
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
        if (IsRunning || ServerId.Length == 0) return;
        StreamAgainRequested?.Invoke(ServerId);
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (IsRunning) return;
        HasResult = false;
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
            if (step == 4) { Steps[4].State = StepState.Done; IsStreaming = true; _clock.Start(); _startedAt = DateTime.UtcNow; }
        }
        if (stage == PlayerStage.Failed)
        {
            Error = message;
            var active = Steps.FirstOrDefault(s => s.State == StepState.Active);
            if (active is not null) active.State = StepState.Failed;
        }
        AddLog(message);
    }

    private void OnStats(LiveStats s)
    {
        Live = s;
        Verdict = s.Verdict;
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
        if (failed && Error.Length == 0 && _stage != PlayerStage.Ended)
            Error = "The stream stopped unexpectedly. The log below may say why.";
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
