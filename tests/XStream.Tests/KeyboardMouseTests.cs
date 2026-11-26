using System.Buffers.Binary;
using XStream.Core.Input;
using XStream.Core.Xbox;

/// <summary>Keyboard and mouse as a controller: key names, bindings, the mouse-to-stick model, capture, and the pump.</summary>
static class KeyboardMouseTests
{
    private static void Eq<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}");
    }
    private static void True(bool c, string what) { if (!c) throw new Exception("expected true: " + what); }
    private static void Near(double expected, double actual, double tol)
    {
        if (Math.Abs(expected - actual) > tol) throw new Exception($"expected {expected} ± {tol}, got {actual}");
    }
    private static int Code(string name) => KeyNames.TryParse(name, out int c) ? c : throw new Exception("no key " + name);

    public static IEnumerable<(string, Action)> All =>
    [
        ("Key names: SDL scancodes, Avalonia's physical key names, mouse buttons, Vexel's own keys", () =>
        {
            Eq(26, Code("W"));                       // SDL_SCANCODE_W
            Eq(26, Code("KeyW"));                    // Avalonia PhysicalKey.KeyW
            Eq(30, Code("Digit1")); Eq(30, Code("1"));
            Eq(225, Code("ShiftLeft")); Eq(225, Code("LeftShift"));
            Eq(82, Code("ArrowUp")); Eq(44, Code("space"));
            Eq(KeyNames.MouseBase + 1, Code("MouseLeft")); Eq(KeyNames.MouseBase + 3, Code("Mouse2"));
            True(!KeyNames.TryParse("Banana", out _), "unknown name");
            True(!KeyNames.TryParse("Key", out _), "not a key");
            foreach (var k in new[] { "Escape", "F3", "F10", "F11", "F12" }) True(KeyNames.IsReserved(Code(k)), k + " is Vexel's");
            True(!KeyNames.IsReserved(Code("F1")) && !KeyNames.IsReserved(Code("Tab")), "others are free");
            Eq("LeftShift", KeyNames.Name(225)); Eq("Left click", KeyNames.Long(KeyNames.MouseBase + 1));
        }),
        ("Keyboard bindings: the default layout", () =>
        {
            var b = KeyboardBindings.Default;
            Eq("", b.ToText());
            var s = Press(b, "Space", "E", "R", "LeftCtrl", "Q", "F", "LeftShift", "C", "Tab", "Enter", "Backquote");
            Eq(GamepadButtons.A | GamepadButtons.X | GamepadButtons.Y | GamepadButtons.B | GamepadButtons.LeftShoulder | GamepadButtons.RightShoulder
               | GamepadButtons.LeftThumb | GamepadButtons.RightThumb | GamepadButtons.View | GamepadButtons.Menu | GamepadButtons.Nexus, s.Buttons);
            s = Press(b, "MouseLeft", "MouseRight");
            Eq(1f, s.RightTrigger); Eq(1f, s.LeftTrigger);
            Eq(GamepadButtons.RightThumb, Press(b, "MouseMiddle").Buttons);
            Eq(GamepadButtons.DPadUp | GamepadButtons.DPadLeft, Press(b, "Up", "4").Buttons);
            s = Press(b, "W"); Eq(0f, s.LeftX); Eq(1f, s.LeftY);
            s = Press(b, "A"); Eq(-1f, s.LeftX);
            s = Press(b, "W", "D"); Near(0.7071, s.LeftX, 1e-3); Near(0.7071, s.LeftY, 1e-3);
            s = Press(b, "W", "S", "D"); Eq(0f, s.LeftY); Eq(1f, s.LeftX);   // opposites cancel
            Eq(default(GamepadState), Press(b));
        }),
        ("Keyboard bindings: parse changes, round-trip, bind a key, skip unknown and reserved keys", () =>
        {
            var b = KeyboardBindings.Parse("A=G, RT=Mouse4, Nexus=None, LeftStickUp=KeyW/ArrowUp, RightStickLeft=J, Foo=Bar, B=Escape, X=Nope", out var problems);
            Eq(3, problems.Count);
            True(problems.Contains("Foo=Bar") && problems.Contains("B=Escape") && problems.Contains("X=Nope"), string.Join(";", problems));
            Eq(GamepadButtons.A, Press(b, "G").Buttons);
            Eq(GamepadButtons.None, Press(b, "Space").Buttons);       // A's default key replaced
            Eq(1f, Press(b, "Mouse4").RightTrigger);
            Eq(0f, Press(b, "MouseLeft").RightTrigger);
            Eq(GamepadButtons.None, Press(b, "Backquote", "Home").Buttons);
            Eq(1f, Press(b, "Up").LeftY);
            Eq(-1f, Press(b, "J").RightX);
            Eq(0, b.Keys(KbmTarget.B).Count);                        // "B=Escape": Esc refused, nothing left
            var text = b.ToText();
            Eq(text, KeyboardBindings.Parse(text).ToText());
            True(text.Contains("A=G") && text.Contains("Nexus=None") && text.Contains("LeftStickUp=W/Up"), text);

            // Binding a key makes it that button's only key and takes it off whatever else it pressed.
            var c = KeyboardBindings.Default.Bind(KbmTarget.Y, Code("Space"));
            Eq(GamepadButtons.Y, Press(c, "Space").Buttons);
            Eq(GamepadButtons.None, Press(c, "R").Buttons);
            Eq(0, c.Keys(KbmTarget.A).Count);
            Eq("A=None,Y=Space", c.ToText());
            Eq("", KeyboardBindings.Default.With(KbmTarget.A, [Code("Space")]).ToText());
            True(KeyboardBindings.TryTarget("lt", out var lt) && lt == KbmTarget.LeftTrigger, "LT alias");
            True(!KeyboardBindings.TryTarget("3", out _), "numbers aren't targets");
        }),
        ("Mouse to stick: still is centre, a tiny nudge clears the deadzone, direction and invert", () =>
        {
            var m = new MouseStick();
            for (int i = 0; i < 50; i++) Eq((0f, 0f), m.Update(0, 0, 1));
            var (x, y) = m.Update(1, 0, 1);                           // one count
            for (int i = 0; i < 5; i++) (x, y) = m.Update(0, 0, 1);
            True(x >= 0.15f && x < 0.2f && y == 0, $"anti-deadzone lifts a 1-count nudge ({x})");
            m = new MouseStick();
            (x, y) = Steady(m, 0, 2, 200);                           // mouse pulled back (towards you)
            True(y < -0.15f && Math.Abs(x) < 1e-6, $"back = stick down ({y})");
            (x, y) = Steady(new MouseStick(invertY: true), 0, 2, 200);
            True(y > 0.15f, "inverted");
        }),
        ("Mouse to stick: speed sets deflection, sensitivity scales it, full speed is full stick", () =>
        {
            // 1 count per ms = 1000 counts/s = half of full scale at sensitivity 1.
            var (x, _) = Steady(new MouseStick(), 1, 0, 300);
            Near(0.15 + 0.85 * 0.5, x, 0.01);
            (x, _) = Steady(new MouseStick(sensitivity: 2), 1, 0, 300);
            Near(1, x, 0.001);
            (x, _) = Steady(new MouseStick(), 10, 0, 300);
            Eq(1f, x);                                                // clamped
            var (dx, dy) = Steady(new MouseStick(sensitivity: 3), 3, 4, 300);
            Near(1, Math.Sqrt(dx * dx + dy * dy), 1e-4);              // diagonal stays inside the circle
            Near(0.6, dx, 1e-3);
        }),
        ("Mouse to stick: smoothing hides a 125 Hz mouse at 1 kHz polling, and it settles to exactly centre", () =>
        {
            var m = new MouseStick();
            float lo = 1, hi = 0;
            for (int t = 0; t < 400; t++)
            {
                var (x, _) = m.Update(t % 8 == 0 ? 8 : 0, 0, 1);    // 8 counts every 8 ms = 1000 counts/s
                if (t >= 200) { lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
            }
            True(hi - lo < 0.06, $"ripple {hi - lo:F3}");
            Near(0.575, (lo + hi) / 2, 0.03);
            int settled = -1;
            for (int t = 0; t < 300; t++) if (m.Update(0, 0, 1) == (0f, 0f)) { settled = t; break; }
            True(settled is > 5 and < 120, $"back to centre after {settled} ms");
            for (int t = 0; t < 20; t++) Eq((0f, 0f), m.Update(0, 0, 1));
            // A first movement reaches most of its value within ~2x the smoothing.
            m = new MouseStick(smoothingMs: 16);
            float after30 = 0;
            for (int t = 0; t < 30; t++) after30 = m.Update(1, 0, 1).X;
            True(after30 > 0.5f, $"responsive ({after30:F2} of 0.575 after 30 ms)");
        }),
        ("Keyboard and mouse: click captures, keys and motion drive the pad, menu and focus loss release it", () =>
        {
            var relative = new List<bool>();
            bool blocked = false;
            var k = new KeyboardMouseInput(KeyboardMouseMode.Auto, KeyboardBindings.Default, new MouseStick(), relative.Add) { Blocked = () => blocked };
            int hints = 0;
            k.Hint += () => hints++;
            True(!k.OnEvent(Key(true, Code("W"))), "keys are never taken from the window");
            Eq(default(GamepadState), k.Read(0));                    // not captured yet
            True(!k.OnEvent(Button(true, 1)), "inactive: the click goes to the window");
            k.Active = true;
            k.Tick();
            Eq(1, hints);
            True(k.OnEvent(Button(true, 1)), "the capturing click is used up");
            True(k.Captured && relative.SequenceEqual([true]), "relative mouse on");
            k.OnEvent(Button(false, 1));
            var s = k.Read(1);
            Eq(1f, s.LeftY);                                          // W was held from before the click
            Eq(0f, s.RightTrigger);                                   // the capturing click didn't fire
            True(k.OnEvent(Button(true, 1)), "clicks while captured are the game's");
            k.OnEvent(Motion(5, 0));
            s = k.Read(2);
            Eq(1f, s.RightTrigger);
            True(s.RightX > 0.15f, $"mouse moved the right stick ({s.RightX})");
            True(k.OnEvent(Wheel(1)), "wheel used");
            True(!k.OnEvent(Button(true, 1, touch: true)), "touch taps stay taps");

            // Menu opens (Esc): neutral at once, mouse freed; closes: captured again without a click.
            blocked = true;
            Eq(default(GamepadState), k.Read(3));
            k.Tick();
            True(!k.Captured && relative[^1] == false, "released for the menu");
            True(!k.OnEvent(Button(true, 1)), "menu clicks reach the menu");
            blocked = false;
            k.Tick();
            True(k.Captured, "back after the menu");
            Eq(0f, k.Read(4).RightTrigger);                           // the menu click wasn't kept as held

            // Focus lost while playing (a permission prompt, another app): released and keys forgotten; when the
            // focus comes back, play carries on without another click.
            k.OnEvent(Focus(lost: true));
            True(!k.Captured, "released on focus loss");
            k.Tick();
            True(!k.Captured, "stays released while away");
            k.OnEvent(Focus(lost: false));
            k.Tick();
            True(k.Captured, "back when the focus returns");
            Eq(0f, k.Read(5).LeftY);                                  // W forgotten
            // A controller takes over (Auto): released.
            k.Active = false;
            k.Tick();
            True(!k.Captured, "released when a pad takes over");
            Eq(1, hints);
        }),
        ("Input pump: keyboard and mouse is pad 0 with no controller (Auto), steps aside for one, merges with one (On)", () =>
        {
            var pad = new Pad();
            GamepadState[]? last = null;
            var k = new KeyboardMouseInput(KeyboardMouseMode.Auto, KeyboardBindings.Default, new MouseStick(), _ => { });
            var changes = new List<(int, bool)>();
            var pump = new InputPump(pad, new InputOptions { KeyboardMouse = k }, f => last = f, (i, on) => changes.Add((i, on)));
            pump.Step(0);
            True(k.Active && last is [{ Index: 0 }], "virtual pad 0");
            Eq((0, true), changes[0]);
            k.Active = true; k.OnEvent(Button(true, 1)); k.OnEvent(Button(false, 1)); // capture
            k.OnEvent(Key(true, Code("Space")));
            pump.Step(1);
            Eq(GamepadButtons.A, last![0].Buttons);
            // A controller connects: it takes pad 0, keyboard steps aside, the console sees no disconnect.
            pad.Connected = true;
            pad.State = new GamepadState(0, GamepadButtons.B, 0, 0, 0, 0, 0, 0);
            pump.Step(2);
            True(!k.Active, "Auto steps aside");
            Eq(GamepadButtons.B, last![0].Buttons);
            Eq(1, changes.Count);
            // On: merged into the controller (buttons combined, sticks added).
            k.Mode = KeyboardMouseMode.On;
            k.OnEvent(Key(true, Code("D")));
            k.Active = true; k.OnEvent(Button(true, 1)); k.OnEvent(Button(false, 1));
            pad.State = new GamepadState(0, GamepadButtons.B, 0, 0.5f, 0, 0, 0, 0);
            pump.Step(3);
            Eq(GamepadButtons.A | GamepadButtons.B, last![0].Buttons);
            Eq(1, last!.Length);
            True(last[0].LeftX > 0.8f && last[0].LeftY > 0.4f, $"stick {last[0].LeftX},{last[0].LeftY}");
            k.Mode = KeyboardMouseMode.Off;
            pump.Step(4);
            Eq(GamepadButtons.B, last![0].Buttons);
        }),
    ];

    // ---- Helpers ----------------------------------------------------------------------------------------

    private static GamepadState Press(KeyboardBindings b, params string[] keys)
    {
        var down = new bool[KeyNames.CodeCount];
        foreach (var k in keys) down[Code(k)] = true;
        return b.Evaluate(down);
    }

    /// <summary>Feed a constant mouse speed (counts per 1 ms poll) and return where the stick settles.</summary>
    private static (float X, float Y) Steady(MouseStick m, double dx, double dy, int ms)
    {
        (float, float) s = default;
        for (int i = 0; i < ms; i++) s = m.Update(dx, dy, 1);
        return s;
    }

    // Raw SDL_Event bytes (SDL2's layout, little-endian).
    private static byte[] Ev(uint type)
    {
        var e = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(e, type);
        return e;
    }
    private static byte[] Key(bool down, int scancode)
    {
        var e = Ev(down ? 0x300u : 0x301u);
        e[12] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(16), scancode);
        return e;
    }
    private static byte[] Button(bool down, byte button, bool touch = false)
    {
        var e = Ev(down ? 0x401u : 0x402u);
        if (touch) BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), 0xFFFFFFFF);
        e[16] = button;
        e[17] = (byte)(down ? 1 : 0);
        return e;
    }
    private static byte[] Motion(int xrel, int yrel)
    {
        var e = Ev(0x400);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(28), xrel);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(32), yrel);
        return e;
    }
    private static byte[] Wheel(int y)
    {
        var e = Ev(0x403);
        BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(20), y);
        return e;
    }
    private static byte[] Focus(bool lost)
    {
        var e = Ev(0x200);
        e[12] = (byte)(lost ? 13 : 12);
        return e;
    }

    private sealed class Pad : IGamepadBackend
    {
        public GamepadState State;
        public bool Connected;
        public string Name => "test";
        public int MaxPads => 4;
        public void Poll(Span<GamepadState> s, Span<bool> c) { s[0] = State; c[0] = Connected; c[1] = c[2] = c[3] = false; }
        public void Rumble(int pad, float low, float high, float lt, float rt, int ms) { }
        public void Dispose() { }
    }
}
