using System.Diagnostics;
using System.Net.Sockets;

namespace XStream.Core.Xbox;

/// <summary>An Xbox Cloud Gaming region offered to the account (from the login response's offeringSettings).</summary>
public sealed record CloudRegion(string Name, string BaseUri, bool IsDefault);

/// <summary>
/// Picks the cloud region closest to this device. The region's API host decides which data centre the game runs in,
/// so a nearer region means lower ping for the whole session. Microsoft marks one region as default; it's kept
/// unless another is clearly faster.
/// </summary>
public static class CloudRegions
{
    /// <summary>Keep the default region unless another is at least this much faster.</summary>
    public const double PreferDefaultWithinMs = 8;

    /// <summary>Best of three TCP connects to the region's API host (ms); +inf if unreachable.</summary>
    public static async Task<double> MeasureMsAsync(string baseUri, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var uri)) return double.PositiveInfinity;
        double best = double.PositiveInfinity;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                using var s = new Socket(SocketType.Stream, ProtocolType.Tcp);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(1.5));
                var sw = Stopwatch.StartNew();
                await s.ConnectAsync(uri.Host, uri.Port > 0 ? uri.Port : 443, cts.Token);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            catch { /* unreachable or too slow this time */ }
        }
        return best;
    }

    /// <summary>The nearest region (the default if it's within <see cref="PreferDefaultWithinMs"/> of the nearest).</summary>
    public static async Task<(CloudRegion? Region, string Report)> PickNearestAsync(IReadOnlyList<CloudRegion> regions, CancellationToken ct = default)
    {
        if (regions.Count == 0) return (null, "no regions offered");
        var timed = await Task.WhenAll(regions.Select(async r => (Region: r, Ms: await MeasureMsAsync(r.BaseUri, ct))));
        return Choose(timed);
    }

    public static (CloudRegion? Region, string Report) Choose(IReadOnlyList<(CloudRegion Region, double Ms)> timed)
    {
        var reachable = timed.Where(t => double.IsFinite(t.Ms)).OrderBy(t => t.Ms).ToList();
        var def = timed.FirstOrDefault(t => t.Region.IsDefault);
        string report = string.Join(", ", timed.OrderBy(t => t.Ms).Select(t => $"{t.Region.Name} {(double.IsFinite(t.Ms) ? $"{t.Ms:F0} ms" : "unreachable")}"));
        if (reachable.Count == 0) return (def.Region ?? timed[0].Region, report);
        var nearest = reachable[0];
        var pick = def.Region is not null && double.IsFinite(def.Ms) && def.Ms - nearest.Ms <= PreferDefaultWithinMs ? def : nearest;
        return (pick.Region, report);
    }
}
