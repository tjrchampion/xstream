namespace XStream.Sim;

/// <summary>Network conditions at a moment in time.</summary>
public readonly record struct LinkState(double CapacityKbps, double LossP, double JitterMs, bool Outage);

/// <summary>
/// A repeatable network trace. Values are modelled on common home-streaming failure modes,
/// not captured from a real console.
/// </summary>
public sealed class Scenario
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public double DurationMs { get; init; } = 180_000;
    public double BaseOwdMs { get; init; } = 4;
    /// <summary>Times when the network path changes (Wi-Fi roam, IP change). Old path stops working.</summary>
    public List<double> PathChanges { get; } = new();
    private readonly List<(double start, double end, Func<double, LinkState, LinkState> apply)> _events = new();
    public required LinkState Baseline { get; init; }
    public Func<double, LinkState, LinkState>? Drift { get; init; }

    public void AddEvent(double start, double dur, Func<double, LinkState, LinkState> apply) => _events.Add((start, start + dur, apply));

    public LinkState At(double t)
    {
        var s = Baseline;
        if (Drift is not null) s = Drift(t, s);
        foreach (var (a, b, f) in _events) if (t >= a && t < b) s = f(t - a, s);
        return s;
    }

    public static IReadOnlyList<Scenario> All(int seed) => new[]
    {
        Wired(seed), WiredHiccup(seed), LossBursts(seed), BusyWifi(seed), Upload(seed), Dropouts(seed), Handheld(seed),
    };

    // Seen live on a wired Series X session: one heavy loss burst 3 s in, then a clean link. The cap was cut
    // to ~4 Mbps and took ~14 s to climb back, with nothing wrong on the network.
    private static Scenario WiredHiccup(int seed)
    {
        var r = new Random(seed * 31 + 5);
        var s = new Scenario
        {
            Id = "hiccup", Name = "Wired, two short hiccups",
            Description = "Ethernet with one heavy loss burst early in the session and another later. The link is clean otherwise, so the picture should be back to full quality within a few seconds.",
            Baseline = new(60_000, 0.0001, 1, false), BaseOwdMs = 2,
        };
        foreach (double at in new[] { 3_000 + r.NextDouble() * 1_000, 90_000 + r.NextDouble() * 20_000 })
            s.AddEvent(at, 300 + r.NextDouble() * 300, (_, l) => l with { CapacityKbps = 3_000, LossP = 0.5, JitterMs = 20 });
        return s;
    }

    private static Scenario Wired(int seed) => new()
    {
        Id = "wired", Name = "Wired home network",
        Description = "Console and player both on Ethernet. The control case: nothing should get worse.",
        Baseline = new(60_000, 0.0001, 1, false), BaseOwdMs = 2,
    };

    // Loss without congestion: the path drops packets in short bursts (a flaky switch port, powerline adapter,
    // brief interference) but its capacity never changes. Cutting deep here only slows the recovery keyframe.
    private static Scenario LossBursts(int seed)
    {
        var r = new Random(seed * 31 + 6);
        var s = new Scenario
        {
            Id = "bursts", Name = "Loss bursts, no congestion",
            Description = "Full-speed link that drops 40-80% of packets for 200-500 ms every 20-40 s, then is fine again.",
            Baseline = new(60_000, 0.0001, 1, false), BaseOwdMs = 3,
        };
        for (double t = 8_000 + r.NextDouble() * 5_000; t < s.DurationMs; t += 20_000 + r.NextDouble() * 20_000)
        {
            double dur = 200 + r.NextDouble() * 300, loss = 0.4 + r.NextDouble() * 0.4;
            s.AddEvent(t, dur, (_, l) => l with { LossP = loss, JitterMs = 2 });
        }
        return s;
    }

    private static Scenario BusyWifi(int seed)
    {
        var r = new Random(seed * 31 + 1);
        var s = new Scenario
        {
            Id = "wifi", Name = "Busy Wi-Fi (interference)",
            Description = "Player on 5 GHz Wi-Fi with neighbours, microwaves and other devices. Bandwidth collapses for 0.5-3 s every 8-20 s.",
            Baseline = new(35_000, 0.002, 4, false), BaseOwdMs = 4,
        };
        for (double t = 5_000; t < s.DurationMs; t += 8_000 + r.NextDouble() * 12_000)
        {
            double dur = 500 + r.NextDouble() * 2_500, cap = 5_000 + r.NextDouble() * 7_000, loss = 0.01 + r.NextDouble() * 0.04;
            s.AddEvent(t, dur, (_, l) => l with { CapacityKbps = cap, LossP = loss, JitterMs = 15 + loss * 200 });
        }
        return s;
    }

    private static Scenario Upload(int seed)
    {
        var r = new Random(seed * 31 + 2);
        double phase = r.NextDouble() * 10;
        var s = new Scenario
        {
            Id = "away", Name = "Away from home (home upload limited)",
            Description = "Playing at a friend's house. Your home broadband upload (~12 Mbps) is the bottleneck, and someone at home starts uploading photos at times.",
            Baseline = new(12_000, 0.003, 6, false), BaseOwdMs = 20,
            Drift = (t, l) => l with { CapacityKbps = 12_000 + 2_000 * Math.Sin(t / 9_000 + phase) },
        };
        for (double t = 20_000; t < s.DurationMs; t += 35_000 + r.NextDouble() * 15_000)
        {
            double dur = 6_000 + r.NextDouble() * 8_000, share = 3_000 + r.NextDouble() * 3_000;
            s.AddEvent(t, dur, (_, l) => l with { CapacityKbps = l.CapacityKbps - share, JitterMs = l.JitterMs + 6 });
        }
        return s;
    }

    private static Scenario Dropouts(int seed)
    {
        var r = new Random(seed * 31 + 3);
        var s = new Scenario
        {
            Id = "dropouts", Name = "Wi-Fi dropouts and roaming",
            Description = "Mesh Wi-Fi: two short signal losses (1.5 s and 4 s) and one switch to a different access point that changes the network path.",
            Baseline = new(40_000, 0.002, 4, false), BaseOwdMs = 4,
        };
        double o1 = 30_000 + r.NextDouble() * 10_000, o2 = 75_000 + r.NextDouble() * 10_000;
        s.AddEvent(o1, 1_500, (_, l) => l with { Outage = true });
        s.AddEvent(o2, 4_000, (_, l) => l with { Outage = true });
        s.PathChanges.Add(120_000 + r.NextDouble() * 10_000);
        return s;
    }

    private static Scenario Handheld(int seed)
    {
        var r = new Random(seed * 31 + 4);
        var s = new Scenario
        {
            Id = "handheld", Name = "Handheld moving around the house",
            Description = "ROG Ally carried from the living room to the bedroom and back: signal fades, jitter rises, short interference bursts, one access-point switch.",
            Baseline = new(40_000, 0.002, 4, false), BaseOwdMs = 5,
            Drift = (t, l) =>
            {
                // fade: 40 Mbps -> ~9 Mbps around t=60-110 s -> back
                double f = Math.Clamp((t - 40_000) / 20_000, 0, 1) * (1 - Math.Clamp((t - 110_000) / 20_000, 0, 1));
                return l with { CapacityKbps = 40_000 - 31_000 * f, LossP = 0.002 + 0.02 * f, JitterMs = 4 + 14 * f };
            },
        };
        for (double t = 45_000; t < 125_000; t += 6_000 + r.NextDouble() * 8_000)
        {
            double dur = 300 + r.NextDouble() * 1_200;
            s.AddEvent(t, dur, (_, l) => l with { CapacityKbps = l.CapacityKbps * 0.4, LossP = l.LossP + 0.04, JitterMs = l.JitterMs + 15 });
        }
        s.PathChanges.Add(85_000 + r.NextDouble() * 5_000);
        return s;
    }
}
