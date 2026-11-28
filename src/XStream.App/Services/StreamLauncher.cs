using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using XStream.Core.Stability;
using XStream.Player;

namespace XStream.App.Services;

/// <summary>
/// Starts a stream in a child copy of this app and turns its stdout events into UI-thread callbacks.
/// All events are raised on the Avalonia UI thread.
///
/// To make Stream start quickly, a standby child is kept ready (<see cref="Prepare"/>): it has already
/// loaded the video libraries, refreshed the sign-in and connected to the streaming service, and just
/// waits to be told which console to play.
/// </summary>
public sealed class StreamLauncher
{
    private Process? _proc;      // the running session
    private Process? _standby;   // ready and waiting
    private bool _standbyReady;
    private bool _stopRequested;

    // The launcher's own record of starting streams (which process, what it said, how it exited): a start that
    // fails before the session's log opens leaves nothing else behind. logs/launcher.log, trimmed at 2 MB.
    private static readonly object LogGate = new();
    private static readonly string LogPath = Path.Combine(XStream.Core.Config.AppPaths.Logs, "launcher.log");
    private static void Trace(string m)
    {
        lock (LogGate)
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000) File.Delete(LogPath);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {m}\n");
            }
            catch { }
        }
    }
    private static string Id(Process? p) { try { return p is null ? "none" : $"pid {p.Id}"; } catch { return "?"; } }

    public event Action<PlayerStage, string>? StageChanged;
    public event Action<LiveStats>? Stats;
    public event Action<SessionInfo>? Info;
    public event Action<SessionSummary, string>? Summary;
    public event Action<string>? Log;
    /// <summary>The child exited. Argument: true if it failed (crash or non-zero exit) rather than ending normally.</summary>
    public event Action<bool>? Exited;
    /// <summary>"More settings…" chosen in the stream menu.</summary>
    public event Action? OpenSettingsRequested;
    /// <summary>The player went back to the game from the video window (tapped it).</summary>
    public event Action? ReturnedToGame;
    /// <summary>Where the stream's window was when it closed (windowed only): the launcher reopens there.</summary>
    public event Action<WindowPlace>? WindowClosedAt;
    /// <summary>Where the stream's window should open: the launcher window's client area (set by the app).</summary>
    public Func<WindowPlace?>? Placement { get; set; }
    /// <summary>The watchdog closed a stream that stopped responding (the reason is in the argument).</summary>
    public event Action<string>? Hung;

    // ---- Watchdog (see StreamWatchdog): close a stream that stopped responding -----------------------------
    private readonly StreamWatchdog _dog = new();
    private DispatcherTimer? _watchdog;

    private void ArmWatchdog()
    {
        _dog.Arm(DateTime.UtcNow);
        _watchdog ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => CheckWatchdog());
        _watchdog.Start();
    }

    private void CheckWatchdog()
    {
        if (!Running) { _watchdog?.Stop(); return; }
        if (_dog.Check(DateTime.UtcNow) is { } reason) KillHung(reason);
    }

    private void OnHeartbeat(string? stuckSeconds)
    {
        double.TryParse(stuckSeconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s);
        if (_dog.Heartbeat(DateTime.UtcNow, s) is { } reason) KillHung(reason);
    }

    private void KillHung(string reason)
    {
        var p = _proc;
        if (p is null || p.HasExited) return;
        _watchdog?.Stop();
        Log?.Invoke("Watchdog: " + reason);
        Hung?.Invoke(reason);
        try { p.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>Tell the stream the launcher's Settings were closed: the controller goes back to the game.</summary>
    public void ReturnToGame() => Send(_proc, "resume");

    public bool Running => _proc is { HasExited: false };

    /// <summary>Whether the next Stream will use a warmed-up child.</summary>
    public bool StandbyReady => _standbyReady && _standby is { HasExited: false };

    /// <summary>Start a standby child if there isn't one (call when signed in and idle).</summary>
    public void Prepare()
    {
        if (_standby is { HasExited: false } || Running) return;
        try
        {
            _standbyReady = false;
            _standby = Spawn(PlayerHost.StandbyArg);
            Trace($"Standby spawned {Id(_standby)}");
        }
        catch { _standby = null; }
    }

    /// <summary>Close the standby child (signed out, or exiting).</summary>
    public void DropStandby()
    {
        var s = _standby;
        _standby = null;
        _standbyReady = false;
        if (s is null) return;
        try { s.StandardInput.Close(); } catch { }
        _ = Task.Run(async () => { await Task.Delay(3000); try { if (!s.HasExited) s.Kill(); } catch { } });
    }

    public void Start(string serverId, string consoleName)
    {
        Trace($"Start {consoleName}: current {Id(_proc)} running={Running}, standby {Id(_standby)} ready={_standbyReady} exited={(_standby is { } sb ? sb.HasExited.ToString() : "-")}");
        if (Running) throw new InvalidOperationException("A stream is already running.");
        _stopRequested = false;
        string place = Placement?.Invoke()?.ToString() ?? "";
        if (StandbyReady && Send(_standby, $"play {serverId}\t{consoleName.Replace('\t', ' ')}\t{place}"))
        {
            _proc = _standby;
            _standby = null;
            _standbyReady = false;
            AllowToFront(_proc);
            ArmWatchdog();
            Log?.Invoke("Using the warmed-up stream process");
            Trace($"  using standby {Id(_proc)}");
            return;
        }
        DropStandby();
        _proc = place.Length > 0 ? Spawn(serverId, consoleName, place) : Spawn(serverId, consoleName);
        Trace($"  spawned {Id(_proc)}");
        AllowToFront(_proc);
        ArmWatchdog();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>
    /// Windows only lets the foreground app bring a window to the front. The stream's window appears later
    /// (with its first picture), from a process that may have been waiting in standby: let it come forward.
    /// </summary>
    private static void AllowToFront(Process p)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { AllowSetForegroundWindow(p.Id); } catch { }
    }

    /// <summary>Ask the stream to end cleanly (it logs and tells the console); kill it if it doesn't within 10 s.</summary>
    public void Stop()
    {
        var p = _proc;
        if (p is null || p.HasExited) return;
        _stopRequested = true;
        Send(p, "stop");
        _ = Task.Run(async () =>
        {
            await Task.Delay(10_000);
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        });
    }

    /// <summary>
    /// Play together: watch a friend's stream in a child process (no sign-in needed). The warmed-up standby child is
    /// kept for the next console stream.
    /// </summary>
    public void StartGuest(string invite, string name)
    {
        if (Running) throw new InvalidOperationException("A stream is already running.");
        _stopRequested = false;
        string place = Placement?.Invoke()?.ToString() ?? "";
        _proc = place.Length > 0 ? Spawn(PlayerHost.JoinArg, invite, name, place) : Spawn(PlayerHost.JoinArg, invite, name);
        AllowToFront(_proc);
        ArmWatchdog();
    }

    /// <summary>Play together: who's in (host or guest), the codes to show. For the Play together page and Discord's party size.</summary>
    public event Action<TogetherStatus>? TogetherChanged;

    // Play together from the launcher's page: the stream does these exactly as if its own panel had been used, and
    // answers with a together event either way. False if there's no stream to tell.
    public bool ShareTogether() => Send(_proc, new TogetherCommand(TogetherCommandKind.Share).ToLine());
    public bool StopSharing() => Send(_proc, new TogetherCommand(TogetherCommandKind.Stop).ToLine());
    public bool RemoveGuest(int slot) => Send(_proc, new TogetherCommand(TogetherCommandKind.Remove, slot).ToLine());
    public bool AcceptReply(string code) => Send(_proc, new TogetherCommand(TogetherCommandKind.Reply, Code: code).ToLine());

    private Process Spawn(params string[] childArgs)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        // Run as `XStream play-child <id>`; under `dotnet XStream.dll` the process is dotnet itself.
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the app's own executable.");
        psi.FileName = self;
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        psi.ArgumentList.Add(StreamLauncherArgs.ChildCommand);
        foreach (var a in childArgs) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Only the session process talks to the UI; a standby child only says when it's ready.
        p.OutputDataReceived += (_, e) => OnLine(p, e.Data);
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            Trace($"  [{Id(p)} stderr] {e.Data}");
            if (p == _proc) Post(() => Log?.Invoke(e.Data));
        };
        p.Exited += (_, _) =>
        {
            int code = -1;
            try { p.WaitForExit(); code = p.ExitCode; } catch { }
            Trace($"  {Id(p)} exited with {code} ({(p == _standby ? "standby" : p == _proc ? "session" : "old")}{(_stopRequested ? ", stop requested" : "")})");
            Post(() =>
            {
                if (p == _standby) { _standby = null; _standbyReady = false; return; }
                if (p != _proc) return;
                Exited?.Invoke(code != 0 && !_stopRequested);
            });
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static bool Send(Process? p, string line)
    {
        try
        {
            if (p is null || p.HasExited) return false;
            p.StandardInput.WriteLine(line);
            p.StandardInput.Flush();
            return true;
        }
        catch { return false; }
    }

    private void OnLine(Process from, string? line)
    {
        if (line is null) return;
        var e = PlayerEvent.TryParse(line);
        if (e?.Type == "ready") { Trace($"  {Id(from)} ready"); Post(() => { if (from == _standby) _standbyReady = true; }); return; }
        if (e?.Type is "stage" or "summary" || e is null) Trace($"  [{Id(from)}] {(e is null ? line : e.Type == "stage" ? $"{e.Stage}: {e.Message}" : "summary")}");
        Post(() =>
        {
            if (from != _proc) return;
            if (e is null) { if (!string.IsNullOrWhiteSpace(line)) Log?.Invoke(line); return; }
            switch (e.Type)
            {
                case "stage" when e.Stage is { } s: StageChanged?.Invoke(s, e.Message ?? ""); break;
                case "stats" when e.Stats is not null: Stats?.Invoke(e.Stats); break;
                case "info" when e.Info is not null: Info?.Invoke(e.Info); break;
                case "summary" when e.Summary is not null: Summary?.Invoke(e.Summary, e.Message ?? ""); break;
                case "log" when e.Message is not null: Log?.Invoke(e.Message); break;
                case "open-settings": OpenSettingsRequested?.Invoke(); break;
                case "returned": ReturnedToGame?.Invoke(); break;
                case "heartbeat": OnHeartbeat(e.Message); break;
                case "window" when WindowPlace.Parse(e.Message) is { } w: WindowClosedAt?.Invoke(w); break;
                case "together" when e.Together is not null: TogetherChanged?.Invoke(e.Together); break;
            }
        });
    }

    private static void Post(Action a) => Dispatcher.UIThread.Post(a);
}
