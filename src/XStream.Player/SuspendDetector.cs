namespace XStream.Player;

/// <summary>
/// Notices that the device slept (a handheld's power button, a laptop lid): a thread that ticks every second
/// sees the wall clock jump. A stream can't survive sleep (the console gives up on the connection and Wi-Fi
/// comes back slowly), so on wake the session reconnects straight away instead of waiting for the old
/// connection to time out. Pure logic: time is passed in.
/// </summary>
public sealed class SuspendDetector(double gapSeconds = 8)
{
    private DateTime? _last;

    /// <summary>Call about once a second. Returns how long the device was away, if it slept since the last call.</summary>
    public TimeSpan? Observe(DateTime nowUtc)
    {
        var last = _last;
        _last = nowUtc;
        if (last is null) return null;
        var gap = nowUtc - last.Value;
        return gap.TotalSeconds >= gapSeconds ? gap : null;
    }
}
