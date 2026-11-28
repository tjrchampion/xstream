namespace XStream.Core.Together;

/// <summary>
/// Paces sending: up to <see cref="BurstBytes"/> at once, then <see cref="RateBytesPerSecond"/>. A keyframe is often
/// 100-300 KB; sent in one burst it overflows the home router's upload queue and much of it is lost. Spread over a
/// few milliseconds it arrives whole. Not thread-safe (the host's sender thread owns it).
/// </summary>
public sealed class TokenBucket(double rateBytesPerSecond, double burstBytes)
{
    private double _tokens = burstBytes;
    private double _lastMs = double.NaN;

    public double RateBytesPerSecond { get; set; } = rateBytesPerSecond;
    public double BurstBytes { get; set; } = burstBytes;

    private void Refill(double nowMs)
    {
        if (!double.IsNaN(_lastMs)) _tokens = Math.Min(BurstBytes, _tokens + (nowMs - _lastMs) * RateBytesPerSecond / 1000);
        _lastMs = nowMs;
    }

    /// <summary>Take <paramref name="bytes"/> if there are enough tokens now.</summary>
    public bool TryTake(int bytes, double nowMs)
    {
        Refill(nowMs);
        if (_tokens < bytes) return false;
        _tokens -= bytes;
        return true;
    }

    /// <summary>Take them whatever the balance (urgent packets); what's sent next waits longer to make up for it.</summary>
    public void Force(int bytes, double nowMs)
    {
        Refill(nowMs);
        _tokens = Math.Max(-BurstBytes * 4, _tokens - bytes);
    }

    /// <summary>How long until <paramref name="bytes"/> can be sent.</summary>
    public double WaitMs(int bytes, double nowMs)
    {
        Refill(nowMs);
        return _tokens >= bytes ? 0 : (bytes - _tokens) * 1000 / Math.Max(1, RateBytesPerSecond);
    }
}
