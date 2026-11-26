using XStream.Core.Diagnostics;

namespace XStream.Player;

/// <summary>
/// Connection tips during a stream: the same advice as the connection check (<see cref="ConnectionCheck"/>),
/// from the stream's own once-a-second numbers. First look after <see cref="FirstLookS"/> seconds of streaming,
/// then every <see cref="EverySeconds"/> over the last <see cref="WindowS"/> seconds. <see cref="Tick"/> says
/// when to tell the player (once when tips first appear, and again only after things were fine for a while).
/// </summary>
public sealed class ConnectionAdvisor
{
    public const int FirstLookS = 20, EverySeconds = 60, WindowS = 45;

    private readonly Queue<CheckSample> _recent = new();
    private readonly Task<LocalLink> _link;
    private int _seconds;
    private bool _told;
    private int _fineSince = -1;

    public ConnectionAdvisor(Task<LocalLink>? link = null) => _link = link ?? Task.Run(LocalLink.Detect);

    /// <summary>The latest tips (null until the first look).</summary>
    public CheckResult? Latest { get; private set; }

    /// <summary>
    /// One second of a streaming connection (call only while it's streaming normally). Returns true when the
    /// player should be told there are tips.
    /// </summary>
    public bool Tick(CheckSample s, string route)
    {
        _seconds++;
        _recent.Enqueue(s);
        while (_recent.Count > WindowS) _recent.Dequeue();
        if (_seconds < FirstLookS || (_seconds - FirstLookS) % EverySeconds != 0) return false;
        var link = _link.IsCompletedSuccessfully ? _link.Result : new LocalLink();
        Latest = ConnectionCheck.Analyze([.. _recent], link, false, route, settleSeconds: 0);
        bool tips = Latest.Level != CheckLevel.Good;
        if (!tips) { if (_fineSince < 0) _fineSince = _seconds; if (_seconds - _fineSince >= 300) _told = false; return false; }
        _fineSince = -1;
        if (_told) return false;
        _told = true;
        return true;
    }

    /// <summary>Settings changes the tips suggest, as a short label ("Fastest · 15 Mbps"), or "" for none.</summary>
    public static string Suggestion(CheckResult? r, Core.Config.AppSettings s)
    {
        if (r is null || r.Level == CheckLevel.Good) return "";
        var parts = new List<string>();
        if (r.RecommendFastest && s.Resolution != Core.Xbox.StreamResolution.R720) parts.Add("Fastest");
        if (r.RecommendedMaxMbps is { } m && Math.Abs(m - s.MaxMbps) >= 1) parts.Add($"{m:F0} Mbps");
        if (r.JitterMs >= 15 && s.Profile != Core.Stability.StreamProfile.Smooth) parts.Add("Smooth");
        return string.Join(" · ", parts);
    }

    /// <summary>The settings with the suggestions applied (bitrate and profile apply live; Fastest from the next stream).</summary>
    public static Core.Config.AppSettings Apply(CheckResult r, Core.Config.AppSettings s)
    {
        if (r.RecommendFastest) s = s with { Resolution = Core.Xbox.StreamResolution.R720 };
        if (r.RecommendedMaxMbps is { } m) s = s with { MaxMbps = m, StartMbps = Math.Min(s.StartMbps, m) };
        if (r.JitterMs >= 15) s = s with { Profile = Core.Stability.StreamProfile.Smooth };
        return s;
    }
}
