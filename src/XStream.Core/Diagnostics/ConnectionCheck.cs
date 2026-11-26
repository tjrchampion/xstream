namespace XStream.Core.Diagnostics;

/// <summary>One second of a connection check (a short test stream with no picture).</summary>
public sealed record CheckSample(double Mbps, double CapMbps, double LossPct, double PingMs, double JitterMs);

public enum CheckLevel { Good, Fair, Poor }

/// <summary>One thing the check found, in plain words, with what to do about it.</summary>
public sealed record CheckFinding(CheckLevel Level, string Title, string Detail);

/// <summary>The outcome: an overall verdict, findings, and settings that suit this connection.</summary>
public sealed record CheckResult
{
    public CheckLevel Level { get; init; }
    public string Headline { get; init; } = "";
    public IReadOnlyList<CheckFinding> Findings { get; init; } = [];
    public double PingMs { get; init; }
    public double JitterMs { get; init; }
    public double LossPct { get; init; }
    /// <summary>Highest bitrate seen (3-second average). Depends on the picture: a quiet dashboard needs little.</summary>
    public double PeakMbps { get; init; }
    /// <summary>A maximum bitrate that keeps clear of trouble; null when the check saw none (keep the current one).</summary>
    public double? RecommendedMaxMbps { get; init; }
    /// <summary>Fastest (720p) suits this connection better than Sharpest.</summary>
    public bool RecommendFastest { get; init; }
    /// <summary>This device's link, and how the stream reached the console (filled in by the caller).</summary>
    public LocalLink Link { get; init; } = new();
    public string Route { get; init; } = "";
}

/// <summary>
/// Turns a connection check into advice. Inputs: per-second stats from a short headless stream (the first
/// seconds, while the bitrate climbs, are skipped), this device's link (wired or Wi-Fi band and signal), whether
/// the console itself is on Wi-Fi, and the route (home network, internet, relayed).
/// </summary>
public static class ConnectionCheck
{
    public const int SettleSeconds = 3;

    /// <param name="settleSeconds">Leading seconds to skip (the bitrate is still climbing); 0 for a window of a running stream.</param>
    public static CheckResult Analyze(IReadOnlyList<CheckSample> samples, LocalLink link, bool consoleOnWifi, string route, int settleSeconds = SettleSeconds)
    {
        var s = samples.Skip(Math.Min(settleSeconds, Math.Max(0, samples.Count - 3))).ToArray();
        if (s.Length == 0)
            return new CheckResult
            {
                Level = CheckLevel.Poor, Headline = "The test stream didn't get going",
                Findings = [new(CheckLevel.Poor, "No data", "The console didn't stream long enough to measure. Check it's on (or in Sleep mode) and try again.")],
            };
        double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToArray(); return a.Length == 0 ? 0 : a[a.Length / 2]; }
        double ping = Median(s.Select(x => x.PingMs));
        double jitter = Median(s.Select(x => x.JitterMs));
        double loss = s.Average(x => x.LossPct);
        double worstLoss = s.Max(x => x.LossPct);
        // Peak: the best 3-second stretch, so one fast second doesn't count.
        double peak = s.Length < 3 ? s.Max(x => x.Mbps) : Enumerable.Range(0, s.Length - 2).Max(i => (s[i].Mbps + s[i + 1].Mbps + s[i + 2].Mbps) / 3);
        bool home = route.StartsWith("Home", StringComparison.OrdinalIgnoreCase);

        var f = new List<CheckFinding>();
        // This device's link.
        if (link.IsWifi)
        {
            if (link.BandGHz is > 0 and < 3)
                f.Add(new(CheckLevel.Poor, "This device is on 2.4 GHz Wi-Fi",
                    "2.4 GHz is slow and crowded. Join your router's 5 GHz (or 6 GHz) network; it often has the same name with \"5G\" added."));
            int q = link.SignalQuality;
            if (q is >= 0 and < 50)
                f.Add(new(CheckLevel.Poor, "Weak Wi-Fi signal", $"Signal is {q}%. Move closer to the router, or keep walls and the router's back out of the way."));
            else if (q is >= 50 and < 70)
                f.Add(new(CheckLevel.Fair, "Wi-Fi signal could be stronger", $"Signal is {q}%. Fine for most games; moving closer to the router helps if the picture stutters."));
            if (link.LinkMbps is > 0 and < 100)
                f.Add(new(CheckLevel.Fair, "Slow Wi-Fi link", $"The radio negotiated only {link.LinkMbps:F0} Mbps; streams need headroom above their bitrate."));
        }
        else if (link.Kind == "Ethernet")
            f.Add(new(CheckLevel.Good, "This device is wired", "Ethernet: the best connection for streaming."));
        if (consoleOnWifi)
            f.Add(new(CheckLevel.Fair, "The console is on Wi-Fi", "Connect the Xbox to the router with an Ethernet cable: it's the single biggest improvement for remote play."));

        // The path to the console.
        if (loss >= 2 || worstLoss >= 10)
            f.Add(new(CheckLevel.Poor, $"Packets are getting lost ({loss:F1}%)", "Lost packets mean freezes and blur. Usually Wi-Fi interference or another device using the network heavily (downloads, uploads, video calls)."));
        else if (loss >= 0.5)
            f.Add(new(CheckLevel.Fair, $"Some packet loss ({loss:F1}%)", "Vexel repairs small losses, but you may see the odd stutter."));
        if (jitter >= 15)
            f.Add(new(CheckLevel.Poor, $"Uneven timing ({jitter:F0} ms jitter)", "Packets arrive in bursts, which needs a bigger buffer (more delay). Common on busy Wi-Fi."));
        else if (jitter >= 6)
            f.Add(new(CheckLevel.Fair, $"Slightly uneven timing ({jitter:F0} ms jitter)", "The Smooth profile hides it at the cost of a little delay."));
        double slowPing = home ? 15 : 60, badPing = home ? 40 : 120;
        if (ping >= badPing)
            f.Add(new(CheckLevel.Poor, $"High delay to the console ({ping:F0} ms)", home ? "That's slow for a home network: something between this device and the console is struggling." : "Distance and the internet path add delay; expect less responsive controls."));
        else if (ping >= slowPing)
            f.Add(new(CheckLevel.Fair, $"Delay to the console is {ping:F0} ms", home ? "Higher than a good home network (under 10 ms)." : "Fine for most games away from home."));
        // The console only sends a high bitrate when the picture is busy, so a quiet dashboard says little about
        // top speed. What counts: did trouble (loss, bursts) show up, and at what bitrate.
        var trouble = s.Where(x => x.LossPct >= 1 || x.JitterMs >= 15).ToArray();
        double? troubleMbps = trouble.Length >= 2 ? Median(trouble.Select(x => x.Mbps)) : null;
        if (troubleMbps is { } tm && tm < 15)
            f.Add(new(CheckLevel.Poor, $"Trouble from {tm:F0} Mbps", "The connection struggles even at a low bitrate. Use Fastest (720p), which needs far less."));
        else if (troubleMbps is { } tm2)
            f.Add(new(CheckLevel.Fair, $"Trouble from about {tm2:F0} Mbps", $"A maximum bitrate below that keeps the stream clean."));
        if (route.StartsWith("Relayed", StringComparison.OrdinalIgnoreCase))
            f.Add(new(CheckLevel.Fair, "The stream goes through a relay", "No direct path to the console was found, which adds delay. Opening UPnP on the router usually allows a direct path."));

        var level = f.Any(x => x.Level == CheckLevel.Poor) ? CheckLevel.Poor : f.Any(x => x.Level == CheckLevel.Fair) ? CheckLevel.Fair : CheckLevel.Good;
        if (!f.Any(x => x.Level != CheckLevel.Good))
            f.Add(new(CheckLevel.Good, "Everything looks good", $"{ping:F0} ms to the console, {loss:F1}% packet loss, steady timing."));

        bool fastest = loss >= 2 || jitter >= 15 || troubleMbps is < 20 || link.BandGHz is > 0 and < 3 || link.LinkMbps is > 0 and < 100;
        // Below where trouble started (a stream at its link's limit queues and stutters); no trouble: no cap needed.
        double? max = troubleMbps is { } t ? Math.Clamp(Math.Floor(t * 0.7 / 5) * 5, 10, 60) : null;
        string headline = level switch
        {
            CheckLevel.Good => "Your connection is great for streaming",
            CheckLevel.Fair => "Your connection is good, with room to improve",
            _ => "Your connection will struggle",
        };
        return new CheckResult
        {
            Level = level, Headline = headline, Findings = f.OrderByDescending(x => x.Level).ToList(),
            PingMs = ping, JitterMs = jitter, LossPct = loss, PeakMbps = peak, RecommendedMaxMbps = max, RecommendFastest = fastest,
        };
    }
}
