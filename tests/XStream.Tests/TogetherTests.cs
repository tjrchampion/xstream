using XStream.Core.Config;
using System.Net;
using XStream.Core.Input;
using XStream.Core.Together;
using XStream.Core.Xbox;
using XStream.Player;

/// <summary>Play together: wire format, encryption, frame recovery, invite and reply codes, guest pads, and a loopback session.</summary>
static class TogetherTests
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

    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    /// <summary>The host's fragments for one frame, as the host builds them (plaintext bodies).</summary>
    private static byte[][] Fragments(uint id, byte[] data, bool key, uint ts = 0)
    {
        int count = Math.Max(1, (data.Length + TogetherMessages.MaxFragmentPayload - 1) / TogetherMessages.MaxFragmentPayload);
        var parts = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            int off = i * TogetherMessages.MaxFragmentPayload, len = Math.Min(TogetherMessages.MaxFragmentPayload, data.Length - off);
            parts[i] = new byte[TogetherMessages.VideoHeaderSize + len];
            TogetherMessages.VideoHeader(parts[i], id, (ushort)i, (ushort)count, key, ts, 0);
            data.AsSpan(off, len).CopyTo(parts[i].AsSpan(TogetherMessages.VideoHeaderSize));
        }
        return parts;
    }

    private static void Feed(FrameAssembler a, byte[] body, double now, List<AssembledFrame> output, List<NackEntry> nacks)
    {
        True(TogetherMessages.ReadVideo(body, out var f), "fragment parses");
        a.OnFragment(f, now, output, nacks);
    }

    private static byte[] Frame(int seed, int size) { var b = new byte[size]; new Random(seed).NextBytes(b); return b; }

    private sealed class Pads : IGamepadBackend
    {
        public readonly GamepadState[] S = new GamepadState[4];
        public readonly bool[] C = new bool[4];
        public string Name => "test";
        public int MaxPads => 4;
        public void Poll(Span<GamepadState> states, Span<bool> connected) { for (int i = 0; i < 4; i++) { states[i] = S[i]; connected[i] = C[i]; } }
        public void Rumble(int pad, float low, float high, float lt, float rt, int ms) { }
        public void Dispose() { }
    }

    public static IEnumerable<(string, Action)> All =>
    [
        ("Together: every message survives a round trip", () =>
        {
            var b = new byte[TogetherMessages.MaxBody];
            int n = TogetherMessages.Hello(b, 0xDEADBEEF, "Sam");
            True(TogetherMessages.ReadHello(b.AsSpan(0, n), out var h), "hello");
            Eq((byte)TogetherMessages.ProtocolVersion, h.Version); Eq(0xDEADBEEFu, h.SessionId); Eq("Sam", h.Name);

            n = TogetherMessages.Welcome(b, new WelcomeMsg(2, "Alex", "Forza Horizon 5", "https://art/x.jpg"));
            True(TogetherMessages.ReadWelcome(b.AsSpan(0, n), out var w, out byte v), "welcome");
            Eq(new WelcomeMsg(2, "Alex", "Forza Horizon 5", "https://art/x.jpg"), w); Eq(TogetherMessages.ProtocolVersion, v);

            var frag = Fragments(77, Frame(1, 3000), key: true, ts: 123456)[2];
            True(TogetherMessages.ReadVideo(frag, out var f), "video");
            Eq(77u, f.FrameId); Eq((ushort)2, f.Index); Eq((ushort)3, f.Count); True(f.Keyframe, "keyframe flag"); Eq(123456u, f.RtpTimestamp);
            Eq(3000 - 2 * TogetherMessages.MaxFragmentPayload, f.Payload.Length);

            n = TogetherMessages.Audio(b, 65000, [1, 2, 3]);
            True(TogetherMessages.ReadAudio(b.AsSpan(0, n), out ushort seq, out var opus), "audio");
            Eq((ushort)65000, seq); Eq(3, opus.Length);

            var pad = new GamepadState(0, GamepadButtons.A | GamepadButtons.Nexus, -1f, 0.5f, 0.25f, -0.75f, 0.3f, 1f);
            n = TogetherMessages.Input(b, 42, pad);
            True(TogetherMessages.ReadInput(b.AsSpan(0, n), out uint iseq, out var back), "input");
            Eq(42u, iseq); Eq(pad.Buttons, back.Buttons);
            Near(-1, back.LeftX, 1e-4); Near(0.5, back.LeftY, 1e-4); Near(0.25, back.RightX, 1e-4); Near(-0.75, back.RightY, 1e-4);
            Near(0.3, back.LeftTrigger, 1e-4); Near(1, back.RightTrigger, 1e-4);

            n = TogetherMessages.Rumble(b, new RumbleMsg(0.5f, 1f, 0, 0.25f, 300));
            True(TogetherMessages.ReadRumble(b.AsSpan(0, n), out var r), "rumble");
            Near(0.5, r.Low, 0.01); Near(1, r.High, 0.01); Near(0.25, r.RightTrigger, 0.01); Eq(300, r.DurationMs);

            n = TogetherMessages.Nack(b, [new NackEntry(5, 1), new NackEntry(6, NackEntry.WholeFrame)]);
            var entries = new NackEntry[10];
            Eq(2, TogetherMessages.ReadNack(b.AsSpan(0, n), entries));
            Eq(new NackEntry(6, NackEntry.WholeFrame), entries[1]);

            n = TogetherMessages.Stats(b, new StatsMsg(2.5f, 14800, 12.3f));
            True(TogetherMessages.ReadStats(b.AsSpan(0, n), out var s), "stats");
            Near(2.5, s.LossPct, 0.01); Near(14800, s.Kbps, 1); Near(12.3, s.RttMs, 0.1);

            n = TogetherMessages.Ping(b, 9, pong: true);
            Eq(TogetherMsg.Pong, TogetherMessages.TypeOf(b)); True(TogetherMessages.ReadPing(b.AsSpan(0, n), out uint pid) && pid == 9, "pong");
            n = TogetherMessages.Bye(b, ByeReason.HostEnded);
            Eq(ByeReason.HostEnded, TogetherMessages.ReadBye(b.AsSpan(0, n)));
            // Malformed bodies are refused, not read past their end.
            False(TogetherMessages.ReadWelcome(b.AsSpan(0, 3), out _, out _), "short welcome");
            False(TogetherMessages.ReadVideo(new byte[] { 3, 0, 0, 0, 1, 0, 5, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, out _), "index past count");
        }),
        ("Together: a video fragment fits a 1280-byte path", () =>
        {
            int datagram = TogetherCipher.Overhead + TogetherMessages.VideoHeaderSize + TogetherMessages.MaxFragmentPayload;
            True(datagram + 28 <= 1280, $"{datagram} + 28 bytes of IP/UDP");
        }),
        ("Together: encryption round trip, and each direction only opens the other's packets", () =>
        {
            using var host = new TogetherCipher(Key, 7, 99, isHost: true);
            using var guest = new TogetherCipher(Key, 7, 99, isHost: false);
            var pkt = new byte[200]; var body = new byte[200];
            int n = guest.Seal("hello"u8, pkt);
            True(host.Open(pkt.AsSpan(0, n), body, out int len) && len == 5, "host opens the guest's packet");
            Eq("hello", System.Text.Encoding.UTF8.GetString(body, 0, len));
            n = host.Seal("back"u8, pkt);
            True(guest.Open(pkt.AsSpan(0, n), body, out len), "guest opens the host's packet");
            False(host.Open(pkt.AsSpan(0, n), body, out _), "the host refuses its own packet reflected back");
        }),
        ("Together: tampered, replayed, foreign and old packets are refused", () =>
        {
            using var host = new TogetherCipher(Key, 7, 99, isHost: true);
            using var guest = new TogetherCipher(Key, 7, 99, isHost: false);
            var body = new byte[200];
            byte[] Sealed(string text) { var p = new byte[100]; int n = guest.Seal(System.Text.Encoding.UTF8.GetBytes(text), p); return p[..n]; }

            var good = Sealed("press A");
            foreach (int at in new[] { 1, 5, 12, TogetherCipher.HeaderSize, good.Length - 1 })
            {
                var bad = (byte[])good.Clone();
                bad[at] ^= 0x01;
                False(host.Open(bad, body, out _), $"a flipped bit at byte {at}");
            }
            True(host.Open(good, body, out _), "the untouched packet still opens");
            False(host.Open(good, body, out _), "the same packet again (replay)");

            // Another session's key, another connection id.
            using var other = new TogetherCipher(Enumerable.Repeat((byte)9, 32).ToArray(), 7, 99, isHost: false);
            var p2 = new byte[100]; int n2 = other.Seal("x"u8, p2);
            False(host.Open(p2.AsSpan(0, n2), body, out _), "another key");
            using var conn2 = new TogetherCipher(Key, 7, 100, isHost: false);
            n2 = conn2.Seal("x"u8, p2);
            False(host.Open(p2.AsSpan(0, n2), body, out _), "another connection");

            // Reordering inside the window is fine (each packet once); far behind the newest is not.
            var a = Sealed("1"); var bb = Sealed("2"); var c = Sealed("3");
            True(host.Open(c, body, out _), "3 first");
            True(host.Open(a, body, out _), "then 1 (late, inside the window)");
            True(host.Open(bb, body, out _), "then 2");
            False(host.Open(a, body, out _), "1 again");
            var old = Sealed("old");
            for (int i = 0; i < TogetherCipher.ReplayWindow + 5; i++) host.Open(Sealed("x"), body, out _);
            False(host.Open(old, body, out _), "older than the replay window");
            True(host.Rejected >= 9, "rejections counted");
        }),
        ("Together: a rejoining connection never reuses a counter", () =>
        {
            using var first = new TogetherCipher(Key, 7, 5, isHost: true);
            var p = new byte[64];
            first.Seal("a"u8, p);
            ulong c1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(10));
            Thread.Sleep(2);
            using var again = new TogetherCipher(Key, 7, 5, isHost: true);
            again.Seal("b"u8, p);
            ulong c2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(10));
            True(c2 > c1, $"second cipher counts on ({c1} then {c2})");
        }),
        ("Together: frames reassemble in order, with no loss", () =>
        {
            var a = new FrameAssembler();
            var output = new List<AssembledFrame>(); var nacks = new List<NackEntry>();
            var frames = Enumerable.Range(0, 20).Select(i => Frame(i, 200 + i * 517)).ToList();
            for (uint id = 0; id < frames.Count; id++)
                foreach (var part in Fragments(100 + id, frames[(int)id], key: id % 10 == 0)) Feed(a, part, id * 16, output, nacks);
            Eq(20, output.Count);
            Eq(0, nacks.Count);
            for (int i = 0; i < 20; i++) { Eq(100u + (uint)i, output[i].FrameId); True(output[i].Data.AsSpan().SequenceEqual(frames[i]), $"frame {i} intact"); }
        }),
        ("Together: frames before the first keyframe are dropped", () =>
        {
            var a = new FrameAssembler();
            var output = new List<AssembledFrame>(); var nacks = new List<NackEntry>();
            for (uint id = 0; id < 5; id++) foreach (var part in Fragments(id, Frame((int)id, 500), key: id == 3)) Feed(a, part, id, output, nacks);
            Eq(2, output.Count);
            Eq(3u, output[0].FrameId); True(output[0].Keyframe, "starts on the keyframe");
            True(a.TakeKeyframeRequest(), "asked for a keyframe on joining");
        }),
        ("Together: a lost piece is NACKed once and the resend completes the frame", () =>
        {
            var a = new FrameAssembler { RttMs = 10 };
            var output = new List<AssembledFrame>(); var nacks = new List<NackEntry>();
            foreach (var part in Fragments(1, Frame(1, 2000), key: true)) Feed(a, part, 0, output, nacks);
            var p2 = Fragments(2, Frame(2, 5000), key: false);
            Feed(a, p2[0], 16, output, nacks);
            Feed(a, p2[2], 16, output, nacks); // [1] lost
            Eq(1, nacks.Count); Eq(new NackEntry(2, 1), nacks[0]);
            Feed(a, p2[3], 17, output, nacks); Feed(a, p2[4], 17, output, nacks);
            Eq(1, nacks.Count); // not asked again
            Eq(1, output.Count);
            Feed(a, p2[1], 25, output, nacks); // the resend
            Eq(2, output.Count);
            True(output[1].Data.AsSpan().SequenceEqual(Frame(2, 5000)), "frame 2 intact");
            Eq(1L, a.FragmentsRecovered);
        }),
        ("Together: a lost tail and a whole lost frame are both noticed", () =>
        {
            var a = new FrameAssembler();
            var output = new List<AssembledFrame>(); var nacks = new List<NackEntry>();
            foreach (var part in Fragments(1, Frame(1, 100), key: true)) Feed(a, part, 0, output, nacks);
            var f2 = Fragments(2, Frame(2, 3000), key: false);
            Feed(a, f2[0], 16, output, nacks); Feed(a, f2[1], 16, output, nacks); // [2] lost: nothing shows it yet
            Eq(0, nacks.Count);
            // Frame 3 lost entirely; frame 4 arrives.
            foreach (var part in Fragments(4, Frame(4, 100), key: false)) Feed(a, part, 48, output, nacks);
            True(nacks.Contains(new NackEntry(2, 2)), "tail of frame 2");
            True(nacks.Contains(new NackEntry(3, NackEntry.WholeFrame)), "the whole of frame 3");
            Feed(a, f2[2], 50, output, nacks);
            foreach (var part in Fragments(3, Frame(3, 100), key: false)) Feed(a, part, 52, output, nacks);
            Eq("1,2,3,4", string.Join(",", output.Select(o => o.FrameId)));
        }),
        ("Together: a resend that's lost too is asked for again before giving up", () =>
        {
            var a = new FrameAssembler { RttMs = 10 };
            var output = new List<AssembledFrame>(); var nacks = new List<NackEntry>();
            foreach (var part in Fragments(1, Frame(1, 100), key: true)) Feed(a, part, 0, output, nacks);
            var f2 = Fragments(2, Frame(2, 3000), key: false);
            Feed(a, f2[0], 16, output, nacks); Feed(a, f2[2], 16, output, nacks);
            nacks.Clear();
            a.Tick(16 + a.GiveUpMs + 1, output, nacks);
            Eq(1, nacks.Count); Eq(new NackEntry(2, 1), nacks[0]); // asked again, not given up
            False(a.WaitingForKeyframe, "not given up yet");
            Feed(a, f2[1], 90, output, nacks);
            Eq(2, output.Count);
            Eq(0L, a.FramesGivenUp);
        }),
        ("Together: a frame that never comes back is given up, and frames wait for a keyframe", () =>
        {
            var a = new FrameAssembler { RttMs = 10 };
            var output = new List<AssembledFrame>(); var nacks = new List<NackEntry>();
            foreach (var part in Fragments(1, Frame(1, 100), key: true)) Feed(a, part, 0, output, nacks);
            a.TakeKeyframeRequest();
            var f2 = Fragments(2, Frame(2, 3000), key: false);
            Feed(a, f2[0], 16, output, nacks); Feed(a, f2[2], 16, output, nacks); // [1] lost for good
            foreach (var part in Fragments(3, Frame(3, 100), key: false)) Feed(a, part, 32, output, nacks);
            a.Tick(30, output, nacks);
            Eq(1, output.Count); False(a.TakeKeyframeRequest(), "still inside the resend time");
            // Asked again once (a P frame gets two rounds), then given up.
            nacks.Clear();
            a.Tick(16 + a.GiveUpMs + 1, output, nacks);
            Eq(1, nacks.Count);
            False(a.TakeKeyframeRequest(), "the second round goes first");
            a.Tick(16 + 2 * a.GiveUpMs + 2, output, nacks);
            True(a.TakeKeyframeRequest(), "keyframe asked for after about a round trip");
            True(a.WaitingForKeyframe, "waiting for a keyframe");
            foreach (var part in Fragments(4, Frame(4, 100), key: false)) Feed(a, part, 80, output, nacks);
            Eq(1, output.Count); // frame 3 and 4 depend on the lost one: dropped
            foreach (var part in Fragments(5, Frame(5, 2500), key: true)) Feed(a, part, 96, output, nacks);
            Eq(2, output.Count); Eq(5u, output[1].FrameId);
            foreach (var part in Fragments(6, Frame(6, 100), key: false)) Feed(a, part, 112, output, nacks);
            Eq(3, output.Count);
            True(a.FramesGivenUp == 1 && a.FramesDropped >= 3, $"given up {a.FramesGivenUp}, dropped {a.FramesDropped}");
        }),
        ("Together: random loss with resends delivers every frame intact", () =>
        {
            var rnd = new Random(3);
            var a = new FrameAssembler { RttMs = 5 };
            var output = new List<AssembledFrame>(); var nacks = new List<NackEntry>();
            var frames = Enumerable.Range(0, 300).Select(i => Frame(i, i % 60 == 0 ? 40_000 : 300 + rnd.Next(9000))).ToList();
            var cache = new Dictionary<uint, byte[][]>();
            double now = 0;
            var resend = new List<(double at, byte[] body)>();
            for (uint id = 0; id < frames.Count; id++)
            {
                var parts = cache[id] = Fragments(id, frames[(int)id], key: id % 60 == 0);
                foreach (var part in parts)
                {
                    now += 0.2;
                    foreach (var r in resend.Where(r => r.at <= now).ToList()) { Feed(a, r.body, now, output, nacks); resend.Remove(r); }
                    if (rnd.NextDouble() < 0.05) continue; // 5% lost on the way
                    nacks.Clear();
                    Feed(a, part, now, output, nacks);
                    foreach (var n in nacks)
                        foreach (var body in n.Index == NackEntry.WholeFrame ? cache[n.FrameId] : [cache[n.FrameId][n.Index]])
                            resend.Add((now + 5, body)); // the resend arrives a round trip later (and isn't lost)
                }
                a.Tick(now, output);
            }
            for (int k = 0; k < 20; k++) { now += 5; foreach (var r in resend.ToList()) { Feed(a, r.body, now, output, nacks); resend.Remove(r); } a.Tick(now, output); }
            Eq(frames.Count, output.Count);
            for (int i = 0; i < frames.Count; i++) True(output[i].Data.AsSpan().SequenceEqual(frames[i]), $"frame {i} intact");
            True(a.FragmentsRecovered > 50, $"{a.FragmentsRecovered} recovered");
            Eq(0L, a.FramesGivenUp);
        }),
        ("Together: invite and reply codes round trip, forgivingly", () =>
        {
            var cands = new List<Candidate>
            {
                new(CandidateKind.Upnp, new IPEndPoint(IPAddress.Parse("81.2.69.160"), 50123)),
                new(CandidateKind.Stun, new IPEndPoint(IPAddress.Parse("81.2.69.160"), 61001)),
                new(CandidateKind.Lan, new IPEndPoint(IPAddress.Parse("192.168.1.20"), 50123)),
                new(CandidateKind.Lan, new IPEndPoint(IPAddress.Parse("fd00::1"), 50123)),
            };
            var inv = new TogetherInvite(0x01020304, Key, cands);
            string code = inv.Encode();
            True(code.StartsWith("VX1-"), code);
            True(code.Length < 140, $"compact ({code.Length} characters)");
            foreach (var pasted in new[] { code, code.ToLowerInvariant(), "join me: " + code + " thanks!", code[..40] + "\n" + code[40..], "VX1-" + code[4..].Replace('0', 'O').Replace('1', 'l') })
            {
                var back = TogetherInvite.TryParse(pasted, out var why);
                True(back is not null, $"parses \"{pasted}\": {why}");
                Eq(inv.SessionId, back!.SessionId);
                True(back.Key.AsSpan().SequenceEqual(Key), "key");
                Eq(cands.Count, back.Candidates.Count);
                for (int i = 0; i < cands.Count; i++) Eq(cands[i], back.Candidates[i]);
            }
            var broken = code[..^3] + (code[^3] == 'A' ? 'B' : 'A') + code[^2..];
            True(TogetherInvite.TryParse(broken, out var p1) is null && p1.Contains("incomplete"), p1);
            True(TogetherInvite.TryParse(code[..60], out var p2) is null && p2.Length > 0, p2);
            True(TogetherInvite.TryParse("hello", out var p3) is null && p3.Contains("VX1-"), p3);

            var reply = new TogetherReply(0x1122334455667788, cands.Take(2).ToList());
            string rc = reply.Encode();
            True(rc.StartsWith("VXR1-"), rc);
            var rb = TogetherReply.TryParse(rc, out var why2);
            True(rb is not null, why2);
            Eq(reply.ConnectionId, rb!.ConnectionId); Eq(2, rb.Candidates.Count);
            True(TogetherInvite.TryParse(rc, out var p4) is null && p4.Contains("reply code"), "a reply code pasted as an invite says so: " + p4);
            True(TogetherReply.TryParse(code, out var p5) is null && p5.Contains("invite"), p5);
        }),
        ("Together: STUN response parsed (XOR-MAPPED-ADDRESS)", () =>
        {
            var req = NatTraversal.StunRequest(out var tx);
            True(NatTraversal.IsStun(req), "request looks like STUN");
            // Binding success for 192.0.2.1:32853, as in RFC 5769's IPv4 sample (XOR'd with the magic cookie).
            var resp = new byte[32];
            resp[0] = 0x01; resp[1] = 0x01; resp[3] = 12;
            resp[4] = 0x21; resp[5] = 0x12; resp[6] = 0xA4; resp[7] = 0x42;
            tx.CopyTo(resp, 8);
            resp[20] = 0x00; resp[21] = 0x20; resp[23] = 8;
            resp[25] = 0x01;
            resp[26] = 0xA1 ^ 0x21 ^ 0x80 ^ 0x80; resp[27] = 0x47; // port 0x8055 ^ 0x2112 = 0xA147
            resp[26] = 0xA1;
            resp[28] = 0xE1; resp[29] = 0x12; resp[30] = 0xA6; resp[31] = 0x43; // 192.0.2.1 ^ 2112A442
            True(NatTraversal.TryParseStunResponse(resp, out var got, out var mapped), "parsed");
            True(got.SequenceEqual(tx), "transaction id");
            Eq(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 32853), mapped);
        }),
        ("Together: guest pads go out in their slot, untouched by local mapping and deadzones", () =>
        {
            var pads = new Pads();
            pads.C[0] = true; pads.S[0] = new GamepadState(0, GamepadButtons.A, 0.02f, 0, 0, 0, 0, 0);
            GamepadState[] last = [];
            var changes = new List<(int, bool)>();
            var pump = new InputPump(pads, new InputOptions { Mapping = ControllerMapping.Parse("A=B"), StickDeadzone = 0.1f }, f => last = f, (i, on) => changes.Add((i, on)));
            pump.Step(0);
            Eq(1, last.Length); Eq(GamepadButtons.B, last[0].Buttons); Eq(0f, last[0].LeftX); // local: remapped, deadzone
            True(pump.IsLocalPad(0) && !pump.IsLocalPad(1), "pad 0 is local");

            pump.SetRemotePad(1, new GamepadState(0, GamepadButtons.A, 0.02f, 0, 0, 0, 0.01f, 0));
            True(pump.IsRemote(1) && pump.HasRemote, "slot 1 is remote");
            pump.Step(1);
            Eq(2, last.Length); // both pads in the same packet
            Eq((byte)1, last[1].Index);
            Eq(GamepadButtons.A, last[1].Buttons); // not remapped
            Near(0.02, last[1].LeftX, 1e-6);        // no deadzone
            Near(0.01, last[1].LeftTrigger, 1e-6);
            True(changes.Contains((1, true)), "the console is told a controller came");
            False(pump.IsLocalPad(1), "slot 1 isn't local");

            pump.SetRemotePad(1, new GamepadState(1, GamepadButtons.Y, 0, 0, 0, 0, 0, 0));
            pump.Step(2);
            Eq(GamepadButtons.Y, last.First(s => s.Index == 1).Buttons);

            // Released: a neutral pad, then the console is told it went.
            var sent = new List<GamepadState[]>();
            var pump2 = new InputPump(new Pads(), new InputOptions(), f => sent.Add(f), (i, on) => changes.Add((i + 10, on)));
            pump2.SetRemotePad(2, new GamepadState(0, GamepadButtons.X, 1, 0, 0, 0, 0, 0));
            pump2.Step(0);
            pump2.SetRemotePad(2, null);
            pump2.Step(1);
            True(sent.Any(f => f.Length == 1 && f[0].Index == 2 && f[0].Buttons == GamepadButtons.None && f[0].LeftX == 0), "neutral sent on release");
            True(changes.Contains((12, false)), "disconnect reported");
        }),
        ("Together: stopping the pump leaves no guest button held", () =>
        {
            var sent = new List<GamepadState[]>();
            var pump = new InputPump(new Pads(), new InputOptions { PollHz = 1000 }, f => { lock (sent) sent.Add(f); });
            pump.SetRemotePad(1, new GamepadState(0, GamepadButtons.A, 0, 0, 0, 0, 1, 0));
            pump.Start();
            Thread.Sleep(30);
            pump.Stop();
            GamepadState[] lastFrame; lock (sent) lastFrame = sent[^1];
            True(lastFrame.Any(s => s.Index == 1) && lastFrame.All(s => s.Buttons == GamepadButtons.None && s.LeftTrigger == 0), "the last thing sent for slot 1 is neutral");
            False(pump.HasRemote, "remote pads cleared");
        }),
        ("Together: the host's menu only sees the host's pads, and guests play on while it's open", () =>
        {
            var remote = new HashSet<int> { 1 };
            var router = new HostPadRouter(remote.Contains);
            var seen = new List<GamepadState[]>();
            bool menuOpen = false;
            router.Menu = f => { seen.Add(f); return menuOpen; };
            var local = new GamepadState(0, GamepadButtons.View | GamepadButtons.Menu, 0, 0, 0, 0, 0, 0);
            var guest = new GamepadState(1, GamepadButtons.View | GamepadButtons.Menu | GamepadButtons.A, 0.5f, 0, 0, 0, 0, 0);

            var outFrame = router.Route([local, guest])!;
            Eq(2, outFrame.Length);
            True(seen.All(f => f.All(s => s.Index == 0)), "the menu never saw the guest's pad");

            // The guest alone pressing View + Menu: the menu isn't even asked.
            seen.Clear();
            router.Route([new GamepadState(0, 0, 0, 0, 0, 0, 0, 0), guest]);
            True(seen.Count == 1 && seen[0].Length == 1 && seen[0][0].Index == 0, "only the local pad was shown to the menu");
            seen.Clear();
            router.Route([guest]);
            Eq(0, seen.Count);

            menuOpen = true;
            outFrame = router.Route([local, guest])!;
            Eq(GamepadButtons.None, outFrame[0].Buttons);                 // the host's pad is held neutral
            Eq(guest.Buttons, outFrame[1].Buttons); Near(0.5, outFrame[1].LeftX, 1e-6); // the guest's goes through
            outFrame = router.Route([local, guest with { Buttons = GamepadButtons.B }])!;
            Eq(GamepadButtons.B, outFrame[1].Buttons);

            // No guests: the old behaviour (one neutral frame, then nothing while the menu is open).
            var solo = new HostPadRouter(_ => false) { Menu = _ => true };
            Eq(GamepadButtons.None, solo.Route([local])![0].Buttons);
            True(solo.Route([local]) is null, "nothing more while the menu holds the pad");
        }),
        ("Together: host and guest over loopback, with 2% loss each way", () =>
        {
            var joined = new ManualResetEventSlim();
            var inputs = new List<GamepadState>();
            int keyframes = 0;
            double? cap = null;
            using var host = new TogetherHost(new TogetherHostOptions { HostName = "Alex", Stun = false, PortMapping = false, Loopback = true, SimulatedLossPct = 2 },
                slot => slot == 0);
            host.GuestInput += (slot, s) => { lock (inputs) inputs.Add(s); };
            host.KeyframeNeeded += () => Interlocked.Increment(ref keyframes);
            host.CapChanged += k => cap = k;
            var ready = new ManualResetEventSlim();
            host.InviteReady += _ => ready.Set();
            host.Start();
            True(ready.Wait(5000), "invite ready");
            var invite = TogetherInvite.TryParse(host.Invite, out var why);
            True(invite is not null, why);

            using var guest = new TogetherClient(invite!, new TogetherClientOptions { Name = "Sam", Stun = false, SimulatedLossPct = 2 });
            var got = new List<AssembledFrame>();
            WelcomeMsg welcome = default;
            guest.Joined += w => { welcome = w; joined.Set(); };
            guest.Frame += f => { lock (got) got.Add(f); };
            guest.Start();
            True(joined.Wait(5000), "joined");
            Eq((byte)1, welcome.Slot); Eq("Alex", welcome.HostName);
            Eq(15_000.0, cap);
            True(keyframes >= 1, "a keyframe was asked for when the guest joined");
            Eq(1, host.Guests.Count); Eq("Sam", host.Guests[0].Name);

            // 3 s of 60 fps: a keyframe every second and whenever the guest asks (as the console does), P frames between.
            int wantKey = 0;
            host.KeyframeNeeded += () => wantKey = 1;
            var frames = Enumerable.Range(0, 180).Select(i => Frame(i, 1500 + i * 7)).ToList();
            for (int i = 0; i < frames.Count; i++)
            {
                bool key = i % 60 == 0 || Interlocked.Exchange(ref wantKey, 0) == 1;
                if (key) frames[i] = Frame(i, 30_000);
                host.SendVideo(frames[i], key, (uint)(i * 1500));
                Thread.Sleep(16);
            }
            Thread.Sleep(400);
            guest.SendInput(new GamepadState(0, GamepadButtons.A | GamepadButtons.Nexus, 0, 0, 0, 0, 0, 0));
            for (int i = 0; i < 20 && inputs.Count == 0; i++) Thread.Sleep(25);
            lock (got)
            {
                // A resend that's lost too costs a keyframe, and the host passes those on once a second: allow for one or two.
                True(got.Count >= 110, $"{got.Count} of {frames.Count} frames arrived (2% loss each way, resends on; " +
                    $"recovered {guest.Assembler.FragmentsRecovered}, given up {guest.Assembler.FramesGivenUp})");
                foreach (var f in got) True(f.Data.AsSpan().SequenceEqual(frames[(int)f.FrameId - 1]), $"frame {f.FrameId} intact"); // the host numbers frames from 1
                for (int i = 1; i < got.Count; i++) True(got[i].FrameId > got[i - 1].FrameId, "in order");
            }
            True(guest.Assembler.FragmentsRecovered > 0, "losses repaired by resend");
            lock (inputs)
            {
                True(inputs.Count > 0, "input arrived");
                Eq((byte)1, inputs[^1].Index);                     // put in the guest's slot
                Eq(GamepadButtons.A, inputs[^1].Buttons);          // Xbox button removed
            }
            var left = new ManualResetEventSlim();
            host.GuestLeft += (_, _) => left.Set();
            guest.Leave();
            True(left.Wait(2000), "the host saw the guest leave");
            Eq(0, host.Guests.Count);
            Eq(null, cap); // bitrate back to normal
        }),        ("Play together: the launcher's stdin commands parse, and refuse anything else", () =>
        {
            Eq(new TogetherCommand(TogetherCommandKind.Share), TogetherCommand.TryParse("together share"));
            Eq(new TogetherCommand(TogetherCommandKind.Stop), TogetherCommand.TryParse("  together stop \r"));
            Eq(new TogetherCommand(TogetherCommandKind.Remove, 2), TogetherCommand.TryParse("together remove 2"));
            Eq(new TogetherCommand(TogetherCommandKind.Reply, Code: "VXR1-ABC DEF"), TogetherCommand.TryParse("together reply VXR1-ABC DEF"));
            foreach (var bad in (string?[])[null, "", "stop", "resume", "together", "together ", "together dance", "together share now",
                         "together remove", "together remove 0", "together remove 4", "together remove -1", "together remove x", "together reply", "together reply  ",
                         "togethershare", "Together share"])
                Eq(null, TogetherCommand.TryParse(bad));
            // Every command makes a line that parses back to itself; a wrapped reply code goes on one line.
            foreach (var c in (TogetherCommand[])[new(TogetherCommandKind.Share), new(TogetherCommandKind.Stop), new(TogetherCommandKind.Remove, 3)])
                Eq(c, TogetherCommand.TryParse(c.ToLine()));
            var wrapped = new TogetherCommand(TogetherCommandKind.Reply, Code: "VXR1-ABCD\r\nEFGH\tIJ  ").ToLine();
            False(wrapped.Contains('\n') || wrapped.Contains('\r'), "one line");
            Eq("together reply VXR1-ABCD EFGH IJ", wrapped);
            Eq("VXR1-ABCD EFGH IJ", TogetherCommand.TryParse(wrapped)!.Code);
            // A real reply code survives the trip and still opens.
            var reply = new TogetherReply(0x1234_5678_9ABC_DEF0UL, [new Candidate(CandidateKind.Lan, System.Net.IPEndPoint.Parse("192.168.1.20:50000"))]).Encode();
            var back = TogetherCommand.TryParse(new TogetherCommand(TogetherCommandKind.Reply, Code: reply[..20] + "\n" + reply[20..]).ToLine())!;
            Eq(0x1234_5678_9ABC_DEF0UL, TogetherReply.TryParse(back.Code, out _)!.ConnectionId);
        }),
        ("Play together: status round-trips with guests' ping and loss, and older lines still read", () =>
        {
            var st = new TogetherStatus
            {
                Role = "host", Players = 3, Guests = ["Sam", "Alex"], Invite = "VX1-XYZ", CapMbps = 12.5, Note = "That code is incomplete", NoteIsProblem = true,
                People = [new TogetherPlayer { Slot = 1, Name = "Sam", PingMs = 23.5, LossPct = 0.2 }, new TogetherPlayer { Slot = 2, Name = "Alex" }],
            };
            var e = PlayerEvent.TryParse(new PlayerEvent { Type = "together", Together = st }.ToLine())!;
            var t = e.Together!;
            Eq("host", t.Role); Eq(3, t.Players); Eq("VX1-XYZ", t.Invite); Eq(12.5, t.CapMbps); Eq(true, t.NoteIsProblem);
            Eq("Sam,Alex", string.Join(",", t.Guests));
            Eq(2, t.People!.Length);
            Eq(st.People[0], t.People[0]);
            True(double.IsNaN(t.People[1].PingMs), "no ping yet stays NaN");
            // A line from before People existed: still reads, with no details.
            var old = PlayerEvent.TryParse(PlayerEvent.Prefix + "{\"Type\":\"together\",\"Together\":{\"Role\":\"host\",\"Players\":2,\"Guests\":[\"Sam\"]}}")!.Together!;
            Eq(2, old.Players); Eq(null, old.People); Eq(null, old.CapMbps); Eq(false, old.NoteIsProblem); Eq(-1, old.Slot);
        }),
        ("Play together: launcher commands act on the share on the window's thread, and every change sends a status", () =>
        {
            var statuses = new List<TogetherStatus>();
            using var share = new TogetherShare(new AppSettings(), "Alex", null, () => null, () => { }, _ => { }, _ => { },
                o => o with { Port = 0, Stun = false, PortMapping = false, Loopback = true, GatherTimeout = TimeSpan.FromMilliseconds(50) });
            share.Changed += st => { lock (statuses) statuses.Add(st); };
            share.ClipboardSet = _ => true; share.ClipboardGet = () => "";
            share.Announce();
            Eq(0, statuses[^1].Players); Eq("host", statuses[^1].Role);
            share.Run(new TogetherCommand(TogetherCommandKind.Share));
            True(!share.Sharing, "waits for the window's thread");
            share.OnMainThread();
            True(share.Sharing, "sharing after the window's turn");
            Eq(1, statuses[^1].Players);
            // The invite arrives a moment later, with its own status.
            var until = DateTime.UtcNow.AddSeconds(5);
            while (share.Invite is null && DateTime.UtcNow < until) Thread.Sleep(20);
            share.OnMainThread();
            True(share.Invite is not null, "invite made");
            True(statuses.Any(s => s.Invite == share.Invite), "a status carried the invite");
            // A bad reply code: the reason comes back in the status, for whichever side pasted it.
            share.Run(new TogetherCommand(TogetherCommandKind.Reply, Code: "VXR1-NOTACODE"));
            share.OnMainThread();
            True(statuses[^1].NoteIsProblem && statuses[^1].Note is { Length: > 0 }, "problem note");
            // Removing a slot nobody has is harmless and still answers.
            int before = statuses.Count;
            share.Run(new TogetherCommand(TogetherCommandKind.Remove, 2));
            share.OnMainThread();
            True(statuses.Count > before, "answered");
            Eq(1, statuses[^1].Players);
            // Share again while sharing: no second host, just the status.
            var invite = share.Invite;
            share.Run(new TogetherCommand(TogetherCommandKind.Share));
            share.OnMainThread();
            Eq(invite, share.Invite);
            share.Run(new TogetherCommand(TogetherCommandKind.Stop));
            share.OnMainThread();
            True(!share.Sharing, "stopped");
            Eq(0, statuses[^1].Players); Eq(null, statuses[^1].Invite); Eq(null, statuses[^1].Note);
        }),
    ];

    private static void False(bool c, string what) { if (c) throw new Exception("expected false: " + what); }
}
