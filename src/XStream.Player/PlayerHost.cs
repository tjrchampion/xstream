using System.Diagnostics;
using XStream.Core.Config;
using XStream.Core.Native;
using XStream.Core.Together;
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
    /// <summary>Server-id prefix that means "Xbox Cloud Gaming, this title" (e.g. cloud:FORTNITE).</summary>
    public const string CloudPrefix = "cloud:";

    public static int RunChild(string serverId, string? consoleName = null, string? place = null)
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
            place = parts.Length > 2 ? parts[2] : null;
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
        session.TogetherChanged += t => Emit(new PlayerEvent { Type = "together", Together = t });

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
                    else if (TogetherCommand.TryParse(cmd) is { } tc) session.Together(tc); // the launcher's Play together page
                }
            }
            catch { /* stdin gone */ }
            session.Stop();
        }) { IsBackground = true, Name = "xstream-stdin" }.Start();

        // "cloud:<titleId>" = Xbox Cloud Gaming for that game; anything else is a console's server id.
        string? cloudTitle = serverId.StartsWith(CloudPrefix, StringComparison.Ordinal) ? serverId[CloudPrefix.Length..] : null;
        XboxStreamingClient client;
        XboxTokens tokens;
        try
        {
            Emit(new PlayerEvent { Type = "stage", Stage = PlayerStage.Starting, Message = "Checking sign-in" });
            (client, tokens) = cloudTitle is null
                ? store.CreateClientAsync(http, settings).GetAwaiter().GetResult()
                : store.CreateCloudClientAsync(http, settings).GetAwaiter().GetResult();
            Emit(new PlayerEvent { Type = "log", Message = $"Sign-in ready in {startup.Elapsed.TotalSeconds:F2} s" });
        }
        catch (Exception ex)
        {
            Emit(new PlayerEvent { Type = "stage", Stage = PlayerStage.Failed, Message = PlayerSession.Friendly(ex) });
            return 1;
        }
        // Reconnects (after a dropped connection) sign in afresh: the first token may have expired by then.
        async Task<XboxStreamingClient> Refresh() => cloudTitle is null
            ? (await store.CreateClientAsync(http, AppSettings.Load())).client
            : (await store.CreateCloudClientAsync(http, AppSettings.Load())).client;
        // Play together: guests see the gamertag ("Joined Alex's stream"); looked up now so it's ready before anyone joins.
        string? gameName = null;
        session.HostName = () => auth.Gamertag ?? Environment.UserName;
        _ = Task.Run(async () => { try { await store.GetWebTokenAsync(); } catch { } });
        // Console remote control (typing, Turn off): same sign-in as the library, console streams only.
        if (cloudTitle is null)
        {
            // What's running (for per-game controller profiles): presence, with the sign-in token reused for 30 min.
            (string Auth, string Xuid, DateTime At)? web = null;
            session.GameName = () => gameName;
            session.NowPlaying = async ct =>
            {
                if (web is not { } w || DateTime.UtcNow - w.At > TimeSpan.FromMinutes(30))
                {
                    var (a, x) = await store.GetWebTokenAsync(ct);
                    web = w = (a, x, DateTime.UtcNow);
                }
                var now = await XboxLibrary.GetNowPlayingAsync(http, w.Auth, w.Xuid, ct);
                gameName = now?.Name;
                return now?.TitleId;
            };
        }
        // Achievement pop-ups: console and cloud (read-only Xbox Live, same sign-in as the library).
        session.Achievements = new AchievementWatcher(new XboxSocial(http, ct => store.GetWebTokenAsync(ct)), settings.Locale);
        if (cloudTitle is null)
            session.Remote =new ConsoleRemote(new XboxConsoleControl(http, async ct => (await store.GetWebTokenAsync(ct)).Authorization),
                serverId, consoleName ?? "Xbox");
        // Connection check (the launcher's "Check connection"): a short stream with no picture, sound or controller,
        // allowed to climb to a high bitrate, ended after XSTREAM_CHECK_SECONDS of streaming.
        int check = int.TryParse(Environment.GetEnvironmentVariable("XSTREAM_CHECK_SECONDS"), out var cs) ? Math.Clamp(cs, 5, 60) : 0;
        var options = PlayerOptions.From(settings, serverId) with
        {
            ConsoleName = consoleName ?? "Xbox", CloudTitleId = cloudTitle, Place = WindowPlace.Parse(place),
        };
        if (check > 0)
        {
            options = options with
            {
                Headless = true, Audio = false, CheckSeconds = check,
                Settings = (options.Settings ?? settings) with { MaxMbps = 60 }, // the engine follows the settings' ceiling
                Stability = options.Stability with { MaxKbps = 60_000, StartKbps = Math.Max(options.Stability.StartKbps, 20_000) },
            };
            session.StageChanged += (s, _) =>
            {
                if (s == PlayerStage.Streaming)
                    Task.Delay(TimeSpan.FromSeconds(check)).ContinueWith(_ => session.Stop());
            };
        }
        int code = session.Run(options, client,
            async () => await auth.GetTransferTokenAsync(store.Load() ?? tokens), Refresh);
        // Tell the launcher where the (windowed) video was, so it reappears in the same place: one window, not two.
        if (session.LastWindow is { } last) Emit(new PlayerEvent { Type = "window", Message = last.ToString() });
        return code;
    }

    /// <summary>Child argument for watching a friend's stream: --join &lt;invite&gt; &lt;name&gt; [place].</summary>
    public const string JoinArg = "--join";

    /// <summary>
    /// Play together as a guest, as a child of the launcher: same events on stdout and "stop" on stdin as a stream, but
    /// no Xbox sign-in (the picture comes from the friend's Vexel).
    /// </summary>
    public static int RunGuestChild(string invite, string? name, string? place)
    {
        var output = Console.Out;
        var gate = new object();
        void Emit(PlayerEvent e) { lock (gate) { output.WriteLine(e.ToLine()); output.Flush(); } }
        var parsed = TogetherInvite.TryParse(invite, out var problem);
        if (parsed is null) { Emit(new PlayerEvent { Type = "stage", Stage = PlayerStage.Failed, Message = problem }); return 1; }
        var settings = AppSettings.Load();
        var session = new GuestSession();
        session.StageChanged += (s, m) => Emit(new PlayerEvent { Type = "stage", Stage = s, Message = m });
        session.Log += m => Emit(new PlayerEvent { Type = "log", Message = m });
        session.Stats += s => Emit(new PlayerEvent { Type = "stats", Stats = s });
        session.TogetherChanged += t => Emit(new PlayerEvent { Type = "together", Together = t });
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
            try { string? line; while ((line = Console.In.ReadLine()) is not null) if (line.Trim() == "stop") break; }
            catch { }
            session.Stop();
        }) { IsBackground = true, Name = "xstream-stdin" }.Start();
        int code = session.Run(new GuestOptions
        {
            Invite = parsed, Name = string.IsNullOrWhiteSpace(name) ? Environment.UserName : name.Trim(), Settings = settings,
            Fullscreen = settings.Fullscreen, Place = WindowPlace.Parse(place), StartHidden = true, HardwareDecode = settings.HardwareDecode,
        });
        if (session.LastWindow is { } lw) Emit(new PlayerEvent { Type = "window", Message = lw.ToString() });
        return code;
    }

    /// <summary>Do the slow first-time work now: native libraries, sign-in refresh, a warm TLS connection.</summary>
    private static void Warm(TokenStore store, HttpClient http)
    {
        try
        {
            NativeLibs.Ensure();
            _ = NativeLibs.CanLoad("SDL2");
            _ = H264Decoder.LibVersion; // loads avcodec + avutil
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
