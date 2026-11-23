namespace XStream.Player;

/// <summary>
/// Decides when a running stream has stopped responding. The stream process sends a heartbeat every 2 s with
/// how long its main loop (window events and drawing) has been stuck. No heartbeat for <see cref="SilentLimitS"/>
/// (the whole process is frozen) or a main loop stuck for <see cref="StuckLimitS"/> (the window froze, e.g. a
/// driver hang) means the player is looking at a dead full-screen window. Pure logic: time is passed in.
/// </summary>
public sealed class StreamWatchdog
{
    public const double SilentLimitS = 20, StuckLimitS = 15;
    private DateTime _lastHeartbeat;
    private bool _fired;

    public void Arm(DateTime now) { _lastHeartbeat = now; _fired = false; }

    /// <summary>A heartbeat line arrived. Returns a reason if the stream should be closed.</summary>
    public string? Heartbeat(DateTime now, double stuckSeconds)
    {
        _lastHeartbeat = now;
        return stuckSeconds >= StuckLimitS ? Fire($"The stream window froze for {stuckSeconds:F0} s, so XStream closed it.") : null;
    }

    /// <summary>Called every couple of seconds. Returns a reason if the stream should be closed.</summary>
    public string? Check(DateTime now) =>
        (now - _lastHeartbeat).TotalSeconds > SilentLimitS
            ? Fire($"The stream stopped responding (no signal for {SilentLimitS:F0} s), so XStream closed it.")
            : null;

    private string? Fire(string reason)
    {
        if (_fired) return null; // once per stream
        _fired = true;
        return reason;
    }
}
