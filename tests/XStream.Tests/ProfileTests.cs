using System.Buffers.Binary;
using XStream.Core.Config;
using XStream.Core.Input;
using XStream.Core.Xbox;

/// <summary>Turbo and toggle buttons (parsing, the pump), and per-game profiles beyond the layout (overrides, EffectiveFor, stream menu edits).</summary>
static class ProfileTests
{
    private static void Eq<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}");
    }
    private static void True(bool c, string what) { if (!c) throw new Exception("expected true: " + what); }

    public static IEnumerable<(string, Action)> All =>
    [
        ("Button modes: parse aliases and triggers, round-trip, skip unknowns, change one", () =>
        {
            var m = ButtonModes.Parse("A:turbo, lt:toggle, RB:Turbo, Paddle1:turbo, Foo:turbo, B:sideways, X", out var problems);
            Eq(4, problems.Count);
            True(problems.Contains("Paddle1:turbo") && problems.Contains("Foo:turbo") && problems.Contains("B:sideways") && problems.Contains("X"), string.Join(";", problems));
            Eq(ButtonMode.Turbo, m.Get("A"));
            Eq(ButtonMode.Turbo, m.Get("RightShoulder"));
            Eq(ButtonMode.Toggle, m.Get("LeftTrigger"));
            Eq(ButtonMode.Normal, m.Get("B"));
            Eq("A:turbo,RightShoulder:turbo,LT:toggle", m.ToText());
            Eq(m.ToText(), ButtonModes.Parse(m.ToText()).ToText());
            Eq("A, RB", m.Describe(ButtonMode.Turbo));
            var n = m.With("A", ButtonMode.Toggle).With("RB", ButtonMode.Normal).With("LT", ButtonMode.Normal);
            Eq("A:toggle", n.ToText());
            True(n.With("A", ButtonMode.Normal).IsEmpty && ButtonModes.Parse("").IsEmpty, "empty");
            Eq("B:toggle", ButtonModes.Parse("B:turbo,B:toggle").ToText()); // the last one wins
        }),
        ("Turbo: first press at once, then presses at the rate; the pump sends every edge", () =>
        {
            var pad = new Pad { Connected = true, State = new GamepadState(0, GamepadButtons.A, 0, 0, 0, 0, 0, 0) };
            var frames = new List<(double T, GamepadState S)>();
            double t = 0;
            var pump = new InputPump(pad, new InputOptions { Modes = ButtonModes.Parse("A:turbo"), TurboRate = 10, HeartbeatMs = 1e9 }, f => frames.Add((t, f[0])));
            for (t = 0; t < 1000; t++) pump.Step(t);
            Eq(GamepadButtons.A, frames[0].S.Buttons);
            Eq(0.0, frames[0].T);
            // 10 a second for 1 s: 10 presses and 10 releases, each sent the moment it happens (every 50 ms).
            int downs = frames.Count(f => f.S.Buttons == GamepadButtons.A), ups = frames.Count(f => f.S.Buttons == GamepadButtons.None);
            Eq(10, downs); Eq(10, ups);
            Eq(50.0, frames[1].T);
            Eq(100.0, frames[2].T);
            // Let go: released (and stays released).
            pad.State = default;
            frames.Clear();
            pump.Step(1000.5); pump.Step(1030); pump.Step(1060);
            True(frames.All(f => f.S.Buttons == GamepadButtons.None), "released");
            // Faster rate on a held trigger: RT pulses 0 / 1.
            pump.SetController(ControllerMapping.None, null, "", ButtonModes.Parse("RT:turbo"), 20);
            pad.State = new GamepadState(0, GamepadButtons.None, 0, 0, 0, 0, 0, 0.7f);
            frames.Clear();
            for (t = 2000; t < 2100; t++) pump.Step(t);
            Eq(1f, frames[0].S.RightTrigger);
            True(frames.Count(f => f.S.RightTrigger == 0) == 2 && frames.Count(f => f.S.RightTrigger == 1) == 2, $"two presses in 100 ms ({frames.Count})");
        }),
        ("Toggle: tap to hold, tap to let go; the stream menu doesn't flip it and it survives the menu", () =>
        {
            var pad = new Pad { Connected = true };
            GamepadState last = default;
            bool menu = false;
            var pump = new InputPump(pad, new InputOptions { Modes = ButtonModes.Parse("A:toggle,LT:toggle") }, f => last = f[0]) { ModesPaused = () => menu };
            void Tap(GamepadButtons b, double at, float lt = 0)
            {
                pad.State = new GamepadState(0, b, 0, 0, 0, 0, lt, 0); pump.Step(at);
                pad.State = default; pump.Step(at + 20);
            }
            Tap(GamepadButtons.A, 0);
            Eq(GamepadButtons.A, last.Buttons);               // held after letting go
            pad.State = new GamepadState(0, GamepadButtons.B, 0, 0, 0, 0, 0, 0); pump.Step(50);
            Eq(GamepadButtons.A | GamepadButtons.B, last.Buttons); // other buttons as normal
            pad.State = default; pump.Step(60);
            // The menu opens: real buttons pass through for it, presses there don't flip the toggle.
            menu = true;
            pump.Step(70);
            Eq(GamepadButtons.None, last.Buttons);
            pad.State = new GamepadState(0, GamepadButtons.A, 0, 0, 0, 0, 0, 0); pump.Step(80);
            Eq(GamepadButtons.A, last.Buttons);               // the menu sees a normal press
            menu = false;                                     // closed by that press, still held
            pump.Step(90);
            Eq(GamepadButtons.A, last.Buttons);
            pad.State = default; pump.Step(100);
            Eq(GamepadButtons.A, last.Buttons);               // still latched after the menu
            Tap(GamepadButtons.A, 120);
            Eq(GamepadButtons.None, last.Buttons);            // second tap lets go
            // A toggled trigger holds at full.
            Tap(GamepadButtons.None, 200, lt: 0.8f);
            Eq(1f, last.LeftTrigger);
            Tap(GamepadButtons.None, 300, lt: 0.6f);
            Eq(0f, last.LeftTrigger);
        }),
        ("Turbo and toggle: release on unplug and on a new layout; paddles and keys get the mode of what they press", () =>
        {
            var pad = new Pad { Connected = true };
            GamepadState[] last = [];
            var k = new KeyboardMouseInput(KeyboardMouseMode.Off, KeyboardBindings.Default, new MouseStick(), _ => { });
            var pump = new InputPump(pad, new InputOptions { Modes = ButtonModes.Parse("A:toggle"), KeyboardMouse = k, HeartbeatMs = 1e9 }, f => last = f);
            pad.State = new GamepadState(0, GamepadButtons.A, 0, 0, 0, 0, 0, 0); pump.Step(0);
            pad.State = default; pump.Step(10);
            Eq(GamepadButtons.A, last[0].Buttons);
            // Unplugged: everything let go, and plugging back in doesn't bring the latch back.
            pad.Connected = false; pump.Step(20);
            Eq(GamepadButtons.None, last[0].Buttons);
            pad.Connected = true; pump.Step(30);
            Eq(GamepadButtons.None, last[0].Buttons);
            // Latch again, then another game's layout: let go.
            pad.State = new GamepadState(0, GamepadButtons.A, 0, 0, 0, 0, 0, 0); pump.Step(40);
            pad.State = default; pump.Step(50);
            Eq(GamepadButtons.A, last[0].Buttons);
            pump.SetController(ControllerMapping.Parse("Paddle1=X"), null, "", ButtonModes.Parse("A:toggle,X:turbo"), 10);
            pump.Step(60);
            Eq(GamepadButtons.None, last[0].Buttons);
            // A paddle pressing X: X is turbo, so the paddle pulses X.
            pad.Paddles = 1; pump.Step(100);
            Eq(GamepadButtons.X, last[0].Buttons);
            pump.Step(150);
            Eq(GamepadButtons.None, last[0].Buttons);
            pad.Paddles = 0; pump.Step(160);
            // A key pressing A (Space) gets A's toggle too.
            k.Mode = KeyboardMouseMode.On; pump.Step(170);
            k.Active = true; k.OnEvent(Click(true)); k.OnEvent(Click(false)); // capture
            k.OnEvent(Key(true, 44)); pump.Step(200);
            k.OnEvent(Key(false, 44)); pump.Step(210);
            Eq(GamepadButtons.A, last[0].Buttons);
            k.OnEvent(Key(true, 44)); pump.Step(220);
            Eq(GamepadButtons.None, last[0].Buttons);
        }),
        ("Settings: a file from before per-game overrides loads unchanged and round-trips", () =>
        {
            const string old = """
            {
              "SettingsVersion": 6, "ButtonMapping": "A=B", "KeyboardMouse": "Auto", "MouseSensitivity": 0.5, "Enhance": true, "Clarity": "Low",
              "GameProfiles": [ { "GameKey": "123", "GameName": "Halo", "ButtonMapping": "X=Y", "GyroAim": true, "GyroSensitivity": 1.5, "GyroHoldButton": "" } ]
            }
            """;
            var path = Path.Combine(Path.GetTempPath(), $"xs-old-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, old);
                var s = AppSettings.Load(path);
                Eq("", s.ButtonModes); Eq(10.0, s.TurboRate);
                var raw = s.GameProfile("123")!;
                True(raw.HasOwnLayout && !raw.HasOverrides && raw.ButtonModes is null && raw.Enhance is null, "old profile: a layout, no overrides");
                var c = s.ControllerFor("123");
                Eq("X=Y", c.ButtonMapping); True(c.GyroAim, "gyro kept");
                Eq(0.5, c.MouseSensitivity); Eq(ClarityLevel.Low, c.Clarity); Eq("", c.ButtonModes);
                var e = s.EffectiveFor("123");
                Eq("X=Y", e.ButtonMapping); Eq(0.5, e.MouseSensitivity); Eq(KeyboardMouseMode.Auto, e.KeyboardMouse);
                s.Save(path);
                var back = AppSettings.Load(path);
                Eq(raw, back.GameProfile("123"));
                Eq("A=B", back.ButtonMapping);
                Eq(s.ControllerFor("123"), back.ControllerFor("123"));
            }
            finally { File.Delete(path); }
        }),
        ("Settings: EffectiveFor applies a game's overrides; unset fields follow all games", () =>
        {
            var s = new AppSettings { KeyboardMouse = KeyboardMouseMode.Off, MouseSensitivity = 1, Enhance = true, Clarity = ClarityLevel.Low, ButtonMapping = "A=B", ButtonModes = "A:turbo" }
                .WithProfile(new ControllerProfile("halo", "Halo") { OwnLayout = false, KeyboardMouseNative = true, Clarity = ClarityLevel.High })
                .WithProfile(new ControllerProfile("forza", "Forza", "X=Y") { ButtonModes = "RT:toggle", TurboRate = 15, Enhance = false, MouseSensitivity = 2 });
            var halo = s.EffectiveFor("HALO");
            True(halo.KeyboardMouseNative, "real keyboard and mouse in Halo");
            Eq(KeyboardMouseMode.Auto, halo.KeyboardMouse);      // chose real for Halo: on, though off for all games
            Eq(ClarityLevel.High, halo.Clarity); True(halo.Enhance, "Enhance from all games");
            Eq("A=B", halo.ButtonMapping); Eq("A:turbo", halo.ButtonModes); // no layout of its own: all games' buttons and turbo
            var forza = s.EffectiveFor("forza");
            Eq("X=Y", forza.ButtonMapping); Eq("RT:toggle", forza.ButtonModes); Eq(15.0, forza.TurboRate);
            True(!forza.Enhance && !forza.KeyboardMouseNative, "Forza's picture, controller emulation");
            Eq(KeyboardMouseMode.Off, forza.KeyboardMouse); Eq(2.0, forza.MouseSensitivity);
            Eq(s, s.EffectiveFor("other")); Eq(s, s.EffectiveFor(null));
            // The profiles agree with it, and say what changed.
            var h = s.ControllerFor("halo");
            Eq("real keyboard and mouse", h.KeyboardMouseText);
            True(!h.SameSetup(s.DefaultController) && h.SameController(s.DefaultController), "same buttons, different keyboard and mouse");
            True(s.ControllerFor("forza").Summary.Contains("toggle RT"), s.ControllerFor("forza").Summary);
            True(s.DefaultController.Summary.Contains("turbo A at 10 a second"), s.DefaultController.Summary);
            True(!s.NeedsControllerExtras, "nothing needs paddles or gyro");
        }),
        ("Settings: stream menu edits go to the running game's profile, or to all games when it has none", () =>
        {
            var s = new AppSettings { Enhance = true, Clarity = ClarityLevel.Low, MouseSensitivity = 1, Volume = 100 }
                .WithProfile(new ControllerProfile("halo", "Halo") { OwnLayout = false, Clarity = ClarityLevel.High });
            // Halo: Enhance off and the volume, from the menu (which shows Halo's effective values).
            var next = s.ApplyEdit("halo", e => e with { Enhance = !e.Enhance, Volume = 50, Clarity = e.Clarity });
            Eq(false, next.GameProfile("halo")!.Enhance);
            Eq(ClarityLevel.High, next.GameProfile("halo")!.Clarity);
            True(next.Enhance, "all games keep Enhance");
            Eq(ClarityLevel.Low, next.Clarity);
            Eq(50, next.Volume);                                    // not per game: everywhere
            next = next.ApplyEdit("halo", e => e with { MouseSensitivity = 1.5 });
            Eq(1.5, next.GameProfile("halo")!.MouseSensitivity); Eq(1.0, next.MouseSensitivity);
            // No profile: the defaults, as before.
            next = next.ApplyEdit("forza", e => e with { Clarity = ClarityLevel.Off });
            Eq(ClarityLevel.Off, next.Clarity);
            Eq(null, next.GameProfile("forza"));
            next = next.ApplyEdit(null, e => e with { Enhance = false });
            True(!next.Enhance, "no game: defaults");
        }),
    ];

    private static byte[] Key(bool down, int scancode)
    {
        var e = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(e, down ? 0x300u : 0x301u);
        e[12] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(16), scancode);
        return e;
    }
    private static byte[] Click(bool down)
    {
        var e = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(e, down ? 0x401u : 0x402u);
        e[16] = 1;
        e[17] = (byte)(down ? 1 : 0);
        return e;
    }

    private sealed class Pad : IGamepadBackend
    {
        public GamepadState State;
        public bool Connected;
        public byte Paddles;
        public string Name => "test";
        public int MaxPads => 4;
        public void Poll(Span<GamepadState> s, Span<bool> c) { s[0] = State; c[0] = Connected; c[1] = c[2] = c[3] = false; }
        public PadExtras Extras(int pad) => new(Paddles, 0, 0, false);
        public void Rumble(int pad, float low, float high, float lt, float rt, int ms) { }
        public void Dispose() { }
    }
}
