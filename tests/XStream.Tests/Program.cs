using System.Buffers.Binary;
using System.Text.Json.Nodes;
using XStream.Core.Config;
using XStream.Core.Input;
using XStream.Core.Video;
using XStream.Core.Rtp;
using XStream.Core.Stability;
using XStream.Core.Xbox;
using XStream.Player;

// Dependency-free test runner. `dotnet run --project tests/XStream.Tests`
var tests = new List<(string name, Action body)>
{
    ("REMB encodes and decodes bitrates", () =>
    {
        foreach (var kbps in new[] { 500.0, 2_000, 9_876.5, 20_000, 150_000 })
        {
            var p = Rtcp.BuildRemb(0x1234, kbps, 0xABCDEF01);
            Eq(24, p.Length);
            Eq((byte)206, p[1]);
            Eq(0xABCDEF01u, BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(20)));
            Near(kbps, Rtcp.ParseRembKbps(p), kbps * 0.0001 + 0.001);
        }
    }),
    ("PLI has the right header", () =>
    {
        var p = Rtcp.BuildPli(1, 2);
        Eq(12, p.Length); Eq((byte)0x81, p[0]); Eq((byte)206, p[1]); Eq((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2)));
    }),
    ("Monitor counts loss and survives sequence wrap-around", () =>
    {
        var m = new RtpReceiveMonitor();
        ushort seq = 65_500; double t = 0; uint ts = 0;
        int sent = 0, dropped = 0;
        for (int f = 0; f < 60; f++, ts += 1500)
            for (int i = 0; i < 5; i++, seq++, sent++)
            {
                t += 3;
                if (sent % 50 == 7) { dropped++; continue; }
                m.OnPacket(seq, ts, i == 4, 1000, t);
            }
        m.OnPacket(seq, ts, true, 1000, t + 3); m.OnPacket((ushort)(seq + 1), ts + 1500, true, 1000, t + 6);
        var s = m.TakeSample(t + 10);
        Eq(dropped, (int)m.TotalLost);
        Near(dropped / (double)(sent + 2), s.LossFraction, 0.01);
        True(s.BrokenFrames >= dropped - 1, "broken frames counted");
    }),
    ("Late retransmission repairs a gap instead of counting loss", () =>
    {
        var m = new RtpReceiveMonitor();
        m.OnPacket(10, 0, false, 1000, 0);
        m.OnPacket(12, 0, true, 1000, 1);   // 11 missing
        m.OnPacket(13, 1500, true, 1000, 17);
        m.OnPacket(11, 0, false, 1000, 20); // retransmission, older frame
        m.OnPacket(14, 3000, true, 1000, 34);
        var s = m.TakeSample(40);
        Eq(0, (int)m.TotalLost);
        Eq(0.0, s.LossFraction);
        Eq(2, s.CompleteFrames); // frame 1500 and 3000 complete; frame 0 broken at assembly time
    }),
    ("Queue delay rises when arrivals fall behind sender time", () =>
    {
        var m = new RtpReceiveMonitor();
        for (int i = 0; i < 100; i++) m.OnPacket((ushort)i, (uint)(i * 1500), true, 1000, i * 16.67);
        var calm = m.TakeSample(1700);
        for (int i = 100; i < 160; i++) m.OnPacket((ushort)i, (uint)(i * 1500), true, 1000, i * 16.67 + (i - 100) * 2.0);
        var busy = m.TakeSample(160 * 16.67 + 130);
        True(calm.QueueDelayMs < 2, $"calm queue {calm.QueueDelayMs}");
        True(busy.QueueDelayMs > 40 && busy.DelayTrendMs > 20, $"busy queue {busy.QueueDelayMs} trend {busy.DelayTrendMs}");
    }),
    ("H.264 keyframe detection (single, STAP-A, FU-A)", () =>
    {
        True(H264.IsKeyframeStart(new byte[] { 0x65, 0x88 }), "IDR");
        False(H264.IsKeyframeStart(new byte[] { 0x41, 0x9a }), "P slice");
        True(H264.IsKeyframeStart(new byte[] { 0x78, 0x00, 0x02, 0x67, 0x42, 0x00, 0x02, 0x68, 0xCE }), "STAP-A with SPS");
        True(H264.IsKeyframeStart(new byte[] { 0x7C, 0x85, 0x00 }), "FU-A start IDR");
        False(H264.IsKeyframeStart(new byte[] { 0x7C, 0x05, 0x00 }), "FU-A middle");
    }),
    ("Bitrate cap drops on queue build-up before any loss", () =>
    {
        var abr = new AdaptiveBitrateController(new StabilityOptions { StartKbps = 15_000 });
        double t = 0;
        for (; t < 3_000; t += 100) abr.Update(S(t, kbps: 15_000));
        double before = abr.CapKbps;
        abr.Update(S(t += 100, kbps: 14_000, queue: 40, trend: 8));
        abr.Update(S(t += 100, kbps: 14_000, queue: 48, trend: 8)); // still building: real congestion
        True(abr.CapKbps < before * 0.9, $"cut {before} -> {abr.CapKbps}");
        Eq("congestion", abr.LastReason);
    }),
    ("A one-tick burst (keyframe) on a healthy link doesn't cut the bitrate", () =>
    {
        // Pattern from a real Series X session: per-100 ms rate swings 4-12 Mbps, then one window with a
        // 35 ms queue and a 4.6 Mbps dip as a big frame arrives. The old controller cut to 3.9 Mbps here.
        var abr = new AdaptiveBitrateController(new StabilityOptions { StartKbps = 10_000 });
        double t = 0;
        double[] noisy = { 9_400, 7_900, 11_000, 9_400, 7_800, 11_100, 8_000, 11_300, 6_000, 11_600 };
        for (int i = 0; i < 130; i++) abr.Update(S(t += 100, kbps: noisy[i % noisy.Length], queue: 3.5));
        double before = abr.CapKbps;
        abr.Update(S(t += 100, kbps: 4_570, queue: 34.9, trend: 30));
        abr.Update(S(t += 100, kbps: 14_750, queue: 3.9, trend: -30));
        True(abr.CapKbps >= before, $"no cut on a burst: {before} -> {abr.CapKbps}");
        // And when congestion is real, the cut is based on the last 300 ms of throughput, not one tick.
        abr.Update(S(t += 100, kbps: 9_000, queue: 40, trend: 5));
        abr.Update(S(t += 100, kbps: 4_000, queue: 50, trend: 5));
        True(abr.CapKbps > 6_000, $"cut target uses a short average, not one tick: {abr.CapKbps}");
    }),
    ("Severe loss does not spiral the cap down within one burst", () =>
    {
        // Loss with the queue full (link overflowing): cut to 70% of what got through, once per burst.
        var abr = new AdaptiveBitrateController(new StabilityOptions { StartKbps = 15_000 });
        double t = 1_000;
        for (int i = 0; i < 8; i++) abr.Update(S(t += 150, kbps: 10_000, loss: 0.2, queue: 40));
        Near(7_000, abr.CapKbps, 1);
        // Loss with no queue (interference / path burst): cut by half the loss from the pre-burst rate.
        abr = new AdaptiveBitrateController(new StabilityOptions { StartKbps = 15_000 });
        t = 1_000;
        for (int i = 0; i < 8; i++) abr.Update(S(t += 150, kbps: 10_000, loss: 0.2));
        Near(13_500, abr.CapKbps, 1);
    }),
    ("A heavy loss burst with no queue cuts at most by half", () =>
    {
        // Live: 77% of packets lost for 400 ms on wired. Only 23% got through, so a throughput-based cut
        // went to 2.5 Mbps and the console paced its recovery keyframe at that rate.
        var abr = new AdaptiveBitrateController(new StabilityOptions { StartKbps = 40_000, MaxKbps = 40_000 });
        double t = 0;
        for (; t < 3_000; t += 100) abr.Update(S(t, kbps: 28_000));
        for (int i = 0; i < 4; i++) abr.Update(S(t += 100, kbps: 6_000, loss: 0.77, queue: 5));
        True(abr.CapKbps >= 20_000, $"cap {abr.CapKbps}");
    }),
    ("Repeated congestion episodes set a stability ceiling", () =>
    {
        var abr = new AdaptiveBitrateController(new StabilityOptions { StartKbps = 12_000 });
        double t = 0;
        for (int episode = 0; episode < 3; episode++)
        {
            for (int i = 0; i < 80; i++) abr.Update(S(t += 100, kbps: abr.CapKbps)); // 8 s clear, ramps up
            abr.Update(S(t += 100, kbps: abr.CapKbps, loss: 0.15));
        }
        True(abr.CeilingActive(t), "ceiling active");
        for (int i = 0; i < 150; i++) abr.Update(S(t += 100, kbps: abr.CapKbps));
        True(abr.CapKbps <= abr.Ceiling + 1, $"cap {abr.CapKbps} stays under ceiling {abr.Ceiling}");
    }),
    ("A lost recovery keyframe is asked for again", () =>
    {
        // Frames after a declared loss arrive intact but are held back, so "broken" doesn't repeat: without a
        // retry the picture stayed frozen for good when the keyframe itself was lost.
        var k = new KeyframeRecovery();
        True(k.Update(S(1_000, kbps: 20_000), decoderError: true), "first request");
        int extra = 0;
        for (double t = 1_100; t <= 3_000; t += 100) if (k.Update(S(t, kbps: 20_000))) extra++;
        True(extra >= 2, $"retries {extra}");
        k.Update(S(3_100, kbps: 20_000) with { KeyframeSeen = true });
        for (double t = 3_200; t <= 5_000; t += 100) False(k.Update(S(t, kbps: 20_000)), "no requests once a keyframe arrived");
    }),
    ("Resent packets don't read as queueing delay", () =>
    {
        // After a loss burst the console resends the lost packets with their original (older) timestamps.
        // Live this showed as a 1.6 s "queue" and pinned the cap at the minimum.
        var m = new RtpReceiveMonitor();
        ushort seq = 0; double t = 0;
        for (int f = 0; f < 120; f++, t += 16.7)
            for (int i = 0; i < 5; i++) m.OnPacket(seq++, (uint)(f * 1503), i == 4, 1200, t + i * 0.1);
        m.TakeSample(t);
        ushort resend = (ushort)(seq - 400);
        for (int f = 120; f < 180; f++, t += 16.7)
            for (int i = 0; i < 5; i++)
            {
                m.OnPacket(seq++, (uint)(f * 1503), i == 4, 1200, t + i * 0.1);
                m.OnPacket(resend++, (uint)(40 * 1503), false, 1200, t + i * 0.1 + 0.05); // resend of a frame from 1.3 s ago
            }
        var s = m.TakeSample(t);
        True(s.QueueDelayMs < 5, $"queue {s.QueueDelayMs:F1} ms");
    }),
    ("After a one-off hiccup on a clean link the cap is back within seconds", () =>
    {
        // Replays a wired Series X session: ~28 Mbps content, one 77% loss burst 3 s in, then nothing wrong.
        // The console follows the cap. Before the fast climb this took ~14 s to get back to 28 Mbps.
        static double Recover(bool fast)
        {
            var abr = new AdaptiveBitrateController(new StabilityOptions { StartKbps = 40_000, MaxKbps = 40_000, FastClimb = fast });
            double t = 0;
            for (; t < 3_000; t += 100) abr.Update(S(t, kbps: Math.Min(abr.CapKbps, 28_000)));
            for (int i = 0; i < 3; i++) abr.Update(S(t += 100, kbps: 6_000, loss: 0.77, queue: 60, trend: 10));
            double burstEnd = t;
            while (t - burstEnd < 60_000)
            {
                abr.Update(S(t += 100, kbps: Math.Min(abr.CapKbps * 0.98, 29_000), queue: 2));
                if (abr.CapKbps >= 28_000) return (t - burstEnd) / 1000;
            }
            return double.PositiveInfinity;
        }
        double slow = Recover(false), fast = Recover(true);
        Console.WriteLine($"      back to 28 Mbps after {slow:F1} s (steady climb) vs {fast:F1} s (fast climb)");
        True(fast <= 9, $"fast climb took {fast:F1} s");
        True(slow > fast * 1.5, $"steady {slow:F1} s vs fast {fast:F1} s");
    }),
    ("On a flaky link the cap keeps the steady climb", () =>
    {
        // Two congestion episodes inside 40 s: climbing fast would just reach the next collapse sooner,
        // so the climb stays at the steady rate (at most the 20%/s recovery rate, ~2% per 100 ms tick).
        var abr = new AdaptiveBitrateController(new StabilityOptions { StartKbps = 20_000, MaxKbps = 40_000 });
        double t = 0;
        for (int ep = 0; ep < 2; ep++)
        {
            for (int i = 0; i < 50; i++) abr.Update(S(t += 100, kbps: abr.CapKbps));
            abr.Update(S(t += 100, kbps: abr.CapKbps * 0.5, loss: 0.2));
        }
        double maxStep = 0;
        for (int i = 0; i < 150; i++)
        {
            double before = abr.CapKbps;
            abr.Update(S(t += 100, kbps: abr.CapKbps * 0.98, queue: 2));
            maxStep = Math.Max(maxStep, abr.CapKbps / before - 1);
        }
        True(maxStep <= 0.0201, $"largest step {maxStep:P2}");
    }),
    ("Queueing stops the fast climb straight away", () =>
    {
        var abr = new AdaptiveBitrateController(new StabilityOptions { StartKbps = 5_000, MaxKbps = 40_000 });
        double t = 0;
        for (int i = 0; i < 80; i++) abr.Update(S(t += 100, kbps: abr.CapKbps * 0.98, queue: 2)); // 8 s quiet: climbing fast
        double before = abr.CapKbps;
        abr.Update(S(t += 100, kbps: abr.CapKbps * 0.98, queue: 12)); // queue starting to build (not congestion yet)
        double step = abr.CapKbps / before - 1;
        True(step < 0.01, $"step with a building queue {step:P1}");
    }),
    ("Cap always stays within user min/max", () =>
    {
        var o = new StabilityOptions { MinKbps = 3_000, MaxKbps = 12_000, StartKbps = 50_000 };
        var abr = new AdaptiveBitrateController(o);
        Eq(12_000.0, abr.CapKbps);
        double t = 0;
        for (int i = 0; i < 300; i++) abr.Update(S(t += 100, kbps: 30_000));
        True(abr.CapKbps <= 12_000, "max");
        for (int i = 0; i < 50; i++) abr.Update(S(t += 500, kbps: 100, loss: 0.5));
        True(abr.CapKbps >= 3_000, "min");
    }),
    ("Reconnect ladder escalates in order and resets on media", () =>
    {
        var r = new ReconnectSupervisor(new StabilityOptions());
        r.Update(S(100, kbps: 10_000));
        var seen = new List<RecoveryAction>();
        for (double gap = 100; gap <= 21_000; gap += 100)
        {
            var a = r.Update(S(100 + gap, kbps: 0, frames: 0, sinceFrame: gap));
            if (a != RecoveryAction.None) seen.Add(a);
        }
        Eq("RequestKeyframe,RestartIce,RestartIce,RestartIce,Renegotiate,StartNewSession", string.Join(",", seen));
        r.Update(S(30_000, kbps: 10_000));
        Eq(LinkState.Connected, r.State);
        Eq(1, r.Reconnects);
    }),
    ("Keyframe requests are immediate on decode error and rate-limited", () =>
    {
        var k = new KeyframeRecovery();
        True(k.Update(S(1_000, kbps: 10_000), decoderError: true), "first request");
        False(k.Update(S(1_100, kbps: 10_000), decoderError: true), "rate limited");
        True(k.Update(S(1_300, kbps: 10_000), decoderError: true), "re-request while still broken");
        False(k.Update(S(1_400, kbps: 10_000) with { KeyframeSeen = true }), "keyframe arrived");
    }),
    ("A repeat keyframe request waits for loss to stop, but not for long", () =>
    {
        bool lossy = true;
        var k = new KeyframeRecovery { Lossy = _ => lossy };
        True(k.Update(S(1_000, kbps: 10_000), decoderError: true), "first request goes out even in loss");
        False(k.OnPictureLost(1_300, 5), "re-ask held while packets are being lost");
        True(k.Held, "held");
        False(k.PollHeld(1_350), "still lossy");
        lossy = false;
        True(k.PollHeld(1_360), "sent once the link is calm");
        lossy = true;
        False(k.OnPictureLost(1_700, 5), "held again");
        False(k.PollHeld(1_750), "within the hold limit");
        True(k.PollHeld(1_800), "sent anyway after 100 ms");
    }),
    ("Stream format is read from the SPS (size, profile, colour range and matrix)", () =>
    {
        // Made with ffmpeg/x264: 1080p High BT.709 limited range, and 720p Main BT.601 full range.
        var hd = H264Format.Parse(Convert.FromHexString("6764002aacd940780227e5c05a810100a0000003002000000f01e30632c0"))!;
        Eq(1920, hd.Width); Eq(1080, hd.Height); Eq(100, hd.Profile); Eq(42, hd.Level);
        False(hd.FullRange, "limited"); False(hd.Bt601, "709"); Eq(1, hd.Matrix);
        var sd = H264Format.Parse(Convert.FromHexString("674d4020eca02802dd80b702020640000003004000001e03c60c6580"))!;
        Eq(1280, sd.Width); Eq(720, sd.Height); Eq(77, sd.Profile);
        True(sd.FullRange, "full"); True(sd.Bt601, "601");
        var au = Convert.FromHexString("000000016764002aacd940780227e5c05a810100a0000003002000000f01e30632c00000000168ebe3cb22c0");
        Eq(1920, H264Format.FromAccessUnit(au)?.Width);
    }),
    ("Old settings move to the new quality defaults, chosen values stay", () =>
    {
        var old = AppSettings.Migrate(new AppSettings { MaxMbps = 30, StartMbps = 10, Resolution = StreamResolution.Max });
        Eq(50.0, old.MaxMbps); Eq(20.0, old.StartMbps); Eq(StreamResolution.Max, old.Resolution);
        Eq(StreamResolution.Max, AppSettings.Migrate(new AppSettings { SettingsVersion = 2, Resolution = StreamResolution.Auto }).Resolution); // 0.4.2's default
        Eq(StreamResolution.R1080, AppSettings.Migrate(new AppSettings { SettingsVersion = 2, Resolution = StreamResolution.R1080 }).Resolution);
        False(AppSettings.Migrate(new AppSettings { SettingsVersion = 3, GyroAim = true }).GyroAim, "0.4.8 turns gyro aim off once");
        True(AppSettings.Migrate(new AppSettings { SettingsVersion = 4, GyroAim = true }).GyroAim, "chosen after 0.4.8: kept");
        False(new AppSettings().GyroAim, "gyro aim off by default");
        var mine = AppSettings.Migrate(new AppSettings { MaxMbps = 40, StartMbps = 40, Resolution = StreamResolution.R720 });
        Eq(40.0, mine.MaxMbps); Eq(40.0, mine.StartMbps); Eq(StreamResolution.R720, mine.Resolution);
        var again = AppSettings.Migrate(old with { MaxMbps = 30 });
        Eq(30.0, again.MaxMbps); // chosen after the update: not touched again
    }),
    ("Window place survives the trip between launcher and stream", () =>
    {
        var p = new WindowPlace(-1920, 40, 1280, 800);
        Eq(p, WindowPlace.Parse(p.ToString()));
        True(WindowPlace.Parse("") is null, "empty");
        True(WindowPlace.Parse("1,2,3") is null, "too short");
        True(WindowPlace.Parse("1,2,0,5") is null, "zero size");
    }),
    ("Recent loss: clean, lossy, and silent links", () =>
    {
        var l = new RecentLoss();
        ushort seq = 0; double t = 0;
        for (int i = 0; i < 200; i++, t += 0.4) l.OnPacket(seq++, t);
        False(l.IsLossy(t), "clean");
        for (int i = 0; i < 200; i++, t += 0.4) { l.OnPacket(seq, t); seq += 2; } // every other packet missing
        True(l.IsLossy(t), "half lost");
        False(new RecentLoss().IsLossy(0), "no packets yet isn't loss");
        True(l.IsLossy(t + 40), "nothing arriving for 40 ms");
    }),
    ("Engine learns whether the console honours caps", () =>
    {
        var honoured = new StabilityEngine(new StabilityOptions { StartKbps = 15_000 });
        var ignored = new StabilityEngine(new StabilityOptions { StartKbps = 15_000 });
        double t = 0, rate = 15_000;
        for (int i = 0; i < 400; i++)
        {
            t += 100;
            bool congest = i % 60 is >= 30 and < 33; // sustained build-up (one-tick spikes are ignored)
            var d = honoured.Update(S(t, kbps: Math.Min(rate, honoured.Snapshot.CapKbps > 0 ? honoured.Snapshot.CapKbps : rate), queue: congest ? 50 : 0, trend: congest ? 8 : 0));
            ignored.Update(S(t, kbps: 18_000, queue: congest ? 50 : 0, trend: congest ? 8 : 0));
        }
        Eq(CapCompliance.Honoured, honoured.Caps);
        Eq(CapCompliance.Ignored, ignored.Caps);
    }),
    ("Buffer rises fast with jitter and falls slowly", () =>
    {
        var b = new BufferPolicy(new StabilityOptions { Profile = StreamProfile.Balanced }.Tuning);
        double t = 0;
        for (int i = 0; i < 20; i++) b.Update(S(t += 100, kbps: 10_000, jitter: 1));
        double calm = b.TargetMs;
        b.Update(S(t += 100, kbps: 10_000, jitter: 20));
        double rough = b.TargetMs;
        for (int i = 0; i < 5; i++) b.Update(S(t += 100, kbps: 10_000, jitter: 1));
        True(rough > calm + 30, $"rose {calm} -> {rough}");
        True(b.TargetMs > rough - 15, $"fell slowly to {b.TargetMs}");
    }),
    ("Playout buffer paces frames by target delay", () =>
    {
        var pb = new PlayoutBuffer<int> { TargetDelayMs = 20 };
        pb.Enqueue(1, 0, 100);
        pb.Enqueue(2, 1500, 140); // arrived 23 ms late vs 16.7 ms spacing
        False(pb.TryDequeue(119, out _), "not due");
        True(pb.TryDequeue(120, out var f1) && f1 == 1, "frame 1 at 120");
        True(pb.TryDequeue(140, out var f2) && f2 == 2, "frame 2 at arrival (late)");
        Eq(1, pb.LateFrames);
    }),
    ("Teredo decodes the RFC 4380 example address", () =>
    {
        var (ip, port) = Teredo.Decode("2001:0:4136:e378:8000:63bf:3fff:fdd2");
        Eq("192.0.2.45", ip); Eq(40000, port);
        var expanded = Teredo.Expand(new[] { new IceCandidate("a=candidate:1 1 UDP 1 2001:0:4136:e378:8000:63bf:3fff:fdd2 9002 typ host", "0", 0) }).ToList();
        Eq(3, expanded.Count);
        True(expanded[1].Candidate.Contains("192.0.2.45 40000"), expanded[1].Candidate);
    }),
    ("SDP offer gets bitrate, REMB and H.264 High first", () =>
    {
        var sdp = "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\nc=IN IP4 0.0.0.0\r\na=fmtp:111 minptime=10;useinbandfec=1\r\n" +
                  "m=video 9 UDP/TLS/RTP/SAVPF 96 102 104\r\nc=IN IP4 0.0.0.0\r\n" +
                  "a=rtpmap:96 VP8/90000\r\na=rtpmap:102 H264/90000\r\na=fmtp:102 profile-level-id=42e01f\r\n" +
                  "a=rtpmap:104 H264/90000\r\na=fmtp:104 profile-level-id=4d001f\r\n";
        var o = SdpTools.PrepareOffer(sdp, 12_000);
        True(o.Contains("m=video 9 UDP/TLS/RTP/SAVPF 104 102 96"), "order");
        True(o.Contains("m=video 9 UDP/TLS/RTP/SAVPF 104 102 96\r\nc=IN IP4 0.0.0.0\r\nb=AS:12000"), "b=AS after c=");
        True(o.Contains("a=rtcp-fb:104 goog-remb") && o.Contains("a=rtcp-fb:96 goog-remb"), "remb");
        True(o.Contains("useinbandfec=1;stereo=1"), "stereo");
        True(SdpTools.AnswerSupportsRemb(o), "detect");
    }),
    ("Input packets match the reference byte layout", () =>
    {
        var meta = InputPacket.ClientMetadata(0, 123.5);
        Eq(15, meta.Length); Eq((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(meta)); Eq((byte)1, meta[14]);
        var pad = InputPacket.Gamepad(7, 0, new[] { new GamepadState(0, GamepadButtons.A | GamepadButtons.Nexus, 1, -1, 0, 0.5f, 1, 0) });
        Eq(14 + 1 + 23, pad.Length);
        Eq(7u, BinaryPrimitives.ReadUInt32LittleEndian(pad.AsSpan(2)));
        Eq((byte)1, pad[14]);
        Eq((ushort)18, BinaryPrimitives.ReadUInt16LittleEndian(pad.AsSpan(16)));
        Eq((short)32767, BinaryPrimitives.ReadInt16LittleEndian(pad.AsSpan(18)));
        Eq((short)-32767, BinaryPrimitives.ReadInt16LittleEndian(pad.AsSpan(20)));
        Eq((ushort)65535, BinaryPrimitives.ReadUInt16LittleEndian(pad.AsSpan(26)));
    }),
    ("Message channel: handshake, config, dialog auto-accept", () =>
    {
        var m = new MessageChannelProtocol();
        bool started = false; m.HandshakeCompleted += () => started = true;
        Eq("Handshake", JsonNode.Parse(m.Handshake())!["type"]!.GetValue<string>());
        var replies = m.OnMessage("""{"type":"HandshakeAck"}""");
        True(started && m.HandshakeDone, "handshake");
        Eq(6, replies.Count);
        var r = m.OnMessage("""{"type":"TransactionStart","target":"/streaming/systemUi/messages/ShowMessageDialog","id":"x1","content":"{\"TitleText\":\"Hi\",\"ContentText\":\"Ok?\"}"}""");
        var j = JsonNode.Parse(r.Single())!;
        Eq("TransactionComplete", j["type"]!.GetValue<string>()); Eq("x1", j["id"]!.GetValue<string>());
    }),
    ("Control messages are valid JSON", () =>
    {
        Eq("videoKeyframeRequested", JsonNode.Parse(ControlMessages.KeyframeRequest())!["message"]!.GetValue<string>());
        Eq(true, JsonNode.Parse(ControlMessages.GamepadChanged(0, true))!["wasAdded"]!.GetValue<bool>());
    }),
    ("NACK asks once, retries after a round trip, then gives up", () =>
    {
        var n = new NackGenerator();
        n.OnPacket(100, 0); n.OnPacket(103, 1);             // 101, 102 missing
        Eq(0, n.Poll(2, 30).Count);                          // reorder wait
        Eq("101,102", string.Join(",", n.Poll(6, 30)));
        Eq(0, n.Poll(20, 30).Count);                         // too soon to retry
        n.OnPacket(101, 40);                                 // repaired by resend
        Eq("102", string.Join(",", n.Poll(60, 30)));
        Eq(1L, n.Recovered);
        Near(34, n.RttEstimateMs, 0.01);                     // 40 - 6
        n.Poll(110, 30); n.Poll(160, 30);
        Eq(0, n.Poll(500, 30).Count);                        // too old: given up
        Eq(1L, n.GivenUp);
    }),
    ("NACK packet packs sequence runs into PID/BLP pairs", () =>
    {
        var seqs = new ushort[] { 65534, 65535, 0, 5, 40 };
        var p = NackGenerator.BuildNack(1, 2, seqs);
        Eq((byte)205, p[1]); Eq((byte)0x81, p[0]);
        Eq(12 + 4 * 2, p.Length);                            // 65534..5 fit one pair, 40 needs another
        Eq(string.Join(",", seqs), string.Join(",", NackGenerator.ParseNack(p)));
    }),
    ("Frame assembler rebuilds FU-A/STAP-A frames as Annex-B", () =>
    {
        var d = new H264Depacketizer();
        var sps = new byte[] { 0x67, 0x42 }; var pps = new byte[] { 0x68, 0xCE };
        var stap = new byte[] { 0x78, 0, 2, 0x67, 0x42, 0, 2, 0x68, 0xCE };
        var fuStart = new byte[] { 0x7C, 0x85, 0xAA }; var fuEnd = new byte[] { 0x7C, 0x45, 0xBB };
        Eq(0, d.Push(1, 900, false, stap, 0).Count);
        Eq(0, d.Push(2, 900, false, fuStart, 1).Count);
        var f = d.Push(3, 900, true, fuEnd, 2).Single();
        True(f.IsKeyframe, "keyframe");
        Eq("00-00-00-01-67-42-00-00-00-01-68-CE-00-00-00-01-65-AA-BB", BitConverter.ToString(f.Data.ToArray()));
    }),
    ("Retransmitted packet completes the frame; no loss counted", () =>
    {
        var d = new H264Depacketizer { MaxWaitMs = 100 };
        d.Push(1, 0, true, new byte[] { 0x65, 1 }, 0);
        d.Push(2, 1500, false, new byte[] { 0x41, 1 }, 17);
        d.Push(4, 3000, true, new byte[] { 0x41, 3 }, 34);   // 3 (end of frame 1500) missing
        Eq(0, d.Tick(60).Count);
        var out1 = d.Push(3, 1500, true, new byte[] { 0x41, 2 }, 70); // resend arrives in time
        Eq(2, out1.Count);
        Eq(0L, d.FramesLost); Eq(3L, d.FramesDelivered);
    }),
    ("Lost frame freezes the picture until the next keyframe", () =>
    {
        var d = new H264Depacketizer { MaxWaitMs = 100 };
        d.Push(1, 0, true, new byte[] { 0x65, 1 }, 0);
        d.Push(3, 1500, true, new byte[] { 0x41, 2 }, 17);     // seq 2 lost forever
        d.Push(4, 3000, true, new byte[] { 0x41, 3 }, 34);
        Eq(0, d.Tick(200).Count);
        Eq(1L, d.FramesLost); True(d.PictureBroken, "broken"); True(d.TakeDecoderError(), "decoder error raised");
        False(d.TakeDecoderError(), "raised once");
        var k = d.Push(5, 4500, true, new byte[] { 0x65, 4 }, 210);
        True(k.Count == 1 && k[0].IsKeyframe && !d.PictureBroken, "keyframe restores picture");
        True(d.FramesHeldBack >= 1, "P frames held back meanwhile");
    }),
    ("SDP offer asks for packet resend (NACK) and keyframes (PLI)", () =>
    {
        var sdp = "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 102\r\nc=IN IP4 0.0.0.0\r\na=rtpmap:102 H264/90000\r\n";
        var o = SdpTools.PrepareOffer(sdp, 0);
        True(o.Contains("a=rtcp-fb:102 nack\r\n") && o.Contains("a=rtcp-fb:102 nack pli"), o);
        True(SdpTools.AnswerSupportsNack(o), "detect nack");
        False(SdpTools.AnswerSupportsNack("a=rtcp-fb:102 nack pli\r\n"), "pli alone is not nack");
    }),
    ("Controller: a change is sent in the same poll it is seen", () =>
    {
        var pad = new FakePad(); var sent = new List<GamepadState[]>();
        var p = new InputPump(pad, new InputOptions(), f => sent.Add(f));
        for (int w = 0; w < 50; w++) { pad.Set(0, new GamepadState(0, w % 2 == 0 ? GamepadButtons.B : 0, 0, 0, 0, 0, 0, 0)); p.Step(-100 + w); } // warm up JIT
        pad.Set(0, new GamepadState(0, 0, 0, 0, 0, 0, 0, 0)); p.Step(0);
        int before = sent.Count;
        pad.Set(0, new GamepadState(0, GamepadButtons.A, 0, 0, 0, 0, 0, 0)); p.Step(1);
        Eq(before + 1, sent.Count);
        Eq(GamepadButtons.A, sent[^1][0].Buttons);
        True(p.Stats().DetectToSendP50Us < 100, $"pipeline {p.Stats().DetectToSendP50Us} us");
    }),
    ("Controller: idle sends only an 8 ms heartbeat; sensor noise is ignored", () =>
    {
        var pad = new FakePad(); int n = 0;
        var p = new InputPump(pad, new InputOptions(), _ => n++);
        pad.Set(0, new GamepadState(0, 0, 0.5f, 0, 0, 0, 0, 0)); p.Step(0);
        n = 0;
        for (int t = 1; t <= 16; t++) { pad.Set(0, new GamepadState(0, 0, 0.5f + (t % 2) * 0.001f, 0, 0, 0, 0, 0)); p.Step(t); }
        Eq(2, n); // heartbeats at 8 and 16 ms only
    }),
    ("Controller: with none connected, an idle controller keeps the console's session alive", () =>
    {
        var pad = new FakePad(); var sent = new List<GamepadState[]>();
        var p = new InputPump(pad, new InputOptions(), f => sent.Add(f));
        for (int t = 0; t <= 1000; t++) p.Step(t);
        True(sent.Count is >= 4 and <= 5, $"about 4 a second, got {sent.Count}");
        True(sent.All(f => f.Length == 1 && f[0].Buttons == GamepadButtons.None && f[0].LeftX == 0), "idle: nothing pressed");
        sent.Clear();
        pad.Set(0, new GamepadState(0, 0, 0, 0, 0, 0, 0, 0));
        for (int t = 1001; t <= 1100; t++) p.Step(t);
        True(sent.Count > 10, "a real controller takes over (8 ms heartbeat)");
    }),
    ("Controller: radial deadzone keeps direction and full range", () =>
    {
        Eq((0f, 0f), InputPump.Radial(0.03f, 0.02f, 0.04f));
        var (x, y) = InputPump.Radial(0.6f, 0.8f, 0.04f);
        Near(1.0, Math.Sqrt(x * x + y * y), 1e-5); Near(0.8 / 0.6, y / x, 1e-4);
        var (hx, _) = InputPump.Radial(0.52f, 0, 0.04f);
        Near(0.5, hx, 1e-5);
    }),
    ("Controller: release to rest is always sent, even a tiny step", () =>
    {
        var pad = new FakePad(); var sent = new List<GamepadState[]>();
        var p = new InputPump(pad, new InputOptions { StickDeadzone = 0, TriggerDeadzone = 0 }, f => sent.Add(f));
        pad.Set(0, new GamepadState(0, 0, 0, 0, 0, 0, 0.001f, 0)); p.Step(0);
        pad.Set(0, new GamepadState(0, 0, 0, 0, 0, 0, 0, 0)); p.Step(1);
        Eq(0f, sent[^1][0].LeftTrigger); Eq(2, sent.Count);
    }),
    ("Controller: unplugging releases every button before removal", () =>
    {
        var pad = new FakePad(); var sent = new List<GamepadState[]>(); var events = new List<string>();
        var p = new InputPump(pad, new InputOptions(), f => sent.Add(f), (i, a) => events.Add($"{i}:{a}"));
        pad.Set(1, new GamepadState(1, GamepadButtons.RightShoulder, 1, 0, 0, 0, 1, 1)); p.Step(0);
        pad.Unplug(1); p.Step(1);
        Eq("1:True,1:False", string.Join(",", events));
        var release = sent.Last(f => f.Length == 1 && f[0].Index == 1);
        True(release[0].Buttons == 0 && release[0].LeftX == 0 && release[0].RightTrigger == 0, "neutral sent");
    }),
    ("Controller: hold View + Menu for the Xbox button", () =>
    {
        var pad = new FakePad(); var sent = new List<GamepadState[]>();
        var p = new InputPump(pad, new InputOptions(), f => sent.Add(f));
        pad.Set(0, new GamepadState(0, GamepadButtons.View | GamepadButtons.Menu, 0, 0, 0, 0, 0, 0));
        p.Step(0); p.Step(400);
        False((sent[^1][0].Buttons & GamepadButtons.Nexus) != 0, "not yet");
        p.Step(501);
        Eq(GamepadButtons.Nexus, sent[^1][0].Buttons);
    }),
    ("Controller: XInput and SDL mappings", () =>
    {
        var x = XInputBackend.Map(0, 0x1000 | 0x0400 | 0x0001 | 0x0200, 255, 0, 32767, -32768, 0, 16384);
        Eq(GamepadButtons.A | GamepadButtons.Nexus | GamepadButtons.DPadUp | GamepadButtons.RightShoulder, x.Buttons);
        Eq(1f, x.LeftX); Eq(-1f, x.LeftY); Eq(1f, x.LeftTrigger); Near(0.5, x.RightY, 0.001);
        var b = new bool[15]; b[0] = true; b[5] = true; b[14] = true;
        var s = Sdl2Backend.Map(0, b, 0, -32768, 0, 32767, 32767, 0);
        Eq(GamepadButtons.A | GamepadButtons.Nexus | GamepadButtons.DPadRight, s.Buttons);
        Eq(1f, s.LeftY); Eq(-1f, s.RightY); Eq(1f, s.LeftTrigger); // SDL Y-down flipped to Y-up
    }),
    ("Controller: real-time loop sees and sends changes within ~1 ms", () =>
    {
        var pad = new FakePad(); var sw = System.Diagnostics.Stopwatch.StartNew();
        double changedAt = 0; var lags = new List<double>(); bool pressed = false;
        var p = new InputPump(pad, new InputOptions(), f =>
        {
            bool a = (f[0].Buttons & GamepadButtons.A) != 0;
            if (a != pressed) { pressed = a; lock (lags) lags.Add(sw.Elapsed.TotalMilliseconds - changedAt); }
        });
        pad.Set(0, new GamepadState(0, 0, 0, 0, 0, 0, 0, 0));
        p.Start(); Thread.Sleep(50); p.ResetWindow();
        for (int i = 0; i < 60; i++)
        {
            Thread.Sleep(7);
            changedAt = sw.Elapsed.TotalMilliseconds;
            pad.Set(0, new GamepadState(0, i % 2 == 0 ? GamepadButtons.A : 0, 0, 0, 0, 0, 0, 0));
        }
        Thread.Sleep(20);
        var st = p.Stats(); p.Stop();
        double[] l; lock (lags) l = lags.OrderBy(v => v).ToArray();
        double p50 = l[l.Length / 2], p95 = l[(int)(l.Length * 0.95)];
        Console.WriteLine($"        loop: {st.PollHzActual:F0} Hz, change->sent p50 {p50:F2} ms, p95 {p95:F2} ms, pipeline p99 {st.DetectToSendP99Us:F0} us");
        True(l.Length >= 55, $"saw {l.Length} of 60 changes");
        True(p50 < 1.5, $"p50 {p50:F2} ms");
    }),
    ("H.264 test stream splits into access units and packetizes losslessly", () =>
    {
        var stream = TestPattern.Stream(20, 15, 30, 10);
        var aus = H264Packetizer.AccessUnits(stream);
        Eq(30, aus.Count);
        var pk = H264Packetizer.Packetize(aus).ToList();
        True(pk.Any(p => (p.Payload[0] & 0x1F) == 28), "large IDR split into FU-A");
        var d = new H264Depacketizer(); var got = new List<AccessUnit>();
        foreach (var p in pk) got.AddRange(d.Push(p.Seq, p.Timestamp, p.Marker, p.Payload, 0));
        Eq(30, got.Count); Eq(3, got.Count(a => a.IsKeyframe));
        var rebuilt = got.SelectMany(a => a.Data.ToArray()).ToArray();
        True(rebuilt.SequenceEqual(stream), "frame assembler rebuilds the exact Annex-B stream");
    }),
    ("Video: real FFmpeg decode through the frame assembler (needs FFmpeg)", () =>
    {
        if (!FfmpegAvailable(out var why)) { Console.WriteLine("        skipped: " + why); return; }
        var stream = TestPattern.Stream(20, 15, 60, 20); // 320x240, 60 frames, IDR every 20
        var d = new H264Depacketizer();
        using var dec = new H264Decoder(preferHardware: false);
        int pictures = 0; DecodedPicture? last = null; var lumas = new HashSet<byte>();
        unsafe
        {
            foreach (var p in H264Packetizer.Packetize(H264Packetizer.AccessUnits(stream)))
                foreach (var au in d.Push(p.Seq, p.Timestamp, p.Marker, p.Payload, 0))
                {
                    var pic = dec.Decode(au);
                    if (pic is null) continue;
                    pictures++; lumas.Add(pic.Plane(0)[0]);
                    last?.Dispose(); last = pic;
                }
        }
        Console.WriteLine($"        {H264Decoder.LibVersion}, decoder {dec.Backend}, {pictures} pictures, {last?.Width}x{last?.Height} {last?.Layout}");
        Eq(60, pictures);
        True(last!.Width == 320 && last.Height == 240 && last.Layout == PlaneLayout.Yuv420p, "picture size/format");
        Eq(3, lumas.Count); // one solid colour per keyframe
        last.Dispose();
    }),
    ("Video: Vexel's VideoToolbox decoder gives the same pictures as FFmpeg, none held back (macOS)", () =>
    {
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("        skipped: macOS only"); return; }
        var stream = TestPattern.Stream(20, 15, 60, 20);
        using var vt = new VideoToolboxDecoder();
        var d = new H264Depacketizer();
        int pictures = 0; var lumas = new HashSet<byte>(); int w = 0, h = 0;
        unsafe
        {
            foreach (var p in H264Packetizer.Packetize(H264Packetizer.AccessUnits(stream)))
                foreach (var au in d.Push(p.Seq, p.Timestamp, p.Marker, p.Payload, 0))
                {
                    using var pic = vt.Decode(au);
                    if (pic is null) continue;
                    pictures++; lumas.Add(pic.Plane(0)[pic.Pitch(0) * 100 + 100]); w = pic.Width; h = pic.Height;
                    True(pic.Layout == PlaneLayout.Nv12, "NV12");
                }
        }
        Console.WriteLine($"        {vt.Backend}: {pictures} pictures {w}x{h}, held back {vt.HeldBack}, errors {vt.Errors}");
        Eq(60, pictures);
        Eq(0L, vt.HeldBack);
        True(w == 320 && h == 240, "picture size");
        Eq(3, lumas.Count); // one solid colour per keyframe, as with FFmpeg
    }),
    ("Video: lost P-frame packet freezes until the next keyframe, then recovers (needs FFmpeg)", () =>
    {
        if (!FfmpegAvailable(out var why)) { Console.WriteLine("        skipped: " + why); return; }
        var stream = TestPattern.Stream(20, 15, 40, 20);
        var pk = H264Packetizer.Packetize(H264Packetizer.AccessUnits(stream)).ToList();
        var lostTs = pk.Where(p => (p.Payload[0] & 0x1F) == 1).Skip(4).First().Timestamp; // a P frame in the first GOP
        var d = new H264Depacketizer { MaxWaitMs = 50 };
        using var dec = new H264Decoder(preferHardware: false);
        int shown = 0; double t = 0;
        foreach (var p in pk)
        {
            t += 1;
            if (p.Timestamp == lostTs) continue;
            foreach (var au in d.Push(p.Seq, p.Timestamp, p.Marker, p.Payload, t)) { using var pic = dec.Decode(au); if (pic != null) shown++; }
            foreach (var au in d.Tick(t)) { using var pic = dec.Decode(au); if (pic != null) shown++; }
        }
        Console.WriteLine($"        frames lost {d.FramesLost}, held back until keyframe {d.FramesHeldBack}, shown {shown}/40");
        Eq(1L, d.FramesLost);
        True(d.FramesHeldBack > 0 && shown == 40 - 1 - d.FramesHeldBack, "only frames before the next keyframe are withheld");
        False(d.PictureBroken, "picture restored by the keyframe");
    }),
    ("Video: window presents decoded frames immediately (needs FFmpeg + SDL2)", () =>
    {
        if (!FfmpegAvailable(out var why)) { Console.WriteLine("        skipped: " + why); return; }
        if (Environment.GetEnvironmentVariable("XSTREAM_SDL2") is null && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) { Console.WriteLine("        skipped: no SDL2"); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew(); Func<double> clock = () => sw.Elapsed.TotalMilliseconds;
        var mailbox = new FrameMailbox();
        using var win = new VideoWindow(mailbox, clock, new VideoWindowOptions { Fullscreen = false, Width = 320, Height = 240,
            Renderer = Environment.GetEnvironmentVariable("SDL_VIDEODRIVER") == "dummy" ? "software" : null });
        var buffer = new PlayoutBuffer<AccessUnit> { TargetDelayMs = 0 };
        using var pipe = new VideoPipeline(buffer, new H264Decoder(false), mailbox, clock);
        pipe.Start();
        var d = new H264Depacketizer();
        var pk = H264Packetizer.Packetize(H264Packetizer.AccessUnits(TestPattern.Stream(20, 15, 120, 30))).ToList();
        int idx = 0; double nextFrame = clock();
        while (idx < pk.Count || clock() < nextFrame + 100)
        {
            if (idx < pk.Count && clock() >= nextFrame)
            {
                uint ts = pk[idx].Timestamp;
                while (idx < pk.Count && pk[idx].Timestamp == ts)
                {
                    foreach (var au in d.Push(pk[idx].Seq, pk[idx].Timestamp, pk[idx].Marker, pk[idx].Payload, clock())) { buffer.Enqueue(au, au.RtpTimestamp, clock()); pipe.Notify(); }
                    idx++;
                }
                nextFrame += 1000.0 / 60;
            }
            win.PumpOnce(1);
        }
        var st = win.TakeStats();
        Console.WriteLine($"        renderer {st.Renderer}: presented {st.Presented}/120, dropped-for-latency {st.Dropped}, network->screen p50 {st.ArrivalToPresentP50Ms:F2} ms p99 {st.ArrivalToPresentP99Ms:F2} ms, decode p50 {pipe.DecodeP50Ms:F2} ms");
        True(st.Presented + st.Dropped >= 118, "every frame shown or superseded by a newer one");
        True(st.ArrivalToPresentP50Ms < 8, $"network->screen p50 {st.ArrivalToPresentP50Ms:F2} ms");
    }),
    ("A total outage isn't counted as packet loss (no bitrate cut when it ends)", () =>
    {
        var m = new RtpReceiveMonitor();
        ushort seq = 0; double t = 0;
        for (int i = 0; i < 300; i++) m.OnPacket(seq++, (uint)(i * 30), true, 1200, t += 0.35);
        m.TakeSample(t);
        seq += 2000; t += 3000;                       // 3 s of nothing, then the stream carries on
        for (int i = 0; i < 300; i++) m.OnPacket(seq++, (uint)(90_000 + i * 30), true, 1200, t += 0.35);
        var s = m.TakeSample(t);
        Eq(0.0, s.LossFraction); Eq(1L, m.Outages); Eq(2000L, m.OutagePackets);
        // Real loss still counts: a few missing packets in a steady stream.
        m.OnPacket(seq, 200_000, true, 1200, t + 0.35); m.OnPacket((ushort)(seq + 3), 200_030, true, 1200, t + 0.7);
        m.OnPacket((ushort)(seq + 4), 200_060, true, 1200, t + 1.0); m.OnPacket((ushort)(seq + 5), 200_090, true, 1200, t + 1.3);
        True(m.TakeSample(t + 2).LossFraction > 0, "ordinary loss counted");
    }),
    ("Playout buffer keeps decode order when frames are released together", () =>
    {
        // A resend completes a stalled frame and the frames behind it come out of one drain, all late (due = now).
        // The old priority queue shuffled equal due times: 1,4,3,2.
        var b = new PlayoutBuffer<int>();
        b.Enqueue(0, 0, 100); b.TryDequeue(1e9, out _);
        double now = 100 + 16.7 * 4 + 40;
        for (int f = 1; f <= 6; f++) b.Enqueue(f, (uint)(f * 1500), now);
        var order = new List<int>(); while (b.TryDequeue(now, out var x)) order.Add(x);
        Eq("1,2,3,4,5,6", string.Join(",", order));
        // Due times never go backwards, even when the target delay drops between frames.
        var c = new PlayoutBuffer<int> { TargetDelayMs = 30 };
        c.Enqueue(1, 0, 0);
        c.TargetDelayMs = 0;
        c.Enqueue(2, 1500, 1);           // would be due at 16.7, before frame 1 (30)
        False(c.TryDequeue(29, out _), "frame 1 not due yet");
        True(c.TryDequeue(30, out var a) && a == 1, "frame 1 first");
        True(c.TryDequeue(30, out var d2) && d2 == 2, "then frame 2");
        Near(double.NaN, c.NextDueMs, 0);
    }),
    ("Lost non-reference frame is skipped without freezing the picture", () =>
    {
        var d = new H264Depacketizer { MaxWaitMs = 30 };
        d.Push(1, 0, true, new byte[] { 0x65, 1 }, 0);                         // IDR
        d.Push(2, 1500, false, new byte[] { 0x01, 1 }, 17);                    // non-ref P (NRI 0), first half
        // seq 3 (its second half, with the marker) lost; next frame starts at 4 but 3 is missing so its start is unsure
        d.Push(4, 3000, true, new byte[] { 0x41, 3 }, 34);
        Eq(0, d.Tick(100).Count);
        Eq(1L, d.FramesLost); // can't be sure 4 starts a frame (the gap could hold its first packet): normal loss

        var e = new H264Depacketizer { MaxWaitMs = 30 };
        e.Push(1, 0, true, new byte[] { 0x65, 1 }, 0);
        e.Push(2, 1500, false, new byte[] { 0x01, 1 }, 17);                    // non-ref, packet 1 of 3
        e.Push(4, 1500, true, new byte[] { 0x01, 3 }, 18);                     // packet 3 (marker): 3 lost
        var after = e.Push(5, 3000, true, new byte[] { 0x41, 4 }, 34);         // reference P frame
        Eq(0, after.Count);
        var outp = e.Tick(60);
        Eq(1, outp.Count); Eq(3000u, outp[0].RtpTimestamp);
        Eq(0L, e.FramesLost); Eq(1L, e.FramesSkipped);
        False(e.PictureBroken, "picture carries on"); False(e.TakeDecoderError(), "no keyframe needed");

        // Same, but the frame is a reference one (NRI 2): freeze and ask for a keyframe as before.
        var f = new H264Depacketizer { MaxWaitMs = 30 };
        f.Push(1, 0, true, new byte[] { 0x65, 1 }, 0);
        f.Push(2, 1500, false, new byte[] { 0x41, 1 }, 17);
        f.Push(4, 1500, true, new byte[] { 0x41, 3 }, 18);
        f.Push(5, 3000, true, new byte[] { 0x41, 4 }, 34);
        Eq(0, f.Tick(60).Count);
        Eq(1L, f.FramesLost); True(f.TakeDecoderError(), "keyframe needed");

        // Only an AUD (NRI 0, not a slice) held: can't tell, so it's a loss.
        var g = new H264Depacketizer { MaxWaitMs = 30 };
        g.Push(1, 0, true, new byte[] { 0x65, 1 }, 0);
        g.Push(2, 1500, false, new byte[] { 0x09, 0xF0 }, 17);                 // AUD
        g.Push(4, 1500, true, new byte[] { 0x7C, 0x41, 3 }, 18);              // FU-A end of a reference slice
        g.Push(5, 3000, true, new byte[] { 0x41, 4 }, 34);
        g.Tick(60);
        Eq(1L, g.FramesLost);
    }),
    ("Frame assembler reports what it gave up on; NACKs for it stop", () =>
    {
        var d = new H264Depacketizer { MaxWaitMs = 30 };
        var n = new NackGenerator();
        void Push(ushort seq, uint ts, bool m, byte[] pl, double t) { n.OnPacket(seq, t); d.Push(seq, ts, m, pl, t); }
        Push(1, 0, true, new byte[] { 0x65, 1 }, 0);
        Push(3, 1500, true, new byte[] { 0x41, 2 }, 17);   // 2 lost
        Push(6, 3000, true, new byte[] { 0x41, 3 }, 34);   // 4, 5 lost (frame 3000 starts at 4)
        Eq("2,4,5", string.Join(",", n.Poll(40, 4)));
        False(d.TakeAbandoned(out _), "nothing given up yet");
        d.Tick(60);
        True(d.TakeAbandoned(out var through), "gave up");
        True(through >= 2, $"through {through}");
        n.ForgetThrough(through);
        True(n.Abandoned >= 1, "dropped");
        var still = n.Poll(80, 4);
        False(still.Contains((ushort)2), "no more asks for seq 2");
    }),
    ("NACK retries follow the measured round trip", () =>
    {
        var n = new NackGenerator();
        n.OnPacket(10, 0); n.OnPacket(12, 1);
        Eq(1, n.Poll(5, 4).Count);               // first ask
        Eq(0, n.Poll(10, 4).Count);              // 5 ms later: too soon (retry after 8 ms on a 4 ms link)
        Eq(1, n.Poll(13.5, 4).Count);            // 8.5 ms later: asked again (was 20 ms)
        n.OnPacket(11, 15);                      // the resend answers one of the two asks: not an RTT sample
        True(double.IsNaN(n.RttEstimateMs), "no sample after a retry");
        n.OnPacket(14, 20);                      // 13 missing
        n.Poll(25, 4);
        n.OnPacket(13, 31);                      // one ask, answered 6 ms later
        Near(6, n.RttEstimateMs, 0.01);
        Near(4 + 3 * (6 * 1.5 + 2), n.GiveUpAfterMs(4), 0.01); // resend RTT is the longer one: retries follow it
    }),
    ("Frame assembler survives sequence wrap-around, reordering and duplicates", () =>
    {
        var stream = TestPattern.Stream(20, 15, 90, 30);
        var pk = H264Packetizer.Packetize(H264Packetizer.AccessUnits(stream)).ToList();
        var rng = new Random(3);
        var order = new List<int>();
        for (int i = 0; i < pk.Count; i++) order.Add(i);
        for (int i = 0; i + 1 < order.Count; i += 3) if (rng.Next(2) == 0) (order[i], order[i + 1]) = (order[i + 1], order[i]); // swaps
        var d = new H264Depacketizer(); var got = new List<AccessUnit>();
        double t = 0;
        foreach (int i in order)
        {
            var p = pk[i];
            ushort seq = (ushort)(p.Seq + 65_000); // wraps mid-stream
            got.AddRange(d.Push(seq, p.Timestamp, p.Marker, p.Payload, t));
            if (rng.Next(10) == 0) got.AddRange(d.Push(seq, p.Timestamp, p.Marker, p.Payload, t)); // duplicate
            t += 0.3;
        }
        Eq(90, got.Count);
        True(got.SelectMany(a => a.Data.ToArray()).SequenceEqual(stream), "exact Annex-B stream");
        Eq(0L, d.FramesLost);
    }),
    ("Receive path asks for a keyframe the moment a frame is lost, not on the next tick", () =>
    {
        foreach (bool fast in new[] { true, false })
        {
            var engine = new StabilityEngine();
            var r = new VideoReceiver(engine, fast ? new RecoveryOptions() : RecoveryOptions.Legacy) { NackEnabled = true };
            int keyframes = 0; double askedAt = double.NaN; double now = 0;
            r.KeyframeNeeded = () => { keyframes++; askedAt = now; };
            r.Sample(0, 4); // 4 ms round trip: sets how long frames wait for resends
            r.OnPacket(1, 1, 0, true, new byte[] { 0x65, 1 }, now);
            r.OnPacket(1, 3, 1500, true, new byte[] { 0x41, 2 }, now = 17); // seq 2 lost for good
            for (now = 20; now <= 300; now += 10) r.FastTick(now);
            // Lost at 17 + 46 ms (4 tries 8 ms apart + margin), asked on that 10 ms tick. 0.4.0: 60+ ms wait, then up to 100 ms more.
            if (fast) { Eq(1, keyframes); True(askedAt <= 17 + 46 + 10, $"asked at {askedAt} ms"); }
            else Eq(0, keyframes); // legacy: left to the engine's 100 ms tick
            var (_, _, lost) = r.Sample(300, 4);
            True(lost, "the tick hears about it too");
        }
    }),
    ("A keyframe repaired by a resend counts as received", () =>
    {
        var engine = new StabilityEngine();
        var r = new VideoReceiver(engine) { NackEnabled = true };
        r.OnPacket(1, 1, 0, false, new byte[] { 0x7C, 0x85, 1 }, 0);  // IDR FU-A start
        r.OnPacket(1, 3, 0, true, new byte[] { 0x7C, 0x45, 3 }, 1);   // end; 2 missing
        r.OnPacket(1, 2, 0, false, new byte[] { 0x7C, 0x05, 2 }, 9);  // resend
        var (s, _, _) = r.Sample(100, 4);
        True(s.KeyframeSeen, "keyframe seen although the monitor saw it arrive with a gap");
    }),
    ("Session logger counts freezes and round-trips through CSV", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"xs-test-{Guid.NewGuid():N}.csv");
        var snap = new StabilitySnapshot(0, 10_000, 12_000, 0, 2, 0, 25, 10, LinkState.Connected, CapCompliance.Honoured, 0, 0, "probe", "Stable");
        SessionSummary live;
        using (var log = new SessionLogger(path, "Balanced") { NackNegotiated = true })
        {
            long frames = 0;
            for (int i = 1; i <= 600; i++) // 60 s
            {
                bool frozen = (i >= 100 && i < 104) || (i >= 300 && i < 302) || i == 400; // 400 ms, 200 ms, 100 ms
                if (!frozen) frames += 6;
                log.Record(S(i * 100, kbps: 10_000), snap with { TimeMs = i * 100, Reconnects = i > 500 ? 1 : 0 },
                    new PictureTick(frames, 1, false, frozen ? 150 : 16, 3, 2));
            }
            live = log.Summary;
        }
        var re = SessionSummary.FromCsv(path);
        foreach (var s in new[] { live, re })
        {
            Eq(1, s.Freezes);                 // only the 400 ms one crosses 250 ms
            Near(0.7, s.FrozenSec, 0.001);
            Near(1.0, s.Minutes, 0.01);
            Eq(1, s.Reconnects);
            Near(10, s.AvgMbps, 0.01);
        }
        True(re.NackNegotiated && re.Profile == "Balanced", "meta kept");
        File.Delete(path); File.Delete(Path.ChangeExtension(path, ".summary.json"));
    }),
    ("Settings round-trip through JSON and map to engine options", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"xs-settings-{Guid.NewGuid():N}.json");
        try
        {
            var s = new AppSettings { Profile = StreamProfile.Smooth, MaxMbps = 12, StartMbps = 30, VSync = true, LastServerId = "F400" };
            s.Save(path);
            var back = AppSettings.Load(path);
            Eq(s with { SettingsVersion = AppSettings.CurrentVersion }, back); // loading stamps the defaults version
            True(File.ReadAllText(path).Contains("\"Smooth\""), "profile stored by name");
            var o = back.ToStabilityOptions();
            Eq(StreamProfile.Smooth, o.Profile);
            Eq(12_000.0, o.MaxKbps);
            Eq(12_000.0, o.StartKbps); // start never above max
            File.WriteAllText(path, "{ not json");
            Eq(new AppSettings(), AppSettings.Load(path)); // corrupt file -> defaults
        }
        finally { File.Delete(path); }
    }),
    ("Player events survive the launcher's stdout protocol", () =>
    {
        var stats = new LiveStats { Mbps = 14.5, PingMs = 9, Verdict = "Healthy, steady", Link = "Connected" };
        var line = new PlayerEvent { Type = "stats", Stats = stats }.ToLine();
        True(line.StartsWith(PlayerEvent.Prefix), "prefixed");
        False(line.Contains('\n'), "one line");
        var e = PlayerEvent.TryParse(line)!;
        Eq("stats", e.Type);
        Eq(stats, e.Stats);
        var st = PlayerEvent.TryParse(new PlayerEvent { Type = "stage", Stage = PlayerStage.WakingConsole, Message = "a\nb" }.ToLine())!;
        Eq(PlayerStage.WakingConsole, st.Stage);
        Eq("a\nb", st.Message);
        var sum = PlayerEvent.TryParse(new PlayerEvent { Type = "summary", Summary = new SessionSummary { Freezes = 3, VerdictPct = new() { ["x"] = 50 } } }.ToLine())!;
        Eq(3, sum.Summary!.Freezes);
        Eq(50.0, sum.Summary.VerdictPct["x"]);
        Eq(null, PlayerEvent.TryParse("some library log line"));
        Eq(null, PlayerEvent.TryParse(PlayerEvent.Prefix + "{broken"));
    }),
    ("Clarity boost: SIMD matches the reference, no halos, flat areas untouched, fast at 1080p", () =>
    {
        unsafe
        {
            int w = 1920, h = 1080;
            var src = new byte[w * h]; var dst = new byte[w * h];
            var rnd = new Random(7);
            rnd.NextBytes(src);
            fixed (byte* s = src) fixed (byte* d = dst)
            {
                foreach (int level in new[] { 1, 2, 3 })
                {
                    Sharpen.Apply(s, w, d, w, w, h, level);
                    int k = Sharpen.Strength(level);
                    for (int i = 0; i < 20_000; i++)
                    {
                        int x = 1 + rnd.Next(w - 2), y = 1 + rnd.Next(h - 2), o = y * w + x;
                        Eq(Sharpen.Scalar(src[o], src[o - 1], src[o + 1], src[o - w], src[o + w], k), dst[o]);
                    }
                }
                // Flat grey stays exactly flat.
                Array.Fill(src, (byte)128);
                Sharpen.Apply(s, w, d, w, w, h, 3);
                True(dst.All(v => v == 128), "flat area unchanged");
                // A soft vertical edge (100 -> 140 -> 180) gets steeper but stays within 100..180.
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) src[y * w + x] = (byte)(x < 900 ? 100 : x == 900 ? 140 : 180);
                Sharpen.Apply(s, w, d, w, w, h, 3);
                int mid = 500 * w;
                Eq((byte)100, dst[mid + 899]); Eq((byte)180, dst[mid + 901]); // no overshoot either side
                Eq((byte)140, dst[mid + 900]); // midpoint of a symmetric ramp has no Laplacian
                src[500 * w + 900] = 120;      // asymmetric ramp: centre pulled toward the nearer side
                Sharpen.Apply(s, w, d, w, w, h, 3);
                True(dst[mid + 900] < 120 && dst[mid + 900] >= 100, $"edge steeper: {dst[mid + 900]}");

                rnd.NextBytes(src);
                Sharpen.Apply(s, w, d, w, w, h, 2); // warm up
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 20; i++) Sharpen.Apply(s, w, d, w, w, h, 2);
                double ms = sw.Elapsed.TotalMilliseconds / 20;
                Console.WriteLine($"        clarity boost 1080p: {ms:F2} ms/frame");
                True(ms < 6, $"fast enough: {ms:F2} ms");
            }
        }
    }),
    ("Picture scaling: Fit letterboxes, Fill crops the centre to the screen's shape", () =>
    {
        var m = typeof(VideoWindow).GetMethod("FitRect", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        (int x, int y, int w, int h) R(int w, int h, int bw, int bh)
        {
            var r = m.Invoke(null, new object[] { w, h, bw, bh })!;
            int F(string n) => (int)r.GetType().GetField(n)!.GetValue(r)!;
            return (F("X"), F("Y"), F("W"), F("H"));
        }
        // 1440p stream on a 14" MacBook Pro panel (3024x1964): Fit = full width, bars top and bottom.
        Eq((0, 131, 3024, 1701), R(2560, 1440, 3024, 1964));
        // Fill: the source crop is the screen's shape, centred: 2217 of 2560 columns, ~6.7% off each side.
        Eq((171, 0, 2217, 1440), R(3024, 1964, 2560, 1440));
        // 16:9 stream on a 16:9 screen (ROG Ally X): no bars, no crop.
        Eq((0, 0, 1920, 1080), R(1920, 1080, 1920, 1080));
    }),
    ("Cloud region: nearest wins, but Microsoft's default is kept when it's nearly as close", () =>
    {
        CloudRegion R(string n, bool def = false) => new(n, "https://" + n + ".example", def);
        var (pick, _) = CloudRegions.Choose([(R("UKSOUTH", def: true), 21), (R("WESTEUROPE"), 22), (R("EASTUS"), 104)]);
        Eq("UKSOUTH", pick!.Name);                                      // default within 8 ms of the best: keep it
        (pick, _) = CloudRegions.Choose([(R("EASTUS", def: true), 104), (R("UKSOUTH"), 21)]);
        Eq("UKSOUTH", pick!.Name);                                      // default far away: nearest wins
        (pick, _) = CloudRegions.Choose([(R("A", def: true), double.PositiveInfinity), (R("B"), 40)]);
        Eq("B", pick!.Name);                                            // default unreachable
        (pick, _) = CloudRegions.Choose([(R("A", def: true), double.PositiveInfinity), (R("B"), double.PositiveInfinity)]);
        Eq("A", pick!.Name);                                            // nothing measurable: fall back to the default
    }),
    ("Audio: a lost Opus packet is rebuilt from the next one's FEC, longer gaps are concealed, late ones dropped", () =>
    {
        var enc = new Concentus.Structs.OpusEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO)
            { Bitrate = 128000, UseInbandFEC = true, PacketLossPercent = 10 };
        // 30 packets (0.6 s) of a 440 Hz stereo tone.
        var packets = new List<byte[]>();
        var pcm = new short[960 * 2]; var buf = new byte[1500];
        for (int f = 0; f < 30; f++)
        {
            for (int i = 0; i < 960; i++) { short v = (short)(Math.Sin(2 * Math.PI * 440 * (f * 960 + i) / 48000.0) * 9000); pcm[2 * i] = v; pcm[2 * i + 1] = v; }
            int n = enc.Encode(pcm, 0, 960, buf, 0, buf.Length);
            packets.Add(buf.AsSpan(0, n).ToArray());
        }
        // Lose packet 10 (one), and 20-22 (three in a row); deliver 5 again late.
        var dec = new XStream.Core.Audio.OpusLossDecoder();
        long samples = 0; double energyAt10 = 0; int chunk = 0;
        for (int f = 0; f < 30; f++)
        {
            if (f == 10 || f is >= 20 and <= 22) continue;
            dec.Push(packets[f], f, (p, n) =>
            {
                samples += n; chunk++;
                if (f == 11 && chunk > 0 && energyAt10 == 0) for (int i = 0; i < n; i++) energyAt10 += Math.Abs((int)p[2 * i]);
            });
            if (f == 15) dec.Push(packets[5], 5, (_, _) => throw new Exception("late packet should be dropped"));
        }
        Eq(26L, dec.Decoded);
        Eq(2L, dec.Recovered);        // 10 (from 11's FEC) and 22 (from 23's FEC)
        Eq(2L, dec.Concealed);        // 20 and 21
        Eq(1L, dec.Dropped);
        Eq(30L * 960, samples);       // no hole: every 20 ms of the 0.6 s is filled
        True(energyAt10 / 960 > 2000, $"rebuilt packet carries the tone (mean level {energyAt10 / 960:F0})");
    }),
    ("Cloud search: forgiving matching, every word must match, best matches first, genre filter and sort", () =>
    {
        CloudGame G(string name, string pub = "", string[]? genres = null, int year = 2020) =>
            new(name.Replace(" ", "").ToUpperInvariant(), "p", name, pub, null, null, null, true, false, genres, new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var lib = new[]
        {
            G("Assassin's Creed® IV Black Flag", "Ubisoft", ["Action & adventure"], 2013),
            G("Halo Infinite", "Xbox Game Studios", ["Shooter"], 2021),
            G("Halo Wars 2", "Xbox Game Studios", ["Strategy"], 2017),
            G("Pokémon Something", "Nintendo-ish", ["Family"], 2022),
            G("Mythical Halo Tale", "Indie", ["Action & adventure"], 2024),
            G("Forza Horizon 5", "Xbox Game Studios", ["Racing & flying"], 2021),
        };
        Eq("Assassin's Creed® IV Black Flag", CloudSearch.Filter(lib, "assassins").Single().Name);   // apostrophe and ® ignored
        Eq("Pokémon Something", CloudSearch.Filter(lib, "POKEMON").Single().Name);                    // accents and case ignored
        var halo = CloudSearch.Filter(lib, "halo").Select(g => g.Name).ToList();
        Eq("Halo Infinite", halo[0]); Eq("Halo Wars 2", halo[1]); Eq("Mythical Halo Tale", halo[2]);   // title-starts-with first
        Eq(1, CloudSearch.Filter(lib, "halo wars").Count);                                            // every word must match
        Eq(3, CloudSearch.Filter(lib, "xbox game studios").Count);                                    // publisher matches
        Eq("Forza Horizon 5", CloudSearch.Filter(lib, "racing").Single().Name);                       // genre matches
        Eq(2, CloudSearch.Filter(lib, "", genre: "Action & adventure").Count);                        // genre filter
        Eq("Mythical Halo Tale", CloudSearch.Filter(lib, "", sort: CloudSort.Newest)[0].Name);        // newest first
        Eq(0, CloudSearch.Filter(lib, "zzzz").Count);
        Eq("Action & adventure", CloudSearch.Genres(lib)[0]);                                         // most common genre first
    }),
    ("Updates: only a manifest signed by the release key, for a newer version, with a matching file", () =>
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        string pub = key.ExportSubjectPublicKeyInfoPem();
        var file = Path.GetTempFileName();
        File.WriteAllBytes(file, [1, 2, 3, 4, 5]);
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        string Sign(string v, string url, long size) => Convert.ToBase64String(key.SignData(
            XStream.Core.Update.UpdateManifest.SignedPayload(v, url, sha, size), System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence));
        var m = new XStream.Core.Update.UpdateManifest("1.3.0", "https://example.com/XStream.exe", sha, 5, null, Sign("1.3.0", "https://example.com/XStream.exe", 5));
        True(m.SignatureValid(pub), "genuine manifest verifies");
        True(m.IsNewerThan(new Version(1, 2, 9)) && !m.IsNewerThan(new Version(1, 3, 0)), "version compare");
        True(m.Matches(file), "file matches hash and size");
        False((m with { Version = "9.0.0" }).SignatureValid(pub), "changed version rejected");
        False((m with { Url = "https://evil.example/XStream.exe" }).SignatureValid(pub), "changed URL rejected");
        False(m.SignatureValid(), "not signed by XStream's real release key");
        File.WriteAllBytes(file, [1, 2, 3, 4, 6]);
        False(m.Matches(file), "altered file rejected");
        File.Delete(file);
    }),
    ("Watchdog: closes a frozen stream once, never a busy-but-alive one", () =>
    {
        var t0 = new DateTime(2026, 1, 1);
        var w = new StreamWatchdog();
        w.Arm(t0);
        // Connecting for a minute: heartbeats keep coming (main loop not running yet, so stuck = 0).
        for (int s = 2; s <= 60; s += 2) { Eq<string?>(null, w.Heartbeat(t0.AddSeconds(s), 0)); Eq<string?>(null, w.Check(t0.AddSeconds(s))); }
        // Main loop stuck, reported by the heartbeat: fires at 15 s, once.
        Eq<string?>(null, w.Heartbeat(t0.AddSeconds(62), 14));
        True(w.Heartbeat(t0.AddSeconds(64), 16) is { } r && r.Contains("froze"), "stuck window fires");
        Eq<string?>(null, w.Heartbeat(t0.AddSeconds(66), 18));
        // A new stream: whole process silent for over 20 s fires.
        w.Arm(t0.AddSeconds(100));
        Eq<string?>(null, w.Check(t0.AddSeconds(119)));
        True(w.Check(t0.AddSeconds(121)) is { } r2 && r2.Contains("stopped responding"), "silence fires");
    }),
    ("Connection alerts: need two bad seconds, never nag, and say when it's back to normal", () =>
    {
        var h = new StreamHealth();
        LiveStats Ok(double t) => new() { TimeS = t, Fps = 60, PingMs = 12, JitterMs = 2, LossPct = 0, CapMbps = 30, NetToScreenP50Ms = 20 };
        LiveStats Lossy(double t) => Ok(t) with { LossPct = 6 };
        Eq<HealthToast?>(null, h.Update(Lossy(2)));          // first seconds: settling in, ignored
        for (int t = 5; t < 10; t++) Eq<HealthToast?>(null, h.Update(Ok(t)));
        Eq<HealthToast?>(null, h.Update(Lossy(10)));         // one bad second: normal on Wi-Fi
        var warn = h.Update(Lossy(11));
        True(warn is { Kind: ToastKind.Warn, Category: "network" } && warn.Text.Contains("6% of packets lost"), "network toast: " + warn?.Text);
        for (int t = 12; t < 20; t++) Eq<HealthToast?>(null, h.Update(Lossy(t))); // same problem: not repeated
        HealthToast? back = null;
        for (int t = 20; t < 30 && back is null; t++) back = h.Update(Ok(t));
        True(back is { Kind: ToastKind.Good, Text: "Connection back to normal" }, "recovery toast");
        Eq<HealthToast?>(null, h.Update(Ok(31)));            // only once

        var h2 = new StreamHealth();
        for (int t = 5; t < 8; t++) h2.Update(Ok(t));
        HealthToast? fps = null;
        for (int t = 8; t < 12 && fps is null; t++) fps = h2.Update(Ok(t) with { Fps = 40 }, clarity: 2);
        True(fps is { Category: "fps" } && fps.Text.Contains("Clarity boost Off"), "fps toast with clarity hint: " + fps?.Text);

        var h3 = new StreamHealth();
        for (int t = 5; t < 8; t++) h3.Update(Ok(t));
        var q = h3.Update(Ok(8) with { CapMbps = 12 });
        True(q is { Category: "quality" } && q.Text.Contains("12 Mbps"), "quality toast: " + q?.Text);
        var r = h3.Update(Ok(9), reconnecting: true);         // a dropped connection always gets through
        True(r is { Kind: ToastKind.Bad, Category: "reconnect" }, "reconnect toast");
    }),
    ("Microphone: 20 ms voice frames encode small and decode back to the same signal", () =>
    {
        var enc = new Concentus.Structs.OpusEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP) { Bitrate = 32000 };
        var dec = new Concentus.Structs.OpusDecoder(48000, 1);
        var pcm = new short[960]; var outp = new short[960]; var buf = new byte[1275];
        double err = 0, energy = 0;
        for (int f = 0; f < 50; f++) // one second of a 440 Hz tone
        {
            for (int i = 0; i < 960; i++) pcm[i] = (short)(Math.Sin(2 * Math.PI * 440 * (f * 960 + i) / 48000.0) * 8000);
            int n = enc.Encode(pcm, 0, 960, buf, 0, buf.Length);
            True(n is > 0 and < 200, $"frame size {n} bytes");
            int got = dec.Decode(buf, 0, n, outp, 0, 960, false);
            Eq(960, got);
            if (f >= 10) for (int i = 0; i < 960; i++) { energy += pcm[i] * (double)pcm[i]; }
            if (f >= 10) for (int i = 0; i < 960; i++) { err += Math.Abs(outp[i]); }
        }
        True(err > 0 && energy > 0, "decoded audio is not silent");
    }),
    ("Library: console games only, newest first, wide art preferred over box art", () =>
    {
        var j = JsonNode.Parse("""
        {"titles":[
          {"titleId":"1","name":"Old Game","type":"Game","devices":["XboxOne"],"titleHistory":{"lastTimePlayed":"2025-01-01T00:00:00Z"},
           "images":[{"type":"BoxArt","url":"http://store-images.s-microsoft.com/image/apps.box"}]},
          {"titleId":"2","name":"New Game","type":"Game","devices":["XboxSeries","PC"],"titleHistory":{"lastTimePlayed":"2026-11-01T00:00:00Z"},
           "images":[{"type":"BoxArt","url":"https://x/box"},{"type":"SuperHeroArt","url":"https://store-images.s-microsoft.com/image/apps.hero"},{"type":"Logo","url":"https://x/logo"}]},
          {"titleId":"3","name":"Netflix","type":"App","devices":["XboxSeries"],"images":[{"type":"SuperHeroArt","url":"https://x/n"}]},
          {"titleId":"4","name":"PC Only","type":"Game","devices":["PC"],"images":[{"type":"SuperHeroArt","url":"https://x/p"}]},
          {"titleId":"5","name":"No Art","type":"Game","devices":["XboxOne"],"images":[]}
        ]}
        """)!;
        var games = XboxLibrary.Parse(j);
        Eq(2, games.Count);
        Eq("New Game", games[0].Name);
        Eq("https://store-images.s-microsoft.com/image/apps.hero", games[0].HeroUrl);
        Eq("https://x/logo", games[0].LogoUrl);
        Eq("https://store-images.s-microsoft.com/image/apps.box", games[1].HeroUrl); // http upgraded, box art as last resort
        Eq("https://store-images.s-microsoft.com/image/apps.hero?w=1920", XboxLibrary.Sized(games[0].HeroUrl, 1920));
        Eq("https://x/box", XboxLibrary.Sized("https://x/box", 1920));
    }),
};
tests.Add(("Audio catch-up: quiet frames detected, loud ones shortened smoothly by 4%", () =>
{
    float g = 1f / 32768;
    var quiet = new float[1920]; for (int i = 0; i < quiet.Length; i++) quiet[i] = (float)(Math.Sin(i * 0.1) * 50 * g);
    True(XStream.Core.Audio.AudioCatchUp.IsQuiet(quiet, g), "near-silence is quiet");
    var tone = new float[1920]; for (int i = 0; i < 960; i++) tone[2 * i] = tone[2 * i + 1] = (float)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 8000 * g);
    False(XStream.Core.Audio.AudioCatchUp.IsQuiet(tone, g), "a tone is not quiet");
    var copy = (float[])tone.Clone();
    int m = XStream.Core.Audio.AudioCatchUp.Shorten(copy, 960, 2);
    Eq(922, m);
    Near(tone[0], copy[0], 1e-6); Near(tone[2 * 959], copy[2 * (m - 1)], 1e-6); // same start and end
    double maxStep = 0; for (int j = 1; j < m; j++) maxStep = Math.Max(maxStep, Math.Abs(copy[2 * j] - copy[2 * j - 2]));
    True(maxStep < 0.02, "no clicks (smooth waveform)");
}));
tests.Add(("Opus: libopus and the managed fallback decode the same audio", () =>
{
    var enc = new Concentus.Structs.OpusEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);
    var pcm = new short[1920]; var pkt = new byte[1500];
    var native = new XStream.Core.Audio.OpusLossDecoder();
    Environment.SetEnvironmentVariable("XSTREAM_OPUS", "managed");
    var managed = new XStream.Core.Audio.OpusLossDecoder();
    Environment.SetEnvironmentVariable("XSTREAM_OPUS", null);
    if (!native.DecoderName.Contains("libopus")) { Console.WriteLine("    (libopus not installed: managed only)"); return; }
    double worst = 0;
    for (int f = 0; f < 100; f++)
    {
        for (int i = 0; i < 960; i++) pcm[2 * i] = pcm[2 * i + 1] = (short)(Math.Sin((f * 960 + i) * 2 * Math.PI * 330 / 48000) * 9000);
#pragma warning disable CS0618
        int n = enc.Encode(pcm, 0, 960, pkt, 0, pkt.Length);
#pragma warning restore CS0618
        var data = pkt[..n];
        short[]? a = null, b = null;
        if (f == 50) continue; // a lost packet: both rebuild it from the next one's FEC data / conceal it
        native.Push(data, f, (p, k) => a = p[..(k * 2)]);
        managed.Push(data, f, (p, k) => b = p[..(k * 2)]);
        double fw = 0; for (int i = 0; i < Math.Min(a!.Length, b!.Length); i++) fw = Math.Max(fw, Math.Abs(a[i] - b[i]));
        if (Environment.GetEnvironmentVariable("XS_DEBUG_OPUS") is "1") Console.WriteLine($"    frame {f}: {fw}");
        // Around a loss the two conceal differently (libopus 1.5's concealment is newer); elsewhere only rounding differs.
        if (f < 50 || f > 60) worst = Math.Max(worst, fw);
    }
    True(worst < 32, $"outputs match away from the loss (worst sample difference {worst})");
    Eq(native.Decoded, managed.Decoded);
}));
tests.AddRange(SrtpTests.All);
tests.AddRange(StunTests.All);
tests.AddRange(FeatureTests.All);

int failed = 0;
foreach (var (name, body) in tests)
{
    try { body(); Console.WriteLine($"  PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"  FAIL  {name}\n        {ex.Message}"); }
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

static bool FfmpegAvailable(out string why)
{
    try { why = ""; _ = H264Decoder.LibVersion; return true; }
    catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { why = "FFmpeg libraries not found (set XSTREAM_FFMPEG)"; return false; }
}

static NetworkSample S(double t, double kbps, double loss = 0, double queue = 0, double trend = 0, double jitter = 1,
    int frames = 6, double sinceFrame = 16) =>
    new(t, loss, jitter, queue, trend, kbps, sinceFrame, frames, 0, false, 20);

static void Eq<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}");
}
static void Near(double expected, double actual, double tol)
{
    if (Math.Abs(expected - actual) > tol) throw new Exception($"expected {expected} ± {tol}, got {actual}");
}
static void True(bool c, string what) { if (!c) throw new Exception("expected true: " + what); }
static void False(bool c, string what) { if (c) throw new Exception("expected false: " + what); }

sealed class FakePad : IGamepadBackend
{
    private readonly GamepadState[] _s = new GamepadState[4];
    private readonly bool[] _c = new bool[4];
    public string Name => "fake";
    public int MaxPads => 4;
    public void Set(int i, GamepadState st) { lock (_s) { _s[i] = st; _c[i] = true; } }
    public void Unplug(int i) { lock (_s) _c[i] = false; }
    public void Poll(Span<GamepadState> states, Span<bool> connected) { lock (_s) for (int i = 0; i < 4; i++) { states[i] = _s[i]; connected[i] = _c[i]; } }
    public void Rumble(int pad, float low, float high, float lt, float rt, int ms) { }
    public void Dispose() { }
}
