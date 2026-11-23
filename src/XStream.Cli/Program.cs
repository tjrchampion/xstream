using XStream.Core.Config;
using XStream.Core.Input;
using XStream.Core.Rtp;
using XStream.Core.Stability;
using XStream.Core.Video;
using XStream.Core.Xbox;
using XStream.Player;
using XStream.WebRtc;

// xstream - command-line client. Connects to your console and prints live stability stats.
// The launcher UI (src/XStream.App) does the same with a window; both share XStream.Player.
// play uses settings.json (written by the launcher) unless profile/maxMbps are given here.
//
//   xstream login                      sign in with a code (approve on your phone)
//   xstream consoles                   list consoles on your account
//   xstream play <serverId> [profile] [maxMbps]   (or cloud:<titleId> for Xbox Cloud Gaming)
//        profile: competitive | balanced | smooth (default: from settings, balanced)
//        Every session is logged to ~/.xstream/logs/<date>-<profile>.csv (+ .summary.json)
//   xstream library [--raw]           recently played games + art (what the connecting screen shows)
//   xstream cloud [n]                  Xbox Cloud Gaming access and games (read-only)
//   xstream report [file.csv]          summary of a session (latest if no file given)
//   xstream pad                        controller test: live state, poll rate, input pipeline latency
//   xstream video-test [file.h264]     decode + window test (built-in test pattern if no file)
//   video-test --screenshot            saves one picture to Pictures/XStream after 2 s (checks the screenshot path)
//   play/video-test options: --windowed  --headless (no video)  --software (no GPU decode)  --vsync

var dir = AppPaths.Root;
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var auth = new XboxAuth(http);
var store = new TokenStore(auth);
string cmd = args.Length > 0 ? args[0] : "help";

try
{
    switch (cmd)
    {
        case "login":
        {
            var prompt = await auth.StartDeviceCodeAsync();
            Console.WriteLine($"Go to {prompt.VerificationUri} and enter code: {prompt.UserCode}");
            var t = await auth.PollDeviceCodeAsync(prompt);
            store.Save(t);
            Console.WriteLine("Signed in. Tokens saved to " + AppPaths.Tokens);
            break;
        }
        case "library":
        {
            var (authz, xuid) = await store.GetWebTokenAsync();
            if (args.Contains("--raw"))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://titlehub.xboxlive.com/users/xuid({xuid})/titles/titlehistory/decoration/image?maxItems=5");
                req.Headers.Add("Authorization", authz); req.Headers.Add("x-xbl-contract-version", "2");
                Console.WriteLine(await (await http.SendAsync(req)).Content.ReadAsStringAsync());
                break;
            }
            var now = await XboxLibrary.GetNowPlayingAsync(http, authz, xuid);
            Console.WriteLine(now is { } np ? $"Now playing on Xbox: {np.Name} ({np.TitleId})" : "Now playing on Xbox: nothing (or console off)");
            foreach (var g in await XboxLibrary.GetRecentAsync(http, authz, xuid, AppSettings.Load().Locale))
                Console.WriteLine($"{g.LastPlayed:yyyy-MM-dd}  {g.Name,-40} {g.HeroUrl}");
            break;
        }
        case "cloud":
        {
            // Xbox Cloud Gaming: which offering this account gets, and its games (read-only; starts nothing).
            var settings = AppSettings.Load();
            var (cc, ct2) = await store.CreateCloudClientAsync(http, settings);
            Console.WriteLine($"Cloud gaming: {ct2.CloudOffering} via {ct2.CloudHost}");
            var all = await cc.GetCloudTitlesAsync();
            var recent = await cc.GetCloudTitlesAsync(recent: true);
            if (args.Contains("--ids")) { foreach (var t in all.Take(5)) Console.WriteLine($"  {t.TitleId} {t.ProductId} entitled={t.Entitled}"); Console.WriteLine($"{all.Count} titles"); break; }
            var games = await XboxLibrary.ResolveCloudGamesAsync(http, all, settings.Locale);
            Console.WriteLine($"{all.Count} titles, {all.Count(t => t.Entitled)} playable, {recent.Count} recently played");
            foreach (var g in games.Where(g => g.Entitled).Take(int.TryParse(args.Skip(1).FirstOrDefault(), out var n) ? n : 12))
                Console.WriteLine($"  {g.TitleId,-28} {g.Name,-42} {(g.FreeToPlay ? "free" : "")} {(g.PosterUrl is null ? "(no art)" : "")}");
            break;
        }
        case "consoles":
        {
            if (!store.SignedIn) throw new XboxApiException("Not signed in. Run: xstream login", 401);
            var (client, _) = await store.CreateClientAsync(http, AppSettings.Load());
            foreach (var c in await client.GetConsolesAsync())
                Console.WriteLine($"{c.ServerId}  {c.DeviceName,-20} {c.ConsoleType,-14} {c.PowerState}{(c.WirelessWarning ? "  (console on Wi-Fi: wire it for best results)" : "")}");
            break;
        }
        case "play" when args.Length >= 2:
        {
            // Runs on the main thread (no 'await'): macOS requires the video window to be created and driven there.
            var pos = args.Where(a => !a.StartsWith("--")).ToArray();
            var settings = AppSettings.Load();
            var profile = pos.Length > 2 ? Enum.Parse<StreamProfile>(pos[2], ignoreCase: true) : settings.Profile;
            double maxMbps = pos.Length > 3 ? double.Parse(pos[3]) : settings.MaxMbps;
            settings = settings with { Profile = profile, MaxMbps = maxMbps };
            // "cloud:<titleId>" streams that game from Xbox Cloud Gaming (see `xstream cloud` for title ids).
            string? cloudTitle = pos[1].StartsWith(PlayerHost.CloudPrefix) ? pos[1][PlayerHost.CloudPrefix.Length..] : null;
            var (client, tokens) = cloudTitle is null
                ? store.CreateClientAsync(http, settings).GetAwaiter().GetResult()
                : store.CreateCloudClientAsync(http, settings).GetAwaiter().GetResult();
            var options = PlayerOptions.From(settings, pos[1]) with
            {
                CloudTitleId = cloudTitle,
                Fullscreen = args.Contains("--windowed") ? false : settings.Fullscreen,
                VSync = args.Contains("--vsync") || settings.VSync,
                HardwareDecode = !args.Contains("--software") && settings.HardwareDecode,
                Headless = args.Contains("--headless"),
            };

            var player = new PlayerSession();
            player.StageChanged += (st, m) => Console.WriteLine($"[{st}] {m.Replace("{view}", "View").Replace("{menu}", "Menu")}");
            player.Log += Console.WriteLine;
            player.Info += i =>
            {
                Console.WriteLine($"Audio: {i.Audio}. Video: {i.Decoder} decode, {i.Renderer} renderer. Controller: {i.Controller}, channel: " +
                    (i.LowLatencyInput ? "unordered/no-retransmit (patched SIPSorcery)" : "ordered/reliable (run scripts/get-sipsorcery to enable the low-latency channel)"));
                Console.WriteLine($"Bitrate caps (REMB) negotiated: {i.Remb}. Packet resend (NACK) negotiated: {i.Nack}. Logging to {i.LogPath}.");
                Console.WriteLine("Press Esc in the window (or Enter here) to stop.");
            };
            player.Stats += s => Console.WriteLine(
                $"{s.TimeS,6:F1}s  {s.Mbps,5:F1} Mbps (cap {s.CapMbps,4:F1})  loss {s.LossPct,4:F1}%  ping {s.PingMs,3:F0}ms  jitter {s.JitterMs,4:F1}ms  " +
                $"buffer {s.BufferMs,3:F0}ms  {s.VideoHeight}p {s.Fps,3:F0}fps net->screen {s.NetToScreenP50Ms:F1}/{s.NetToScreenP99Ms:F1}ms decode {s.DecodeMs:F1}ms  " +
                $"{s.Link}  pad {s.Pads} @{s.PadHz:F0}Hz p99 {s.PadP99Us:F0}us  {s.Verdict}");
            player.Ended += (sum, path) => PrintSummary(sum, path);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; player.Stop(); };
            new Thread(() => { Console.ReadLine(); player.Stop(); }) { IsBackground = true }.Start();
            return player.Run(options, client, () => auth.GetTransferTokenAsync(tokens));
        }
        case "video-test":
        {
            // Plays an H.264 file (or a built-in test pattern) through the real receive path:
            // RTP packets -> frame assembler -> decoder -> window, paced at 60 fps. No console needed.
            var file = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
            var stream = file is not null ? File.ReadAllBytes(file) : TestPattern.Stream(80, 45, 600, 60); // 1280x720, 10 s
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double Now() => sw.Elapsed.TotalMilliseconds;
            var mailbox = new FrameMailbox();
            using var window = new VideoWindow(mailbox, Now, new VideoWindowOptions { Title = "XStream video test", Fullscreen = !args.Contains("--windowed"), VSync = args.Contains("--vsync"), StartHidden = args.Contains("--hidden") });
            window.FirstPicture += () => Console.WriteLine($"First picture shown after {Now():F0} ms (window {(args.Contains("--hidden") ? "was hidden until now" : "was already visible")})");
            var buffer = new PlayoutBuffer<AccessUnit> { TargetDelayMs = 0 };
            using var pipe = new VideoPipeline(buffer, new H264Decoder(!args.Contains("--software")), mailbox, Now);
            Console.WriteLine($"Decoder: {pipe.Decoder.Backend} ({H264Decoder.Version}). Renderer: {window.RendererName}. Esc to quit.");
            pipe.Start();
            var packets = H264Packetizer.Packetize(H264Packetizer.AccessUnits(stream)).ToList();
            var depack = new H264Depacketizer();
            int i = 0; double next = Now(), lastPrint = Now();
            bool shot = !args.Contains("--screenshot"); // --screenshot: save one picture after 2 s, then quit
            while (!window.QuitRequested && (i < packets.Count || Now() < next + 200))
            {
                if (!shot && Now() > 2000)
                {
                    shot = true;
                    window.RequestScreenshot((px, w, h) =>
                    {
                        Console.WriteLine($"Screenshot {w}x{h}: {Screenshots.Save(px, w, h, "video test")}");
                        Environment.Exit(0);
                    });
                }
                if (i < packets.Count && Now() >= next)
                {
                    uint ts = packets[i].Timestamp;
                    for (; i < packets.Count && packets[i].Timestamp == ts; i++)
                        foreach (var au in depack.Push(packets[i].Seq, packets[i].Timestamp, packets[i].Marker, packets[i].Payload, Now()))
                        { buffer.Enqueue(au, au.RtpTimestamp, Now()); pipe.Notify(); }
                    next += 1000.0 / 60;
                }
                window.PumpOnce(1);
                if (Now() - lastPrint >= 1000)
                {
                    lastPrint = Now();
                    var d = window.TakeStats();
                    Console.WriteLine($"{d.Fps,3:F0} fps  network->screen p50 {d.ArrivalToPresentP50Ms:F2} ms  p99 {d.ArrivalToPresentP99Ms:F2} ms  decode p50 {pipe.DecodeP50Ms:F2} ms  dropped-for-latency {d.Dropped}");
                }
            }
            break;
        }
        case "pad":
        {
            using var backend = GamepadBackends.CreateDefault();
            Console.WriteLine($"Controller backend: {backend.Name}. Press A to test rumble. Ctrl+C to quit.");
            bool aDown = false;
            GamepadState[] last = Array.Empty<GamepadState>();
            var pump = new InputPump(backend, new InputOptions(), frame =>
            {
                last = frame;
                bool a = frame.Length > 0 && (frame[0].Buttons & GamepadButtons.A) != 0;
                if (a && !aDown) backend.Rumble(frame[0].Index, 0.6f, 0.6f, 0.4f, 0.4f, 200);
                aDown = a;
            }, (i, added) => Console.WriteLine($"\nController {i} {(added ? "connected" : "disconnected")}"));
            pump.Start();
            var quit = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
            while (!quit.Wait(250))
            {
                var st = pump.Stats();
                pump.ResetWindow();
                var f = last.Length > 0 ? last[0] : default;
                Console.Write($"\r{st.PadsConnected} pad(s)  poll {st.PollHzActual,5:F0} Hz (worst gap {st.WorstPollGapMs,4:F1} ms)  " +
                              $"change->send p50 {st.DetectToSendP50Us,4:F0} us p99 {st.DetectToSendP99Us,4:F0} us  " +
                              $"L({f.LeftX,5:F2},{f.LeftY,5:F2}) R({f.RightX,5:F2},{f.RightY,5:F2}) LT {f.LeftTrigger:F2} RT {f.RightTrigger:F2}  {f.Buttons,-30}");
            }
            pump.Stop();
            Console.WriteLine();
            break;
        }
        case "report":
        {
            string? file = args.Length > 1 ? args[1]
                : Directory.Exists(AppPaths.Logs) ? Directory.GetFiles(AppPaths.Logs, "*.csv").OrderBy(f => f).LastOrDefault() : null;
            if (file is null) { Console.WriteLine("No session logs yet."); break; }
            PrintSummary(SessionSummary.FromCsv(file), file);
            break;
        }
        default:
            Console.WriteLine("xstream login | consoles | play <serverId> [competitive|balanced|smooth] [maxMbps] [--windowed|--headless|--software|--vsync] | report [file.csv] | pad | video-test [file.h264]");
            break;
    }
}
catch (XboxApiException ex)
{
    Console.Error.WriteLine("Xbox API error: " + ex.Message);
    return 1;
}
return 0;

static void PrintSummary(SessionSummary s, string file)
{
    Console.WriteLine();
    Console.WriteLine($"Session: {Path.GetFileName(file)}  ({s.Minutes:F1} min, {s.Profile})");
    Console.WriteLine($"  Freezes (250 ms+):  {s.Freezes}  ({s.FreezesPerHour:F1} per hour)");
    Console.WriteLine($"  Frozen time:        {s.FrozenSec:F1} s  ({s.FrozenPct:F2}% of the session)");
    Console.WriteLine($"  Reconnects:         {s.Reconnects}{(s.Failed ? "  - session FAILED" : "")}");
    Console.WriteLine($"  Average bitrate:    {s.AvgMbps:F1} Mbps");
    Console.WriteLine($"  Ping (median):      {s.MedianRttMs:F0} ms");
    Console.WriteLine($"  Packet loss:        {s.AvgLossPct:F2}%  ({s.NacksRecovered} packets recovered by resend, {s.FramesLost} frames lost)");
    Console.WriteLine($"  Console follows bitrate caps: {s.Caps}   REMB: {s.RembNegotiated}   NACK: {s.NackNegotiated}");
    if (s.InputPollHz > 0)
        Console.WriteLine($"  Controller:         read {s.InputPollHz:F0}x/s, change->send p50 {s.InputP50Us:F0} us, p99 {s.InputP99Us:F0} us, worst read gap {s.InputWorstGapMs:F1} ms");
    foreach (var (v, pct) in s.VerdictPct.OrderByDescending(kv => kv.Value)) Console.WriteLine($"    {pct,5:F1}%  {v}");
    Console.WriteLine("  Paste these into the test bench's session log, or upload the .csv there.");
}

