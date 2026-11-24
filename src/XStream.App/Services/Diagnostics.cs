using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using XStream.Core.Config;
using XStream.Core.Native;

namespace XStream.App.Services;

/// <summary>
/// One zip with everything needed to look into a problem: recent session logs and summaries, crash
/// reports, settings, what this device has, and the launcher's log. Never includes the sign-in tokens.
/// </summary>
public static class Diagnostics
{
    private const int RecentSessions = 5;

    /// <summary>Writes the zip to Downloads (or Desktop / the data folder) and returns its path.</summary>
    public static string Export(IEnumerable<string> launcherLog, string? lastError = null)
    {
        string folder = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AppPaths.Root,
        }.First(d => !string.IsNullOrEmpty(d) && Directory.Exists(d));
        string path = Path.Combine(folder, $"Vexel-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "system.txt", SystemReport(lastError));
        Add(zip, "launcher-log.txt", string.Join(Environment.NewLine, launcherLog));
        if (File.Exists(AppPaths.Settings)) zip.CreateEntryFromFile(AppPaths.Settings, "settings.json");

        var logs = new DirectoryInfo(AppPaths.Logs);
        // Each session is a .csv plus its .summary.json and .log, named by start time.
        var sessions = logs.GetFiles("*.csv").OrderByDescending(f => f.LastWriteTimeUtc).Take(RecentSessions);
        foreach (var csv in sessions)
        {
            zip.CreateEntryFromFile(csv.FullName, "sessions/" + csv.Name, CompressionLevel.Optimal);
            var summary = Path.ChangeExtension(csv.FullName, ".summary.json");
            if (File.Exists(summary)) zip.CreateEntryFromFile(summary, "sessions/" + Path.GetFileName(summary));
            var text = Path.ChangeExtension(csv.FullName, ".log");
            if (File.Exists(text)) zip.CreateEntryFromFile(text, "sessions/" + Path.GetFileName(text));
        }
        foreach (var crash in logs.GetFiles("crash-*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Take(5))
            zip.CreateEntryFromFile(crash.FullName, "crashes/" + crash.Name);
        return path;
    }

    private static string SystemReport(string? lastError)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Vexel {typeof(Diagnostics).Assembly.GetName().Version}");
        sb.AppendLine($"Created {DateTime.Now:O}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($".NET {Environment.Version}, {Environment.ProcessorCount} logical processors");
        sb.AppendLine($"Process: {Environment.ProcessPath}");
        sb.AppendLine($"Native libraries: {NativeBundle.Location}");
        sb.AppendLine();
        sb.AppendLine("System check:");
        try
        {
            foreach (var c in SystemCheck.Run()) sb.AppendLine($"  [{(c.Ok ? "ok" : "!!")}] {c.Name}: {c.Detail}");
        }
        catch (Exception ex) { sb.AppendLine("  failed: " + ex.Message); }
        sb.AppendLine($"  Low-latency input channel: {XStream.WebRtc.XboxPeer.UnreliableInputSupported}");
        if (!string.IsNullOrEmpty(lastError)) { sb.AppendLine(); sb.AppendLine("Last error: " + lastError); }
        return sb.ToString();
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open());
        w.Write(text);
    }
}
