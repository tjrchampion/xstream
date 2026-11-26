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
//   video-test --kbm                   keyboard & mouse as a controller: click to capture, prints the pad it makes (sends nothing)
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
            if (args.Contains("--regions"))
            {
                // Which xCloud regions this account is offered, and how far each is (TCP connect time to its API host).
                var t0 = store.Load()!;
                var raw = await auth.GetStreamingTokenAsync(t0, "xgpuweb");
                foreach (var r in auth.LastRegions)
                    Console.WriteLine($"  {r.Name,-16} {(r.IsDefault ? "default" : "       ")} {await XStream.Core.Xbox.CloudRegions.MeasureMsAsync(r.BaseUri),6:F0} ms  {r.BaseUri}");
                break;
            }
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
            // --latency-test=N: press D-pad right/left N times on the dashboard and time press -> changed picture.
            if (args.FirstOrDefault(a => a.StartsWith("--latency-test=")) is { } lt)
                Environment.SetEnvironmentVariable("XSTREAM_LATENCY_TEST", lt["--latency-test=".Length..]);
            // --resolution=max|1080|720 overrides the saved setting for this session.
            if (args.FirstOrDefault(a => a.StartsWith("--resolution=")) is { } res)
                settings = settings with { Resolution = res["--resolution=".Length..].ToLowerInvariant() switch
                {
                    "1080" or "1080p" => StreamResolution.R1080, "720" or "720p" => StreamResolution.R720, _ => StreamResolution.Max,
                } };
            // "cloud:<titleId>" streams that game from Xbox Cloud Gaming (see `xstream cloud` for title ids).
            string? cloudTitle = pos[1].StartsWith(PlayerHost.CloudPrefix) ? pos[1][PlayerHost.CloudPrefix.Length..] : null;
            var (client, tokens) = cloudTitle is null
                ? store.CreateClientAsync(http, settings).GetAwaiter().GetResult()
                : store.CreateCloudClientAsync(http, settings).GetAwaiter().GetResult();
            var options = PlayerOptions.From(settings, pos[1]) with
            {
                CloudTitleId = cloudTitle,
                Fullscreen = args.Contains("--windowed") ? false : settings.Fullscreen,
                VSync = args.Contains("--no-vsync") ? false : args.Contains("--vsync") || settings.VSync,
                Sharpen = args.FirstOrDefault(a => a.StartsWith("--clarity=")) is { } c ? int.Parse(c["--clarity=".Length..]) : (int)settings.Clarity,
                HardwareDecode = !args.Contains("--software") && settings.HardwareDecode,
                Headless = args.Contains("--headless"),
                // --enhance=0|1: Enhance off/on for this session (A/B).
                Enhance = args.FirstOrDefault(a => a.StartsWith("--enhance=")) is { } en ? en != "--enhance=0" : settings.Enhance,
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
                $"buffer {s.BufferMs,3:F0}ms  {s.VideoHeight}p {s.Fps,3:F0}fps net->screen {s.NetToScreenP50Ms:F1}/{s.NetToScreenP99Ms:F1}ms decode {s.DecodeMs:F1}ms audio {s.AudioQueuedMs:F0}ms  " +
                $"{s.Link}  pad {s.Pads} @{s.PadHz:F0}Hz p99 {s.PadP99Us:F0}us  {s.Verdict}");
            player.Ended += (sum, path) => PrintSummary(sum, path);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; player.Stop(); };
            new Thread(() => { Console.ReadLine(); player.Stop(); }) { IsBackground = true }.Start();
            // --seconds=N: stop by itself after N seconds of streaming (tests).
            if (args.FirstOrDefault(a => a.StartsWith("--seconds=")) is { } secs)
                _ = Task.Delay(TimeSpan.FromSeconds(double.Parse(secs["--seconds=".Length..], System.Globalization.CultureInfo.InvariantCulture))).ContinueWith(_ => player.Stop());
            return player.Run(options, client, () => auth.GetTransferTokenAsync(tokens));
        }
        case "mics":
            // The microphones Vexel can use (Settings > Sound and controls > Which microphone).
            foreach (var m in XStream.Core.Audio.Microphone.Devices()) Console.WriteLine(m);
            break;
        case "link":
            // This device's connection as the connection check sees it (wired / Wi-Fi band, signal, link speed).
            Console.WriteLine(XStream.Core.Diagnostics.LocalLink.Detect() is var l ? $"{l.Describe()}  (channel {l.Channel}, {l.RssiDbm} dBm)" : "");
            break;
        case "video-test":
        {
            // Plays an H.264 file (or a built-in test pattern) through the real receive path:
            // RTP packets -> frame assembler -> decoder -> window, paced at 60 fps. No console needed.
            var file = args.Skip(1).Where((a, k) => !a.StartsWith("--") && args[k] is not ("--size" or "--clarity" or "--enhance" or "--overlay")).FirstOrDefault();
            var stream = file is not null ? File.ReadAllBytes(file) : TestPattern.Stream(80, 45, 600, 60); // 1280x720, 10 s
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double Now() => sw.Elapsed.TotalMilliseconds;
            var mailbox = new FrameMailbox();
            // --size WxH: window size (points), e.g. 960x540 on a 2x Mac screen = a 1920x1080 picture (the Ally's screen).
            var size = args.SkipWhile(a => a != "--size").Skip(1).FirstOrDefault()?.Split('x') is [var sw0, var sh0] ? (W: int.Parse(sw0), H: int.Parse(sh0)) : (W: 1280, H: 720);
            using var window = new VideoWindow(mailbox, Now, new VideoWindowOptions { Title = "XStream video test", Fullscreen = !args.Contains("--windowed"), VSync = args.Contains("--vsync"), StartHidden = args.Contains("--hidden"), Width = size.W, Height = size.H });
            // --clarity 0-3: Clarity boost level.
            if (args.SkipWhile(a => a != "--clarity").Skip(1).FirstOrDefault() is { } cl) window.Sharpen = int.Parse(cl);
            // --enhance 0|1: edge-aware upscaling when the picture is enlarged (on by default).
            if (args.SkipWhile(a => a != "--enhance").Skip(1).FirstOrDefault() is { } en) window.Enhance = en != "0";
            // --overlay stats|detailed|menu: draw the stream overlay with made-up numbers (checks its layout).
            StreamOverlay? demoOverlay = null;
            var rnd = new Random(1);
            if (args.SkipWhile(a => a != "--overlay").Skip(1).FirstOrDefault() is { } ov)
            {
                var demo = AppSettings.Load() with { ShowStatsHud = ov != "menu", StatsDetailed = ov == "detailed" };
                demoOverlay = new StreamOverlay("Xbox Series X", demo, false, consoleControl: true, clips: true);
                demoOverlay.SetRoute("Home network");
                window.Overlay = demoOverlay;
                window.KeyDown = demoOverlay.OnKey;
                if (ov == "menu") demoOverlay.OnKey(0x1B);
                // Sample connection tips and a muted mic, to check how they're drawn.
                demoOverlay.SetTips(XStream.Core.Diagnostics.ConnectionCheck.Analyze(
                    Enumerable.Range(0, 20).Select(_ => new XStream.Core.Diagnostics.CheckSample(22, 50, 2.5, 9, 16)).ToList(),
                    new XStream.Core.Diagnostics.LocalLink { Kind = "Wi-Fi", BandGHz = 2.4, SignalPct = 60 }, false, "Home network", 0));
                demoOverlay.SetMic(true, true, false, false);
            }
            window.FirstPicture += () => Console.WriteLine($"First picture shown after {Now():F0} ms (window {(args.Contains("--hidden") ? "was hidden until now" : "was already visible")})");
            var buffer = new PlayoutBuffer<AccessUnit> { TargetDelayMs = 0 };
            using var pipe = new VideoPipeline(buffer, new H264Decoder(!args.Contains("--software")), mailbox, Now);
            Console.WriteLine($"Decoder: {pipe.Decoder.Backend} ({H264Decoder.LibVersion}). Renderer: {window.RendererName}. Esc to quit.");
            pipe.Start();
            // --kbm: keyboard and mouse drive a virtual controller (your settings' keys and sensitivity, mode On); the pad
            // is printed when it changes, nothing is sent anywhere. Click to capture; F1 toggles a pretend menu (frees the mouse).
            InputPump? kbmPump = null;
            if (args.Contains("--kbm"))
            {
                var settings = AppSettings.Load();
                var kbm = KeyboardMouseInput.FromSettings(settings);
                kbm.Mode = KeyboardMouseMode.On;
                bool menu = false;
                kbm.Blocked = () => menu;
                kbm.Hint += () => Console.WriteLine("Keyboard & mouse ready: click the window to capture. F1 toggles a pretend menu, Esc quits.");
                window.Input = kbm;
                window.KeyDown = sym => { if (sym == 0x4000003A) { menu = !menu; Console.WriteLine(menu ? "menu open (mouse freed)" : "menu closed"); return true; } return false; };
                GamepadState lastPad = default; double lastPrintMs = 0;
                var none = new NoPads();
                kbmPump = new InputPump(none, new InputOptions { KeyboardMouse = kbm }, f =>
                {
                    var s = f[0];
                    if (s == lastPad || Now() - lastPrintMs < 50) return;
                    lastPad = s; lastPrintMs = Now();
                    Console.WriteLine($"{(kbm.Captured ? "captured" : "free    ")}  buttons {s.Buttons,-30} L ({s.LeftX,5:F2},{s.LeftY,5:F2})  R ({s.RightX,5:F2},{s.RightY,5:F2})  LT {s.LeftTrigger:F0} RT {s.RightTrigger:F0}");
                });
                kbmPump.Start();
            }
            using var _kbmStop = kbmPump;
            var packets = H264Packetizer.Packetize(H264Packetizer.AccessUnits(stream)).ToList();
            var depack = new H264Depacketizer();
            int i = 0; double next = Now(), lastPrint = Now();
            bool shot = !args.Contains("--screenshot"); // --screenshot: save one picture after 2 s, then quit
            while (!window.QuitRequested && (i < packets.Count || Now() < next + 200))
            {
                if (!shot && Now() > (demoOverlay is null ? 2000 : 9000))
                {
                    shot = true;
                    window.RequestScreenshot((px, w, h) =>
                    {
                        if (demoOverlay is not null && w == StreamOverlay.CanvasWidth && h == StreamOverlay.CanvasHeight)
                        {
                            // The screenshot is the picture only: blend the overlay on top so its layout can be checked.
                            var ovl = new byte[w * h * 4];
                            demoOverlay.Render(ovl, w * 4);
                            for (int q = 0; q < ovl.Length; q += 4)
                            {
                                int al = ovl[q + 3];
                                for (int ch = 0; ch < 3; ch++) px[q + ch] = (byte)((ovl[q + ch] * al + px[q + ch] * (255 - al)) / 255);
                            }
                        }
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
                    demoOverlay?.UpdateStats(new LiveStats
                    {
                        Mbps = 38 + rnd.NextDouble() * 8, CapMbps = 50, PingMs = 4 + rnd.NextDouble(), JitterMs = 0.8, LossPct = 0.1, BufferMs = 12,
                        Fps = d.Fps, NetToScreenP50Ms = d.ArrivalToPresentP50Ms + 6, NetToScreenP99Ms = d.ArrivalToPresentP99Ms + 9, DecodeMs = pipe.DecodeP50Ms,
                        Link = "Connected", Pads = 1, PadHz = 250, PadP99Us = 300, AudioQueuedMs = 38,
                    }, window.VideoSize);
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

/// <summary>video-test --kbm: no real controllers (keyboard and mouse are the only pad).</summary>
sealed class NoPads : IGamepadBackend
{
    public string Name => "none";
    public int MaxPads => 4;
    public void Poll(Span<GamepadState> states, Span<bool> connected) => connected.Clear();
    public void Rumble(int pad, float low, float high, float leftTrigger, float rightTrigger, int durationMs) { }
    public void Dispose() { }
}

