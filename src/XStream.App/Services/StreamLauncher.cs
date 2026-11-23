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
            _standby = Spawn(PlayerHost.StandbyArg, null);
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
        if (Running) throw new InvalidOperationException("A stream is already running.");
        _stopRequested = false;
        if (StandbyReady && Send(_standby, $"play {serverId}\t{consoleName.Replace('\t', ' ')}"))
        {
            _proc = _standby;
            _standby = null;
            _standbyReady = false;
            AllowToFront(_proc);
            ArmWatchdog();
            Log?.Invoke("Using the warmed-up stream process");
            return;
        }
        DropStandby();
        _proc = Spawn(serverId, consoleName);
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

    private Process Spawn(string serverId, string? consoleName)
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
        psi.ArgumentList.Add(serverId);
        if (consoleName is not null) psi.ArgumentList.Add(consoleName);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Only the session process talks to the UI; a standby child only says when it's ready.
        p.OutputDataReceived += (_, e) => OnLine(p, e.Data);
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && p == _proc) Post(() => Log?.Invoke(e.Data)); };
        p.Exited += (_, _) =>
        {
            int code = -1;
            try { p.WaitForExit(); code = p.ExitCode; } catch { }
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
        if (e?.Type == "ready") { Post(() => { if (from == _standby) _standbyReady = true; }); return; }
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
            }
        });
    }

    private static void Post(Action a) => Dispatcher.UIThread.Post(a);
}
