namespace XStream.Player;

public enum ToastKind { Good, Warn, Bad }

public sealed record HealthToast(string Text, ToastKind Kind, string Category);

/// <summary>
/// Turns the once-a-second stream numbers into the occasional plain-language toast: "Network trouble: …",
/// "Frame rate dropped …", "Lowered quality …", and "Back to normal" once it has recovered. Problems must last
/// a couple of seconds before they're mentioned (one bad second is normal on Wi-Fi), the same kind of message
/// isn't repeated within 30 s, and nothing is shown more often than every 8 s, so it never nags.
/// Pure logic (no clock of its own): feed it <see cref="LiveStats"/> with their stream time.
/// </summary>
public sealed class StreamHealth
{
    private const double RepeatAfterS = 30, MinGapS = 8, RecoveredAfterS = 6, IgnoreFirstS = 5;

    private readonly Dictionary<string, double> _lastShown = [];
    private double _lastAnyS = double.NegativeInfinity;
    private int _badNetS, _lowFpsS, _laggyS;
    private double _okSinceS = double.NaN;
    private bool _warned;
    private double _bestCapMbps;

    /// <summary>The newest second of numbers. Returns a toast to show, or null.</summary>
    public HealthToast? Update(LiveStats s, bool reconnecting = false, int clarity = 0)
    {
        double t = s.TimeS;
        if (t < IgnoreFirstS) { _bestCapMbps = Math.Max(_bestCapMbps, s.CapMbps); return null; } // settling in

        if (reconnecting)
        {
            _okSinceS = double.NaN;
            return Show(t, new HealthToast("Connection lost, reconnecting…", ToastKind.Bad, "reconnect"), force: true);
        }

        // Network: loss, jitter or ping beyond what feels fine, for two seconds running.
        string? net = s.LossPct > 3 ? $"{s.LossPct:F0}% of packets lost"
            : s.JitterMs > 25 ? $"unsteady connection ({s.JitterMs:F0} ms jitter)"
            : s.PingMs > 120 ? $"high ping ({s.PingMs:F0} ms)"
            : null;
        _badNetS = net is null ? 0 : _badNetS + 1;

        // Picture: frame rate well under 60 while the network is fine means this device is struggling.
        _lowFpsS = s.Fps is > 0 and < 48 && net is null ? _lowFpsS + 1 : 0;
        // Lag: network-to-screen time creeping up (buffering to ride out a rough patch).
        _laggyS = s.NetToScreenP50Ms > 60 ? _laggyS + 1 : 0;

        // Quality: the bitrate controller has backed well off its best for this session.
        bool lowered = _bestCapMbps > 0 && s.CapMbps > 0 && s.CapMbps < _bestCapMbps * 0.6;
        _bestCapMbps = Math.Max(_bestCapMbps, s.CapMbps);

        HealthToast? toast = null;
        if (_badNetS >= 2)
            toast = new($"Network trouble: {net}. Keeping the picture smooth…", ToastKind.Warn, "network");
        else if (_lowFpsS >= 3)
            toast = new($"Frame rate dropped to {s.Fps:F0} fps{(clarity > 0 ? ". Try Clarity boost Off" : "")}", ToastKind.Warn, "fps");
        else if (_laggyS >= 3)
            toast = new($"Extra delay: {s.NetToScreenP50Ms:F0} ms from network to screen", ToastKind.Warn, "lag");
        else if (lowered)
            toast = new($"Lowered quality to {s.CapMbps:F0} Mbps for a weaker connection", ToastKind.Warn, "quality");

        if (toast is not null) { _okSinceS = double.NaN; return Show(t, toast); }

        // Healthy this second. After a warning, say so once it's held for a few seconds.
        bool healthy = net is null && _lowFpsS == 0 && _laggyS == 0;
        if (!healthy) { _okSinceS = double.NaN; return null; }
        if (double.IsNaN(_okSinceS)) _okSinceS = t;
        if (_warned && t - _okSinceS >= RecoveredAfterS)
        {
            _warned = false;
            return Show(t, new HealthToast("Connection back to normal", ToastKind.Good, "ok"), force: true);
        }
        return null;
    }

    private HealthToast? Show(double t, HealthToast toast, bool force = false)
    {
        if (!force && t - _lastAnyS < MinGapS) return null;
        if (_lastShown.TryGetValue(toast.Category, out var last) && t - last < RepeatAfterS) return null;
        _lastShown[toast.Category] = t;
        _lastAnyS = t;
        if (toast.Kind != ToastKind.Good) _warned = true;
        return toast;
    }
}
