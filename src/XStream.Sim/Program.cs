using XStream.Core.Rtp;
using System.Text.Json;
using XStream.Core.Stability;
using XStream.Sim;

// Usage: dotnet run --project src/XStream.Sim -- [--seeds 5] [--out results.json]
int seeds = 5;
string outPath = "results.json";
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--seeds") seeds = int.Parse(args[i + 1]);
    if (args[i] == "--out") outPath = args[i + 1];
}

// --packets: packet-level A/B of loss recovery (0.4.0 settings vs current), on the real receive path.
if (args.Contains("--packets"))
{
    var cur = new RecoveryOptions();
    var bal = new StabilityOptions { Profile = StreamProfile.Balanced };
    // 0.4.0 = the recovery settings it shipped with (engine's keyframe re-ask every 500 ms included).
    var recoveries = new List<(string name, RecoveryOptions o, StabilityOptions e)>
    {
        ("0.4.0", RecoveryOptions.Legacy, bal with { KeyframeKeepAskingMs = 500 }), ("current", cur, bal),
    };
    if (args.Contains("--variants"))
        recoveries.AddRange(new (string, RecoveryOptions, StabilityOptions)[]
        {
            ("backoff", cur with { NackBackoff = true }, bal),
            ("3 tries", cur with { NackMaxTries = 3 }, bal),
            ("keep 500", cur, bal with { KeyframeKeepAskingMs = 500 }),
            ("keep 150", cur, bal with { KeyframeKeepAskingMs = 150 }),
            ("slow-key", cur with { FastKeyframe = false }, bal),
            ("wait>=60", cur with { MinWaitMs = 60 }, bal),
            ("old-broken", cur with { CountOnlyLostFrames = false }, bal),
        });
    string? only = args.SkipWhile(a => a != "--pattern").Skip(1).FirstOrDefault();
    var patterns = PacketSimulation.Patterns().Select((p, i) => (p, i)).Where(x => only is null || only.Split(',').Contains(x.p.Id)).Select(x => x.i).ToList();
    var pjobs = new List<(int pattern, int r, int seed)>();
    int patternCount = patterns.Count;
    for (int seed = 0; seed < seeds; seed++)
        foreach (int pi in patterns)
            for (int ri = 0; ri < recoveries.Count; ri++) pjobs.Add((pi, ri, seed));
    Console.WriteLine($"Packet simulation: {pjobs.Count} runs of 60 s ({seeds} seeds x {patternCount} loss patterns x {recoveries.Count} recovery settings)...");
    var psw = System.Diagnostics.Stopwatch.StartNew();
    var pres = new PacketRunResult[pjobs.Count];
    Parallel.For(0, pjobs.Count, i =>
    {
        var (pi, ri, seed) = pjobs[i];
        var pattern = PacketSimulation.Patterns()[pi]; // fresh per run: bursty patterns keep state
        pres[i] = new PacketSimulation(pattern, recoveries[ri].o, recoveries[ri].name, seed, engineOptions: recoveries[ri].e).Run();
    });
    Console.WriteLine($"Done in {psw.Elapsed.TotalSeconds:F1} s (averages per 60 s session)\n");
    foreach (var pattern in PacketSimulation.Patterns().Where((_, i) => patterns.Contains(i)))
    {
        Console.WriteLine(pattern.Name);
        Console.WriteLine($"  {"Recovery",-10}{"Frozen ms",10}{">100ms",8}{"Worst ms",10}{"Keyfr",7}{"Lost fr",8}{"NACKs",8}{"Resends",9}{"Lat ms",8}{"Order!",7}");
        foreach (var (name, _, _) in recoveries)
        {
            var g = pres.Where(r => r.Pattern == pattern.Id && r.Recovery == name).ToList();
            Console.WriteLine($"  {name,-10}{g.Average(r => r.FrozenMs),10:F0}{g.Average(r => r.Freezes100),8:F1}{g.Average(r => r.WorstFreezeMs),10:F0}" +
                              $"{g.Average(r => r.KeyframeRequests),7:F1}{g.Average(r => r.FramesLost),8:F1}{g.Average(r => r.NacksSent),8:F0}" +
                              $"{g.Average(r => r.Resends),9:F0}{g.Average(r => r.AvgLatencyMs),8:F1}{g.Sum(r => r.OutOfOrder),7}");
        }
        Console.WriteLine();
    }
    return;
}

var profiles = new[] { StreamProfile.Competitive, StreamProfile.Balanced, StreamProfile.Smooth };
var jobs = new List<(Scenario sc, Func<IReceiverBrain> brain, int seed, bool caps)>();
foreach (bool caps in new[] { true, false })
    for (int seed = 0; seed < seeds; seed++)
        foreach (var sc in Scenario.All(seed))
        {
            jobs.Add((sc, () => new BaselineBrain(), seed, caps));
            foreach (var p in profiles) jobs.Add((sc, () => new EngineBrain(new StabilityOptions { Profile = p, FastClimb = Environment.GetEnvironmentVariable("SIM_SLOW_CLIMB") != "1" /* A/B: old steady climb */ }), seed, caps));
        }

Console.WriteLine($"Running {jobs.Count} simulations ({seeds} seeds x {Scenario.All(0).Count} scenarios x 4 receivers x 2 console behaviours)...");
var sw = System.Diagnostics.Stopwatch.StartNew();
var results = new RunResult[jobs.Count];
Parallel.For(0, jobs.Count, i => results[i] = new Simulation(jobs[i].sc, jobs[i].brain(), jobs[i].seed, jobs[i].caps).Run());
Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F1} s\n");

var summary = results
    .GroupBy(r => (r.Scenario, r.Receiver, r.CapsHonoured))
    .Select(g => new
    {
        scenario = g.Key.Scenario, receiver = g.Key.Receiver, capsHonoured = g.Key.CapsHonoured,
        frozenSec = g.Average(r => r.FrozenSec), freezes = g.Average(r => r.Freezes), stutters = g.Average(r => r.Stutters),
        avgMbps = g.Average(r => r.AvgKbps) / 1000, qualitySwingPct = g.Average(r => r.QualitySwing) * 100, lowQualitySec = g.Average(r => r.LowQualitySec),
        avgLatencyMs = g.Average(r => r.AvgLatencyMs), p95LatencyMs = g.Average(r => r.P95LatencyMs),
        sessionDrops = g.Average(r => r.SessionDrops), keyframeRequests = g.Average(r => r.KeyframeRequests),
    }).ToList();

foreach (bool caps in new[] { true, false })
{
    Console.WriteLine(caps ? "=== Console follows bitrate caps (REMB) ===" : "=== Console ignores bitrate caps ===");
    foreach (var sc in Scenario.All(0))
    {
        Console.WriteLine($"\n{sc.Name}");
        Console.WriteLine($"  {"Receiver",-28}{"Frozen s",9}{"Freezes",9}{"Stutter",9}{"Mbps",7}{"Swing%",8}{"Lat ms",8}{"p95",7}{"Drops",7}{"Soft s",8}");
        foreach (var r in summary.Where(x => x.scenario == sc.Id && x.capsHonoured == caps))
            Console.WriteLine($"  {r.receiver,-28}{r.frozenSec,9:F1}{r.freezes,9:F1}{r.stutters,9:F1}{r.avgMbps,7:F1}{r.qualitySwingPct,8:F0}{r.avgLatencyMs,8:F0}{r.p95LatencyMs,7:F0}{r.sessionDrops,7:F1}{r.lowQualitySec,8:F1}");
    }
    Console.WriteLine();
}

// Timelines from seed 0 for the test-bench charts.
var timelines = results.Where(r => r.Seed == 0).Select(r => new
{
    scenario = r.Scenario, receiver = r.Receiver, capsHonoured = r.CapsHonoured,
    t = r.Timeline.Select(p => p.T), capacity = r.Timeline.Select(p => Math.Round(p.CapacityKbps)),
    cap = r.Timeline.Select(p => Math.Round(p.CapKbps)), delivered = r.Timeline.Select(p => Math.Round(p.DeliveredKbps)),
    latency = r.Timeline.Select(p => double.IsNaN(p.LatencyMs) ? (double?)null : Math.Round(p.LatencyMs, 1)),
    frozen = r.Timeline.Select(p => Math.Round(p.FrozenMs)), buffer = r.Timeline.Select(p => Math.Round(p.BufferMs, 1)),
});

var doc = new
{
    generated = DateTime.UtcNow, seeds, durationSec = 180,
    scenarios = Scenario.All(0).Select(s => new { id = s.Id, name = s.Name, description = s.Description }),
    summary, timelines,
};
File.WriteAllText(outPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));
Console.WriteLine($"Wrote {outPath}");
