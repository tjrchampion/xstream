using System.Diagnostics;
using XStream.Core.Config;
using XStream.Core.Native;
using XStream.Core.Video;
using XStream.Core.Xbox;

namespace XStream.Player;

/// <summary>
/// Runs one session as a child process of the launcher: settings come from settings.json, progress goes
/// to stdout as <see cref="PlayerEvent"/> lines, and "stop" on stdin (or stdin closing, i.e. the launcher
/// went away) ends the session. Keeping the stream in its own process gives the video window the main
/// thread it needs on macOS and means a crash in native video code can't take the launcher down.
///
/// Standby: started with <see cref="StandbyArg"/> instead of a console id, the child loads the native
/// libraries, refreshes the sign-in and opens a connection to the streaming service, then waits for
/// "play &lt;serverId&gt;\t&lt;name&gt;" on stdin. Pressing Stream then skips all of that.
/// </summary>
public static class PlayerHost
{
    public const string StandbyArg = "--standby";

    public static int RunChild(string serverId, string? consoleName = null)
    {
        var output = Console.Out;
        var gate = new object();
        void Emit(PlayerEvent e) { lock (gate) { output.WriteLine(e.ToLine()); output.Flush(); } }

        var http = new HttpClient(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromMinutes(15) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        var auth = new XboxAuth(http);
        var store = new TokenStore(auth);

        var startup = Stopwatch.StartNew();
        if (serverId == StandbyArg)
        {
            Warm(store, http);
            Emit(new PlayerEvent { Type = "ready" });
            // Wait for the launcher. Anything other than a play command (or stdin closing) means "not needed".
            var line = Console.In.ReadLine();
            if (line is null || !line.StartsWith("play ", StringComparison.Ordinal)) return 0;
            var parts = line[5..].Split('\t');
            serverId = parts[0].Trim();
            consoleName = parts.Length > 1 ? parts[1] : null;
            startup.Restart();
        }

        var settings = AppSettings.Load();
        var session = new PlayerSession();
        session.StageChanged += (s, m) => Emit(new PlayerEvent { Type = "stage", Stage = s, Message = m });
        session.Log += m => Emit(new PlayerEvent { Type = "log", Message = m });
        session.Stats += s => Emit(new PlayerEvent { Type = "stats", Stats = s });
        session.Info += i => Emit(new PlayerEvent { Type = "info", Info = i });
        session.Ended += (sum, path) => Emit(new PlayerEvent { Type = "summary", Summary = sum, Message = path });
        session.MoreSettingsRequested += () => Emit(new PlayerEvent { Type = "open-settings" });
        session.ReturnedToGame += () => Emit(new PlayerEvent { Type = "returned" });

        // Heartbeat for the launcher's watchdog: every 2 s, with how long the main loop has been stuck (0 normally).
        new Thread(() =>
        {
            long last = -1; double stuck = 0;
            while (true)
            {
                Thread.Sleep(2000);
                long t = session.MainLoopTicks;
                stuck = session.InMainLoop && t == last ? stuck + 2 : 0;
                last = t;
                Emit(new PlayerEvent { Type = "heartbeat", Message = stuck.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) });
            }
        }) { IsBackground = true, Name = "xstream-heartbeat" }.Start();

        new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = Console.In.ReadLine()) is not null)
                {
                    var cmd = line.Trim();
                    if (cmd == "stop") break;
                    if (cmd == "resume") session.ReturnToGame(); // launcher's Settings closed
                }
            }
            catch { /* stdin gone */ }
            session.Stop();
        }) { IsBackground = true, Name = "xstream-stdin" }.Start();

        XboxStreamingClient client;
        XboxTokens tokens;
        try
        {
            Emit(new PlayerEvent { Type = "stage", Stage = PlayerStage.Starting, Message = "Checking sign-in" });
            (client, tokens) = store.CreateClientAsync(http, settings).GetAwaiter().GetResult();
            Emit(new PlayerEvent { Type = "log", Message = $"Sign-in ready in {startup.Elapsed.TotalSeconds:F2} s" });
        }
        catch (Exception ex)
        {
            Emit(new PlayerEvent { Type = "stage", Stage = PlayerStage.Failed, Message = PlayerSession.Friendly(ex) });
            return 1;
        }
        // Reconnects (after a dropped connection) sign in afresh: the first token may have expired by then.
        async Task<XboxStreamingClient> Refresh() => (await store.CreateClientAsync(http, AppSettings.Load())).client;
        return session.Run(PlayerOptions.From(settings, serverId) with { ConsoleName = consoleName ?? "Xbox" }, client,
            async () => await auth.GetTransferTokenAsync(store.Load() ?? tokens), Refresh);
    }

    /// <summary>Do the slow first-time work now: native libraries, sign-in refresh, a warm TLS connection.</summary>
    private static void Warm(TokenStore store, HttpClient http)
    {
        try
        {
            NativeLibs.Ensure();
            _ = NativeLibs.CanLoad("SDL2");
            _ = H264Decoder.Version; // loads avcodec + avutil
        }
        catch { /* the session reports missing libraries properly */ }
        try
        {
            if (!store.SignedIn) return;
            var (client, _) = store.CreateClientAsync(http, AppSettings.Load()).GetAwaiter().GetResult();
            _ = client.GetConsolesAsync().GetAwaiter().GetResult(); // opens and keeps the connection to the service
        }
        catch { /* not fatal: the session signs in again and reports problems */ }
    }
}
