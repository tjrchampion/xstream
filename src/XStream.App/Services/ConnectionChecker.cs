using System.Diagnostics;
using System.Reflection;
using XStream.Core.Diagnostics;
using XStream.Player;

namespace XStream.App.Services;

/// <summary>
/// "Check connection": runs the stream process in check mode (XSTREAM_CHECK_SECONDS) against a console: a short
/// stream with no picture, sound or controller. Collects its per-second stats and the route, looks at this
/// device's own link, and turns it all into advice (<see cref="ConnectionCheck"/>).
/// </summary>
public sealed class ConnectionChecker
{
    public const int Seconds = 12;

    private Process? _proc;

    /// <summary>Progress line ("Waking the console…", "Measuring… 5 s").</summary>
    public event Action<string, double>? Progress;

    public bool Running => _proc is { HasExited: false };

    public void Cancel()
    {
        var p = _proc;
        if (p is null || p.HasExited) return;
        try { p.StandardInput.WriteLine("stop"); p.StandardInput.Flush(); } catch { }
        _ = Task.Run(async () => { await Task.Delay(8000); try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } });
    }

    public async Task<CheckResult> RunAsync(string serverId, string consoleName, bool consoleOnWifi, CancellationToken ct = default)
    {
        var linkTask = Task.Run(LocalLink.Detect, ct);
        var samples = new List<CheckSample>();
        string route = "", failure = "";
        var streamingAt = (DateTime?)null;

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the app's own executable.");
        psi.FileName = self;
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        psi.ArgumentList.Add(StreamLauncherArgs.ChildCommand);
        psi.ArgumentList.Add(serverId);
        psi.ArgumentList.Add(consoleName);
        psi.Environment["XSTREAM_CHECK_SECONDS"] = Seconds.ToString();

        var done = new TaskCompletionSource();
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) =>
        {
            if (PlayerEvent.TryParse(e.Data) is not { } ev) return;
            switch (ev.Type)
            {
                case "stage" when ev.Stage is { } s:
                    if (s == PlayerStage.Streaming) streamingAt ??= DateTime.UtcNow;
                    if (s == PlayerStage.Failed) failure = ev.Message ?? "The test stream failed";
                    else if (s is not (PlayerStage.Streaming or PlayerStage.Stopping or PlayerStage.Ended))
                        Report(s switch
                        {
                            PlayerStage.WakingConsole => "Waking the console…",
                            PlayerStage.InQueue => "Waiting for the console…",
                            _ => "Connecting to the console…",
                        }, 0.05);
                    break;
                case "stats" when ev.Stats is { } st && streamingAt is not null:
                    lock (samples) samples.Add(new CheckSample(st.Mbps, st.CapMbps, st.LossPct, st.PingMs, st.JitterMs));
                    double t = (DateTime.UtcNow - streamingAt.Value).TotalSeconds;
                    Report($"Measuring… {Math.Max(0, Seconds - (int)t)} s", 0.1 + 0.9 * Math.Clamp(t / Seconds, 0, 1));
                    break;
                case "info" when ev.Info is { } info:
                    route = info.Route;
                    break;
            }
        };
        p.Exited += (_, _) => done.TrySetResult();
        _proc = p;
        Report("Connecting to the console…", 0.02);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(Cancel))
            await done.Task.WaitAsync(TimeSpan.FromSeconds(Seconds + 120), CancellationToken.None).ContinueWith(_ => { });
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        _proc = null;
        ct.ThrowIfCancellationRequested();

        var link = await linkTask;
        List<CheckSample> got;
        lock (samples) got = samples.ToList();
        var result = ConnectionCheck.Analyze(got, link, consoleOnWifi, route);
        if (got.Count == 0 && failure.Length > 0)
            result = result with { Findings = [new CheckFinding(CheckLevel.Poor, "The test stream failed", failure)] };
        return result with { Link = link, Route = route };
    }

    private void Report(string text, double fraction) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Progress?.Invoke(text, fraction));
}
