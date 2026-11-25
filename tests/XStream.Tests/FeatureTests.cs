using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using XStream.Core.Input;
using XStream.Core.Video;
using XStream.Core.Xbox;
using XStream.Player;

/// <summary>Sleep/wake, remote control, controller layout and gyro, instant replay (MP4), launch shortcuts.</summary>
static class FeatureTests
{
    private static void Eq<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}");
    }
    private static void True(bool c, string what) { if (!c) throw new Exception("expected true: " + what); }

    public static IEnumerable<(string, Action)> All =>
    [
        ("Sleep: a jump in the wall clock is noticed, normal ticks aren't", () =>
        {
            var d = new SuspendDetector();
            var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            True(d.Observe(t) is null, "first call");
            for (int i = 1; i <= 5; i++) True(d.Observe(t.AddSeconds(i)) is null, "1 s ticks");
            var away = d.Observe(t.AddSeconds(5 + 90));
            True(away is { TotalSeconds: > 89 and < 91 }, "90 s asleep");
            True(d.Observe(t.AddSeconds(96)) is null, "back to normal");
        }),
        ("Watchdog: the launcher sleeping isn't taken for a frozen stream", () =>
        {
            var w = new StreamWatchdog();
            var t = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            w.Arm(t);
            True(w.Check(t.AddSeconds(2)) is null, "normal check");
            // Asleep for 5 minutes: no heartbeat in that time, but no checks either.
            True(w.Check(t.AddMinutes(5)) is null, "first check after waking");
            True(w.Check(t.AddMinutes(5).AddSeconds(2)) is null, "next check");
            // A real hang afterwards is still caught.
            string? fired = null;
            for (int s = 4; s <= 30 && fired is null; s += 2) fired = w.Check(t.AddMinutes(5).AddSeconds(s));
            True(fired is not null, "silence after waking still closes a hung stream");
        }),
        ("Remote control: command body and console list", () =>
        {
            var body = JsonNode.Parse(XboxConsoleControl.CommandBody("sess", "F4000ABC", "Shell", "InjectString",
                new JsonObject { ["replacementString"] = "hello" }))!;
            Eq("Xbox", body["destination"]!.GetValue<string>());
            Eq("Shell", body["type"]!.GetValue<string>());
            Eq("InjectString", body["command"]!.GetValue<string>());
            Eq("com.microsoft.smartglass", body["sourceId"]!.GetValue<string>());
            Eq("F4000ABC", body["linkedXboxId"]!.GetValue<string>());
            Eq("hello", body["parameters"]![0]!["replacementString"]!.GetValue<string>());
            var power = JsonNode.Parse(XboxConsoleControl.CommandBody("s", "X", "Power", "TurnOff", null))!;
            Eq(1, power["parameters"]!.AsArray().Count); // [{}], never empty
            var list = XboxConsoleControl.ParseConsoles(JsonNode.Parse("""
                {"result":[{"id":"F4000ABC","name":"Living Room","powerState":"ConnectedStandby","remoteManagementEnabled":true}],"status":{"errorCode":"OK"}}
                """)!);
            Eq(1, list.Count);
            Eq(new ControlledConsole("F4000ABC", "Living Room", "ConnectedStandby", true), list[0]);
        }),
        ("Remote control: installed apps (as the console returned them live)", () =>
        {
            var apps = XboxConsoleControl.ParseInstalled(JsonNode.Parse("""
                {"result":[{"name":"Rust Console Edition X|S","oneStoreProductId":"9PJVJ4WF031H","titleId":893395429,"isGame":true},
                           {"name":"Kodi 18.5","oneStoreProductId":null,"titleId":null}]}
                """)!);
            Eq(2, apps.Count);
            Eq(("9PJVJ4WF031H", "893395429", "Rust Console Edition X|S"), apps[0]);
            Eq("", apps[1].ProductId);
        }),
        ("Controller mapping: swaps, paddles, None, aliases and bad entries", () =>
        {
            var m = ControllerMapping.Parse("A=B, B=A, Paddle1=LS, View=None, rb=lb, Nope=A, X=Banana", out var problems);
            Eq(2, problems.Count);
            Eq(GamepadButtons.B, m.Apply(GamepadButtons.A, 0));
            Eq(GamepadButtons.A | GamepadButtons.B, m.Apply(GamepadButtons.A | GamepadButtons.B, 0));
            Eq(GamepadButtons.None, m.Apply(GamepadButtons.View, 0));
            Eq(GamepadButtons.LeftShoulder, m.Apply(GamepadButtons.RightShoulder, 0));
            Eq(GamepadButtons.Y | GamepadButtons.LeftThumb, m.Apply(GamepadButtons.Y, 0b0001));
            Eq(GamepadButtons.Y, m.Apply(GamepadButtons.Y, 0b0010)); // Paddle2 isn't mapped
            True(ControllerMapping.Parse("").IsIdentity, "empty is identity");
            True(ControllerMapping.Held("Paddle2", 0, 0b0010), "paddle hold");
            True(ControllerMapping.Held("LeftShoulder", GamepadButtons.LeftShoulder, 0), "button hold");
        }),
        ("Input pump applies the mapping", () =>
        {
            var pad = new MapPad();
            GamepadState[]? last = null;
            var pump = new InputPump(pad, new InputOptions { Mapping = ControllerMapping.Parse("A=B") }, f => last = f);
            pad.State = new GamepadState(0, GamepadButtons.A, 0, 0, 0, 0, 0, 0);
            pump.Step(0);
            Eq(GamepadButtons.B, last![0].Buttons);
        }),
        ("Gyro: still is still, turning moves the stick past the game's deadzone, capped at full", () =>
        {
            var g = new GyroAim();
            Eq((0f, 0f), g.Stick(0.005f, -0.005f));
            var (x, y) = g.Stick(0, -0.2f); // turning right a little
            True(x > 0.12f && x < 0.3f && Math.Abs(y) < 1e-6, $"small right turn -> ({x}, {y})");
            var (fx, _) = g.Stick(0, -20f);
            True(Math.Abs(fx - 1) < 1e-5, "fast turn caps at full stick");
            var (_, uy) = g.Stick(0.5f, 0);
            True(uy > 0, "tilting up aims up");
            Eq((1f, -0.5f), GyroAim.Combine(0.8f, -0.5f, (0.5f, 0f)));
        }),
        ("Opus packet durations from the TOC byte", () =>
        {
            Eq(960, Mp4Writer.OpusSamples([0xF8]));       // CELT 20 ms, one frame
            Eq(480, Mp4Writer.OpusSamples([0xF0]));       // CELT 10 ms
            Eq(1920, Mp4Writer.OpusSamples([0xF9, 0]));   // two 20 ms frames
            Eq(2880, Mp4Writer.OpusSamples([0xFB, 3]));   // code 3, three frames
            Eq(960, Mp4Writer.OpusSamples([0x08]));       // SILK 20 ms
        }),
        ("MP4: boxes, avcC from the keyframe's SPS/PPS, samples length-prefixed", () =>
        {
            var (frames, audio) = SyntheticClip(seconds: 2);
            using var ms = new MemoryStream();
            Mp4Writer.Write(ms, frames, audio);
            var file = ms.ToArray();
            var top = Boxes(file, 0, file.Length).Select(b => b.type).ToList();
            Eq("ftyp,mdat,moov", string.Join(",", top));
            var moov = Boxes(file, 0, file.Length).Single(b => b.type == "moov");
            var traks = Boxes(file, moov.at + 8, moov.at + moov.size).Where(b => b.type == "trak").ToList();
            Eq(2, traks.Count);
            string text = Encoding.ASCII.GetString(file);
            True(text.Contains("avcC") && text.Contains("dOps") && text.Contains("stss") && text.Contains("co64"), "sample tables");
            // First video sample: the IDR slice with a 4-byte length (parameter sets moved to avcC).
            var mdat = Boxes(file, 0, file.Length).Single(b => b.type == "mdat");
            int first = mdat.at + 16;
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(first));
            Eq(5, file[first + 4] & 0x1F);
            True(len > 0 && len < 1000, "slice length");
        }),
        ("Instant replay keeps whole groups from the keyframe before the window", () =>
        {
            double now = 0;
            var r = new ReplayBuffer(seconds: 30) { Clock = () => now };
            r.AddVideo(P(1), keyframe: false); // before any keyframe: useless, dropped
            var (sps, pps, idr, p) = Nals();
            for (int f = 0; f < 60 * 70; f++) // 70 s at 60 fps, a keyframe every 10 s
            {
                now = f * 1000.0 / 60;
                bool key = f % 600 == 0;
                r.AddVideo(key ? Concat(sps, pps, idr) : p, key);
                if (f % 3 == 0) r.AddAudio([0xF8, 1, 2, 3]);
            }
            var (v, a) = r.Snapshot();
            True(v[0].Keyframe, "starts on a keyframe");
            double length = v[^1].AtMs / 1000;
            True(length >= 30 && length <= 40.1, $"30-40 s kept ({length:F1})");
            True(a.Count > 0 && a[0].AtMs >= 0, "audio from the clip's start");
            r.Clear();
            Eq(0, r.Snapshot().Video.Count);
        }),
        ("Game profiles: per-game layout, default otherwise, add/replace/remove, saved and loaded", () =>
        {
            var s = new XStream.Core.Config.AppSettings { ButtonMapping = "A=B" };
            Eq("A=B", s.ControllerFor("123").ButtonMapping);
            Eq("", s.ControllerFor("123").GameKey);
            s = s.WithProfile(new XStream.Core.Config.ControllerProfile("123", "Halo", "", GyroAim: true, GyroSensitivity: 1.5));
            s = s.WithProfile(new XStream.Core.Config.ControllerProfile("cloud:FORTNITE", "Fortnite", "X=Y"));
            Eq("Halo", s.ControllerFor("123").GameName);
            True(s.ControllerFor("123").GyroAim, "Halo has gyro");
            Eq("A=B", s.ControllerFor("999").ButtonMapping);   // no profile: default
            Eq("A=B", s.ControllerFor(null).ButtonMapping);
            True(s.NeedsControllerExtras, "a gyro profile needs the SDL backend");
            s = s.WithProfile(new XStream.Core.Config.ControllerProfile("123", "Halo", "B=A"));
            Eq(2, s.GameProfiles!.Length);                     // replaced, not added
            Eq("B=A", s.ControllerFor("123").ButtonMapping);
            var path = Path.Combine(Path.GetTempPath(), $"xs-profiles-{Guid.NewGuid():N}.json");
            try
            {
                s.Save(path);
                var back = XStream.Core.Config.AppSettings.Load(path);
                Eq(2, back.GameProfiles!.Length);
                Eq("X=Y", back.ControllerFor("cloud:FORTNITE").ButtonMapping);
            }
            finally { File.Delete(path); }
            s = s.WithoutProfile("123").WithoutProfile("cloud:FORTNITE");
            True(s.GameProfiles is null, "last one removed");
            True(!s.NeedsControllerExtras, "nothing needs extras now");
        }),
        ("Input pump switches layout live", () =>
        {
            var pad = new MapPad();
            GamepadState[]? last = null;
            var pump = new InputPump(pad, new InputOptions(), f => last = f);
            pad.State = new GamepadState(0, GamepadButtons.A, 0, 0, 0, 0, 0, 0);
            pump.Step(0);
            Eq(GamepadButtons.A, last![0].Buttons);
            pump.SetController(ControllerMapping.Parse("A=Y"), null, "");
            pump.Step(1);
            Eq(GamepadButtons.Y, last![0].Buttons);
        }),
        ("Launch arguments", () =>
        {
            Eq(new LaunchArgs(true, null, null, null), LaunchArgs.Parse(["--stream"]));
            Eq(new LaunchArgs(true, "Living Room", null, null), LaunchArgs.Parse(["--stream", "Living Room"]));
            Eq(new LaunchArgs(true, "Den", null, null), LaunchArgs.Parse(["--stream=Den"]));
            Eq(new LaunchArgs(true, "Den", "9NBLGGH4R315", null), LaunchArgs.Parse(["--game", "9NBLGGH4R315", "--stream", "Den"]));
            Eq(new LaunchArgs(false, null, null, "FORTNITE"), LaunchArgs.Parse(["--cloud", "FORTNITE"]));
            True(!LaunchArgs.Parse([]).Any, "no arguments");
            Eq("--stream \"Living Room\"", LaunchArgs.ForConsole("Living Room"));
        }),
        ("Title history product ids are read", () =>
        {
            var j = JsonNode.Parse("""
                {"titles":[{"titleId":"123","name":"Forza","type":"Game","devices":["XboxSeries"],"productId":"9NKX70BBCDRN",
                  "images":[{"type":"SuperHeroArt","url":"https://x/a.jpg"}],"titleHistory":{"lastTimePlayed":"2026-11-01T10:00:00Z"}}]}
                """)!;
            Eq("9NKX70BBCDRN", XboxLibrary.Parse(j)[0].ProductId);
        }),
    ];

    // ---- Helpers ----------------------------------------------------------------------------------------

    private sealed class MapPad : IGamepadBackend
    {
        public GamepadState State;
        public string Name => "map";
        public int MaxPads => 4;
        public void Poll(Span<GamepadState> s, Span<bool> c) { s[0] = State; c[0] = true; }
        public void Rumble(int pad, float low, float high, float lt, float rt, int ms) { }
        public void Dispose() { }
    }

    private static byte[] P(byte n) => [0, 0, 0, 1, 0x41, n, 2, 3];

    // A real 64x64 baseline SPS (so H264Format can read the size) plus small PPS / IDR / P slices.
    private static (byte[] sps, byte[] pps, byte[] idr, byte[] p) Nals() =>
        ([0, 0, 0, 1, 0x67, 0x42, 0xC0, 0x0A, 0xD9, 0x07, 0x3C, 0x04, 0x40, 0x00, 0x00, 0x03, 0x00, 0x40, 0x00, 0x00, 0x0F, 0x03, 0xC4, 0x89, 0x92],
         [0, 0, 0, 1, 0x68, 0xCB, 0x83, 0xCB, 0x20],
         [0, 0, 0, 1, 0x65, 0x88, 0x84, 0x00, 0x33, 0xFF],
         [0, 0, 0, 1, 0x41, 0x9A, 0x22, 0x6C, 0x42]);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

    private static (List<ClipFrame>, List<ClipAudio>) SyntheticClip(int seconds)
    {
        var (sps, pps, idr, p) = Nals();
        var frames = new List<ClipFrame>();
        var audio = new List<ClipAudio>();
        for (int f = 0; f < 60 * seconds; f++)
            frames.Add(new ClipFrame(f == 0 ? Concat(sps, pps, idr) : p, f * 1000.0 / 60, f == 0));
        for (int a = 0; a < 50 * seconds; a++) audio.Add(new ClipAudio([0xF8, 0xFF, 0xFE], a * 20.0));
        return (frames, audio);
    }

    private static IEnumerable<(string type, int at, int size)> Boxes(byte[] f, int from, int to)
    {
        int at = from;
        while (at + 8 <= to)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(f.AsSpan(at));
            string type = Encoding.ASCII.GetString(f, at + 4, 4);
            if (size == 1) size = BinaryPrimitives.ReadInt64BigEndian(f.AsSpan(at + 8));
            if (size < 8) yield break;
            yield return (type, at, (int)size);
            at += (int)size;
        }
    }
}
