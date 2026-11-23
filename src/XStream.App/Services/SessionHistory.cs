using System.Text.Json;
using XStream.Core.Config;
using XStream.Core.Stability;

namespace XStream.App.Services;

public sealed record PastSession(SessionSummary Summary, string CsvPath, DateTime When);

/// <summary>Past sessions from ~/.xstream/logs (*.summary.json written at the end of each stream).</summary>
public static class SessionHistory
{
    public static IReadOnlyList<PastSession> Load(int max = 100)
    {
        var list = new List<PastSession>();
        foreach (var f in Directory.EnumerateFiles(AppPaths.Logs, "*.summary.json").OrderByDescending(f => f).Take(max))
        {
            try
            {
                var s = JsonSerializer.Deserialize<SessionSummary>(File.ReadAllText(f));
                if (s is null) continue;
                var when = s.Started != default ? s.Started.LocalDateTime : File.GetLastWriteTime(f);
                list.Add(new PastSession(s, f.Replace(".summary.json", ".csv"), when));
            }
            catch (Exception ex) when (ex is JsonException or IOException) { /* skip unreadable */ }
        }
        return list.OrderByDescending(p => p.When).ToList();
    }
}
