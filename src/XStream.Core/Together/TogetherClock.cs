using System.Diagnostics;

namespace XStream.Core.Together;

/// <summary>
/// The machine's monotonic clock in tenths of a millisecond (wraps every 5 days; only differences are used). Shared by every process on the machine, so
/// two copies of Vexel on one computer (the test harness) can measure exactly how long a frame took from host to guest.
/// </summary>
public static class TogetherClock
{
    public static uint Tenths => (uint)(Stopwatch.GetTimestamp() / Math.Max(1, Stopwatch.Frequency / 10_000));
}
