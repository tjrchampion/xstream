// Compile-only stand-ins for SIPSorcery helpers, so its SCTP sources can be tested on their own
// without restoring any NuGet packages.
// Minimal stand-ins so SIPSorcery's SCTP sources compile on their own for testing.
namespace Microsoft.Extensions.Logging
{
    public enum LogLevel { Trace, Debug, Information, Warning, Error, Critical, None }
    public interface ILogger { }
    public interface ILogger<T> : ILogger { }
    public static class LoggerExtensions
    {
        public static void LogTrace(this ILogger l, string m, params object[] a) { }
        public static void LogDebug(this ILogger l, string m, params object[] a) { }
        public static void LogInformation(this ILogger l, string m, params object[] a) { }
        public static void LogWarning(this ILogger l, string m, params object[] a) { if (System.Environment.GetEnvironmentVariable("SCTP_LOG") == "1") System.Console.WriteLine("WARN " + m + " " + string.Join(",", a)); }
        public static void LogWarning(this ILogger l, System.Exception e, string m, params object[] a) { }
        public static void LogError(this ILogger l, string m, params object[] a) { System.Console.WriteLine("ERR " + m); }
        public static void LogError(this ILogger l, System.Exception e, string m, params object[] a) { System.Console.WriteLine("ERR " + m + " " + e); }
        public static bool IsEnabled(this ILogger l, LogLevel lv) => false;
    }
}
namespace SIPSorcery.Sys
{
    internal sealed class NullLogger : Microsoft.Extensions.Logging.ILogger { }
    public static class LogFactory
    {
        public static Microsoft.Extensions.Logging.ILogger CreateLogger<T>() => new NullLogger();
        public static Microsoft.Extensions.Logging.ILogger CreateLogger(string n) => new NullLogger();
    }
}
namespace SIPSorcery
{
    public static class LogFactory
    {
        public static Microsoft.Extensions.Logging.ILogger CreateLogger<T>() => new SIPSorcery.Sys.NullLogger();
        public static Microsoft.Extensions.Logging.ILogger CreateLogger(string n) => new SIPSorcery.Sys.NullLogger();
    }
}
namespace SIPSorcery.Net
{
    public static class SctpUdpTransport { public const int DEFAULT_UDP_MTU = 1300; }
}
namespace SIPSorcery.Sys
{
    public static class IPSocket { public static System.Net.IPEndPoint Parse(string s) => System.Net.IPEndPoint.Parse(s); public static bool IsPrivateAddress(string a) => true; }
}
