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

var profiles = new[] { StreamProfile.Competitive, StreamProfile.Balanced, StreamProfile.Smooth };
var jobs = new List<(Scenario sc, Func<IReceiverBrain> brain, int seed, bool caps)>();
foreach (bool caps in new[] { true, false })
    for (int seed = 0; seed < seeds; seed++)
        foreach (var sc in Scenario.All(seed))
        {
            jobs.Add((sc, () => new BaselineBrain(), seed, caps));
            foreach (var p in profiles) jobs.Add((sc, () => new EngineBrain(new StabilityOptions { Profile = p }), seed, caps));
        }

Console.WriteLine($"Running {jobs.Count} simulations ({seeds} seeds x 5 scenarios x 4 receivers x 2 console behaviours)...");
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
        avgMbps = g.Average(r => r.AvgKbps) / 1000, qualitySwingPct = g.Average(r => r.QualitySwing) * 100,
        avgLatencyMs = g.Average(r => r.AvgLatencyMs), p95LatencyMs = g.Average(r => r.P95LatencyMs),
        sessionDrops = g.Average(r => r.SessionDrops), keyframeRequests = g.Average(r => r.KeyframeRequests),
    }).ToList();

foreach (bool caps in new[] { true, false })
{
    Console.WriteLine(caps ? "=== Console follows bitrate caps (REMB) ===" : "=== Console ignores bitrate caps ===");
    foreach (var sc in Scenario.All(0))
    {
        Console.WriteLine($"\n{sc.Name}");
        Console.WriteLine($"  {"Receiver",-28}{"Frozen s",9}{"Freezes",9}{"Stutter",9}{"Mbps",7}{"Swing%",8}{"Lat ms",8}{"p95",7}{"Drops",7}");
        foreach (var r in summary.Where(x => x.scenario == sc.Id && x.capsHonoured == caps))
            Console.WriteLine($"  {r.receiver,-28}{r.frozenSec,9:F1}{r.freezes,9:F1}{r.stutters,9:F1}{r.avgMbps,7:F1}{r.qualitySwingPct,8:F0}{r.avgLatencyMs,8:F0}{r.p95LatencyMs,7:F0}{r.sessionDrops,7:F1}");
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
