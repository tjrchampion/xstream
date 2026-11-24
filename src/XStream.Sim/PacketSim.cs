using XStream.Core.Rtp;
using XStream.Core.Stability;

namespace XStream.Sim;

/// <summary>A loss pattern for the packet simulator (applies to every transmission, resends included).</summary>
public sealed record LossPattern(string Id, string Name, Func<Random, double, bool> Lost);

public sealed record PacketRunResult(string Pattern, string Recovery, int Seed, double FrozenMs, int Freezes100, double WorstFreezeMs,
    int KeyframeRequests, long FramesLost, long NacksSent, long Resends, double AvgLatencyMs, int OutOfOrder, int Displayed);

/// <summary>
/// Packet-level simulation of the receive path. Unlike <see cref="Simulation"/> (which tests the engine's
/// decisions on 100 ms samples), this runs the real <see cref="VideoReceiver"/> (monitor, NACKs, frame assembly,
/// playout buffer, fast keyframe requests) and <see cref="StabilityEngine"/> against a modelled console:
/// 1440p60 at ~30 Mbps (1200-byte packets, ~52 per frame, keyframes ~250), packets paced at ~120 Mbps,
/// 2 ms each way, resends on NACK, a keyframe as the next frame after a request.
/// </summary>
public sealed class PacketSimulation(LossPattern loss, RecoveryOptions recovery, string recoveryName, int seed, double durationMs = 60_000,
    StabilityOptions? engineOptions = null)
{
    private const double FrameMs = 1000.0 / 60, OwdMs = 2, PacketGapMs = 0.08, StepMs = 0.05;
    private const int PFramePackets = 52, KeyPackets = 250, History = 4096;

    private readonly record struct Sent(ushort Seq, uint Ts, bool Marker, byte[] Payload, double FrameSentAt);

    public PacketRunResult Run()
    {
        var rng = new Random(seed * 7919 + loss.Id.Aggregate(17, (h, c) => h * 31 + c));
        var engine = new StabilityEngine(engineOptions ?? new StabilityOptions { Profile = StreamProfile.Balanced });
        var rx = new VideoReceiver(engine, recovery) { NackEnabled = true };
        var events = new PriorityQueue<Action, double>();
        var history = new Sent[History];
        var sentAt = new Dictionary<uint, double>();
        double t = 0;
        bool keyPending = true;
        int keyRequests = 0; long nacksSent = 0, resends = 0;

        double lastArrival = 0;
        void Deliver(Sent p, bool resend)
        {
            if (loss.Lost(rng, t)) return;
            // Jitter, but no reordering (a LAN path keeps packets in order); resends come back later on their own.
            double at = t + OwdMs + Math.Abs(Gauss(rng)) * 0.3 + (resend ? 0.5 : 0);
            if (!resend) { at = Math.Max(at, lastArrival + 0.005); lastArrival = at; }
            events.Enqueue(() => rx.OnPacket(1, p.Seq, p.Ts, p.Marker, p.Payload, t), at);
        }
        void AskKeyframe() { keyRequests++; events.Enqueue(() => keyPending = true, t + OwdMs); }
        rx.KeyframeNeeded = AskKeyframe;
        rx.SendRtcp = nack =>
        {
            nacksSent++;
            var seqs = NackGenerator.ParseNack(nack);
            events.Enqueue(() =>
            {
                foreach (var s in seqs)
                {
                    var h = history[s % History];
                    if (h.Payload is null || h.Seq != s) continue;
                    resends++;
                    Deliver(h, resend: true);
                }
            }, t + OwdMs);
        };

        // Console: frames of FU-A packets (NRI 3), sent paced from the frame's start.
        ushort seq = 0; int frame = 0;
        double nextFrame = 0, nextFast = 10, nextSample = 100, lastDue = 0;
        var displays = new List<(double at, double latency)>();
        uint lastShownTs = 0; bool anyShown = false; int outOfOrder = 0;

        for (; t < durationMs; t += StepMs)
        {
            if (t >= nextFrame)
            {
                bool key = keyPending; keyPending = false;
                int n = key ? KeyPackets : PFramePackets;
                uint ts = (uint)(frame * 1500);
                double start = nextFrame;
                sentAt[ts] = start;
                byte type = (byte)(key ? 5 : 1);
                for (int i = 0; i < n; i++)
                {
                    var pl = new byte[1200];
                    pl[0] = 0x7C; // NRI 3, FU-A
                    pl[1] = (byte)(type | (i == 0 ? 0x80 : 0) | (i == n - 1 ? 0x40 : 0));
                    var p = new Sent(seq, ts, i == n - 1, pl, start);
                    history[seq % History] = p;
                    seq++;
                    double at = start + i * PacketGapMs;
                    events.Enqueue(() => Deliver(p, resend: false), at);
                }
                frame++;
                nextFrame += FrameMs;
            }

            while (events.TryPeek(out _, out var at) && at <= t) events.Dequeue()();

            if (t >= nextFast) { rx.FastTick(t); nextFast += 10; }
            if (t >= nextSample)
            {
                var (s, _, lost) = rx.Sample(t, 2 * OwdMs);
                var d = engine.Update(s, lost);
                if (d.RequestKeyframe) AskKeyframe();
                rx.Frames.TargetDelayMs = d.BufferTargetMs;
                nextSample += 100;
            }

            // Decoder + screen: take frames as they come due (decode/present cost left out: same for both).
            while (rx.Frames.TryDequeue(t, out var au))
            {
                if (anyShown && (int)(au.RtpTimestamp - lastShownTs) <= 0) outOfOrder++;
                lastShownTs = au.RtpTimestamp; anyShown = true;
                double shownAt = Math.Max(t, lastDue);
                lastDue = shownAt;
                displays.Add((shownAt, shownAt - sentAt.GetValueOrDefault(au.RtpTimestamp, shownAt)));
                au.Release();
            }
        }

        // Freezes: time beyond the normal frame interval between pictures (after the first one).
        double frozen = 0, worst = 0; int freezes = 0;
        for (int i = 1; i < displays.Count; i++)
        {
            double gap = displays[i].at - displays[i - 1].at;
            if (gap > 2.5 * FrameMs) frozen += gap - FrameMs;
            if (gap > 100) freezes++;
            worst = Math.Max(worst, gap);
        }
        return new PacketRunResult(loss.Id, recoveryName, seed, frozen, freezes, worst, keyRequests, rx.Depacketizer.FramesLost,
            nacksSent, resends, displays.Count > 0 ? displays.Average(x => x.latency) : 0, outOfOrder, displays.Count);
    }

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    public static IReadOnlyList<LossPattern> Patterns()
    {
        // Gilbert-Elliott bursts: state shared per run through the closure's own small object.
        LossPattern Bursty(string id, string name, double enterBad, double leaveBad, double badLoss)
        {
            var bad = new bool[1];
            return new LossPattern(id, name, (r, _) =>
            {
                bad[0] = bad[0] ? r.NextDouble() >= leaveBad : r.NextDouble() < enterBad;
                return bad[0] && r.NextDouble() < badLoss;
            });
        }
        return
        [
            new("clean", "Wired, 0.02% random loss", (r, _) => r.NextDouble() < 0.0002),
            new("wifi1", "Wi-Fi, 1% random loss", (r, _) => r.NextDouble() < 0.01),
            new("wifi3", "Poor Wi-Fi, 3% random loss", (r, _) => r.NextDouble() < 0.03),
            Bursty("bursty", "Bursty Wi-Fi (short loss bursts, ~0.5% overall)", 0.001, 0.2, 0.6),
            new("hiccup400", "77% loss for 400 ms every 10 s (the live test)", (r, t) => t % 10_000 is >= 5_000 and < 5_400 && r.NextDouble() < 0.77),
            new("hiccup100", "30% loss for 100 ms every 5 s", (r, t) => t % 5_000 is >= 2_500 and < 2_600 && r.NextDouble() < 0.30),
        ];
    }
}
