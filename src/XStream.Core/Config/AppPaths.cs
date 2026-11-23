namespace XStream.Core.Config;

/// <summary>Where XStream keeps its files: ~/.xstream (tokens, settings, session logs). Override with XSTREAM_HOME.</summary>
public static class AppPaths
{
    public static string Root { get; } = Create(Environment.GetEnvironmentVariable("XSTREAM_HOME") is { Length: > 0 } home
        ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xstream"));

    public static string Tokens => Path.Combine(Root, "tokens.json");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Logs => Create(Path.Combine(Root, "logs"));
    /// <summary>Cached game art for the connecting screen.</summary>
    public static string Art => Create(Path.Combine(Root, "art"));

    private static string Create(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
