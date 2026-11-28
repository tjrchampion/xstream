using System.Diagnostics;
using Concentus.Enums;
using Concentus.Structs;
using XStream.Core.Config;
using XStream.Core.Input;
using XStream.Core.Rtp;
using XStream.Core.Stability;
using XStream.Core.Together;
using XStream.Core.Video;
using XStream.Core.Xbox;
using XStream.Player;

/// <summary>
/// Play together without a console, for testing on one machine or a LAN:
///   xstream together-host [file.h264] [--loss 5] [--name Alex] [--panel] [--tone] [--windowed] [--headless]
///                         [--no-upnp] [--no-stun] [--invite-file path] [--seconds N] [--screenshot file.png;secs]
///                         [--stdin [--wait]]
///   xstream together-join &lt;invite | @file&gt; [--loss 5] [--name Sam] [--windowed] [--headless] [--fake-input]
///                         [--seconds N] [--screenshot file.png;secs] [--menu] [--no-stun]
/// The host streams the built-in test pattern (or an .h264 file) through a real TogetherHost and prints each guest's
/// controller; the guest is the real guest player. --loss drops that share of the datagrams the side sends (host) or
/// receives (guest), to prove the resend and keyframe recovery. --stdin takes the launcher's Play together commands on
/// stdin ("together share", "together remove 1"...) and prints each status as the launcher would get it; --wait doesn't
/// start sharing until "together share".
/// </summary>
static class TogetherHarness
{
    static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    static double Num(string[] a, string name, double dflt) =>
        double.TryParse(Opt(a, name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : dflt;
    static readonly string[] ValueOpts = ["--loss", "--name", "--invite-file", "--seconds", "--screenshot", "--size", "--reply-file"];
    static string? Positional(string[] a) =>
        a.Skip(1).Where((x, k) => !x.StartsWith("--") && !ValueOpts.Contains(a[k])).FirstOrDefault();

    static byte[] AnnexB(List<byte[]> nals)
    {
        var ms = new MemoryStream();
        foreach (var n in nals) { ms.Write([0, 0, 0, 1]); ms.Write(n); }
        return ms.ToArray();
    }

    public static int Host(string[] args)
    {
        var file = Positional(args);
        // A small picture: the test pattern's keyframes are raw (about 220 KB at 512x288), like a busy console keyframe.
        var stream = file is not null ? File.ReadAllBytes(file) : TestPattern.Stream(32, 18, 1200, 120);
        var aus = H264Packetizer.AccessUnits(stream).Select(au => (data: AnnexB(au), key: au.Any(n => (n[0] & 0x1F) == 5))).ToList();
        string name = Opt(args, "--name") ?? "Alex";
        double loss = Num(args, "--loss", 0), seconds = Num(args, "--seconds", 0);
        bool headless = args.Contains("--headless");
        var settings = AppSettings.Load() with { ShowStatsHud = false };
        var sw = Stopwatch.StartNew();
        double Now() => sw.Elapsed.TotalMilliseconds;

        VideoWindow? window = null; StreamOverlay? overlay = null; VideoPipeline? pipe = null;
        var buffer = new PlayoutBuffer<AccessUnit> { TargetDelayMs = 0 };
        if (!headless)
        {
            var mailbox = new FrameMailbox();
            window = new VideoWindow(mailbox, Now, new VideoWindowOptions { Title = "Vexel together-host (test pattern)", Fullscreen = false, Width = 1280, Height = 720 });
            overlay = new StreamOverlay("Xbox Series X", settings, false, consoleControl: true, together: TogetherMode.Host);
            overlay.SetRoute("Home network");
            window.Overlay = overlay; window.KeyDown = overlay.OnKey; window.KeyUp = overlay.OnKeyUp; window.Click = overlay.OnClick;
            pipe = new VideoPipeline(buffer, VideoDecoder.Create(true), mailbox, Now);
            pipe.Start();
        }

        // Guests' controllers go into this pump exactly as on a real stream; it has no local pads, so only theirs print.
        GamepadState[] lastSent = [];
        InputPump? pump = null;
        pump = new InputPump(new NoPads(), new InputOptions(), frame =>
        {
            var remote = frame.Where(f => pump!.IsRemote(f.Index)).ToArray();
            foreach (var f in remote)
            {
                var before = lastSent.FirstOrDefault(x => x.Index == f.Index);
                if (before.Buttons != f.Buttons || Math.Abs(before.LeftX - f.LeftX) > 0.2 || Math.Abs(before.LeftY - f.LeftY) > 0.2)
                    Console.WriteLine($"{Now() / 1000,7:F2}s  Player {f.Index + 1} (pad {f.Index}): buttons {f.Buttons,-28} L ({f.LeftX,5:F2},{f.LeftY,5:F2}) R ({f.RightX,5:F2},{f.RightY,5:F2}) LT {f.LeftTrigger:F2} RT {f.RightTrigger:F2}");
            }
            lastSent = remote;
        }, (i, on) => Console.WriteLine($"{Now() / 1000,7:F2}s  console told: controller {i} {(on ? "connected" : "disconnected")}"));
        pump.Start();

        bool wantKey = false;
        double? capKbps = null;
        var share = new TogetherShare(settings, name, overlay, () => pump, () => wantKey = true,
            k => { capKbps = k; Console.WriteLine(k is { } c ? $"Console bitrate cap: {c / 1000:F1} Mbps" : "Console bitrate cap: off"); },
            m => Console.WriteLine(m),
            o => o with { SimulatedLossPct = loss, Loopback = true, PortMapping = !args.Contains("--no-upnp"), Stun = !args.Contains("--no-stun") });
        if (headless) { share.ClipboardSet = _ => false; share.ClipboardGet = () => ""; }
        // Rumble test: a guest pressing A gets a short buzz back (the console's rumble for their slot).
        var aDown = new bool[4];
        share.GuestInput += (slot, s) =>
        {
            bool a = (s.Buttons & GamepadButtons.A) != 0;
            if (a && !aDown[slot]) share.OnRumble(slot, 0.5f, 0.5f, 0, 0, 120);
            aDown[slot] = a;
        };
        string lastStatus = "";
        share.Changed += st =>
        {
            string line = $"Together: {st.Players} player(s){(st.Guests.Length > 0 ? ": " + string.Join(", ", st.Guests) : "")}";
            if (line != lastStatus) Console.WriteLine(lastStatus = line);
        };
        if (args.Contains("--stdin"))
        {
            share.Changed += st => Console.WriteLine(new PlayerEvent { Type = "together", Together = st }.ToLine());
            new Thread(() =>
            {
                string? l;
                while ((l = Console.In.ReadLine()) is not null)
                    if (TogetherCommand.TryParse(l) is { } c) share.Run(c);
                    else if (l.Trim().Length > 0) Console.WriteLine("Not a together command: " + l);
            }) { IsBackground = true }.Start();
        }
        string? inviteFile = Opt(args, "--invite-file");
        if (!args.Contains("--wait")) share.Start();
        else share.Announce();
        if (args.Contains("--panel")) overlay?.OpenTogetherPanel();
        Console.WriteLine($"Sharing a {(file is null ? "test pattern" : Path.GetFileName(file))} as \"{name}\"{(loss > 0 ? $", dropping {loss}% of what's sent" : "")}. Esc or Ctrl+C to stop.");

        // XSTREAM-style screenshot of the window with the overlay, for the README.
        if (window is not null && Opt(args, "--screenshot") is { } shotSpec && shotSpec.Split(';') is [var shotPath, var shotAt])
            _ = Task.Delay(TimeSpan.FromSeconds(double.Parse(shotAt, System.Globalization.CultureInfo.InvariantCulture)))
                .ContinueWith(_ => window.RequestScreenshot((px, w, h) => Console.WriteLine("Screenshot: " + Screenshots.SaveWithOverlay(px, w, h, overlay, shotPath))));

        // A quiet tone as the game's sound (--tone), Opus like the console's, 20 ms a packet.
        OpusEncoder? opus = args.Contains("--tone") ? new OpusEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO) { Bitrate = 64000 } : null;
        var pcm = new short[960 * 2]; var opusBuf = new byte[1500]; long sample = 0; ushort audioSeq = 0; double nextAudio = Now();

        var quit = new ManualResetEventSlim();
        using var _games = XStream.Core.Native.WindowsStreaming.GameThread(); // the "console": keep 60 fps when not in front
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
        int i = 0; long frameNo = 0; double next = Now(), lastPrint = Now();
        bool printedInvite = false, replyTaken = false;
        string? replyFile = Opt(args, "--reply-file");
        while (!quit.IsSet && !(window?.QuitRequested ?? false) && (seconds <= 0 || Now() < seconds * 1000))
        {
            if (!printedInvite && share.Invite is { } inv)
            {
                printedInvite = true;
                // --hide-addresses: the invite only points at an address nobody answers on, so the guest has to use the
                // reply code (tests the fallback on one machine).
                if (args.Contains("--hide-addresses") && TogetherInvite.TryParse(inv, out _) is { } full)
                    inv = new TogetherInvite(full.SessionId, full.Key, [new Candidate(CandidateKind.Stun, System.Net.IPEndPoint.Parse("203.0.113.7:50123"))]).Encode();
                Console.WriteLine("\nInvite (paste it into together-join, or Vexel's Play together page):\n" + inv + "\n");
                if (inviteFile is not null) File.WriteAllText(inviteFile, inv);
            }
            // --reply-file: "Paste reply code" from a file (the guest's together-join --reply-file writes it).
            if (replyFile is not null && !replyTaken && File.Exists(replyFile) && share.Host is { } rh)
            {
                replyTaken = true;
                Console.WriteLine(rh.AcceptReply(File.ReadAllText(replyFile), out var why) ? "Reply code accepted" : "Reply code refused: " + why);
            }
            if (Now() >= next)
            {
                // The console answers a keyframe request with an IDR: jump to the next one in the pattern.
                if (wantKey)
                {
                    wantKey = false;
                    int k = aus.FindIndex(i, x => x.key);
                    i = k >= 0 ? k : aus.FindIndex(x => x.key);
                }
                var (data, key) = aus[i];
                uint ts = (uint)(frameNo * 1500);
                var au = new AccessUnit(data, ts, key, Now());
                share.OnVideo(au);
                if (pipe is not null) { buffer.Enqueue(au, ts, Now()); pipe.Notify(); }
                i = (i + 1) % aus.Count; frameNo++;
                next += 1000.0 / 60;
                if (Now() - next > 100) next = Now();
            }
            if (opus is not null && Now() >= nextAudio)
            {
                for (int s = 0; s < 960; s++, sample++) { short v = (short)(Math.Sin(sample * 2 * Math.PI * 440 / 48000) * 1500); pcm[2 * s] = pcm[2 * s + 1] = v; }
                int n = opus.Encode(pcm, 0, 960, opusBuf, 0, opusBuf.Length);
                share.OnAudio(opusBuf[..n], audioSeq++);
                nextAudio += 20;
            }
            share.OnMainThread(); // headless too: the --stdin commands run here
            if (window is not null) { window.PumpOnce(1); overlay?.Apply(window); }
            // Headless: macOS stretches a short sleep to many ms in a process with no window while another app is in
            // front (the frame rate halved); spin near the deadline instead (testing only, costs one core).
            else if (next - Now() > 3) Thread.Sleep(1);
            else Thread.SpinWait(200);
            if (Now() - lastPrint >= 1000)
            {
                lastPrint = Now();
                var h = share.Host;
                if (h is not null && h.Guests.Count > 0)
                    Console.WriteLine($"{Now() / 1000,7:F1}s  sent {h.FramesSent} frames, {h.FragmentsSent} pieces, resent {h.FragmentsResent}, keyframes asked {h.KeyframesAsked}, cap {(capKbps / 1000)?.ToString("F1") ?? "-"} Mbps  |  " +
                        string.Join("  ", h.Guests.Select(g => $"{g.PlayerName} {g.Name}: ping {g.PingMs:F1} ms, loss {g.LossPct:F1}%, {g.Kbps / 1000:F1} Mbps")));
            }
        }
        share.Dispose();
        pump.Dispose();
        pipe?.Dispose();
        window?.Dispose();
        return 0;
    }

    /// <summary>together-invite ip:port [ip:port...]: an invite for these addresses (testing: e.g. an address nobody answers on).</summary>
    public static int MakeInvite(string[] args)
    {
        var cands = args.Skip(1).Select(a => new Candidate(CandidateKind.Lan, System.Net.IPEndPoint.Parse(a))).ToList();
        Console.WriteLine(TogetherInvite.New(cands).Encode());
        return 0;
    }

    public static int Join(string[] args)
    {
        string? code = Positional(args);
        if (code is null) { Console.WriteLine("together-join <invite | @file>"); return 1; }
        if (code.StartsWith('@'))
        {
            var path = code[1..];
            for (int k = 0; k < 100 && !File.Exists(path); k++) Thread.Sleep(100); // the host may still be writing it
            code = File.ReadAllText(path);
        }
        var invite = TogetherInvite.TryParse(code, out var problem);
        if (invite is null) { Console.WriteLine(problem); return 1; }
        Console.WriteLine($"Invite: session {invite.SessionId:X8}, addresses {string.Join(", ", invite.Candidates)}");
        double seconds = Num(args, "--seconds", 0);
        var guest = new GuestSession();
        guest.StageChanged += (s, m) => Console.WriteLine($"[{s}] {m.Replace("{view}", "View").Replace("{menu}", "Menu")}");
        guest.Log += Console.WriteLine;
        string? replyFile = Opt(args, "--reply-file");
        guest.TogetherChanged += t =>
        {
            if (t.ReplyCode is not { } rc) return;
            Console.WriteLine("\nReply code (the host pastes it under Play together):\n" + rc + "\n");
            if (replyFile is not null) File.WriteAllText(replyFile, rc);
        };
        TogetherClientStats last = default;
        guest.ClientStats += s =>
        {
            last = s;
            if (s.FramesDelivered > 0)
                Console.WriteLine($"ping {s.RttMs,5:F1} ms  loss {s.LossPct,4:F1}%  {s.Kbps / 1000,5:F1} Mbps  frames {s.FramesDelivered}  resent pieces {s.Recovered}/{s.Missed} missed  " +
                                  $"dropped frames {s.FramesDropped}  keyframes asked {s.KeyframeRequests}  host->guest p50 {s.HostToGuestP50Ms:F2} ms p99 {s.HostToGuestP99Ms:F1} ms");
        };
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; guest.Stop(); };
        if (seconds > 0) _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => guest.Stop());
        if (Opt(args, "--screenshot") is { } shot) Environment.SetEnvironmentVariable("XSTREAM_SCREENSHOT", shot);
        int code2 = guest.Run(new GuestOptions
        {
            Invite = invite, Name = Opt(args, "--name") ?? "Sam", Settings = AppSettings.Load() with { ShowStatsHud = false },
            Fullscreen = false, StartHidden = false, Headless = args.Contains("--headless"),
            SimulatedLossPct = Num(args, "--loss", 0), Stun = !args.Contains("--no-stun"), FakeInput = args.Contains("--fake-input"),
            OpenMenuAfterSeconds = args.Contains("--menu") ? 2.5 : 0,
        });
        Console.WriteLine($"Summary: {last.FramesDelivered} frames, {last.Recovered} pieces recovered by resend of {last.Missed} missed, {last.FramesDropped} frames dropped, " +
                          $"{last.KeyframeRequests} keyframe requests; host->guest p50 {last.HostToGuestP50Ms:F2} ms, p99 {last.HostToGuestP99Ms:F1} ms");
        return code2;
    }
}
