namespace XStream.Player;

/// <summary>
/// Command-line shortcuts, for a tile in Steam, Armoury Crate, Playnite or a desktop shortcut:
///   Vexel --stream                 stream the last used console
///   Vexel --stream "Living Room"   stream that console (name or id)
///   Vexel --game 9NBLGGH4R315      start that game (store product id) on the console, then stream it
///   Vexel --cloud FORTNITE         Xbox Cloud Gaming, that title id
/// </summary>
public sealed record LaunchArgs(bool Stream, string? Console, string? GameProductId, string? CloudTitleId)
{
    public bool Any => Stream || GameProductId is not null || CloudTitleId is not null;

    public static LaunchArgs Parse(IReadOnlyList<string> args)
    {
        bool stream = false; string? console = null, game = null, cloud = null;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            // Also accept --stream=Name forms.
            string? inline = a.Contains('=') ? a[(a.IndexOf('=') + 1)..] : null;
            string key = inline is null ? a : a[..a.IndexOf('=')];
            switch (key.ToLowerInvariant())
            {
                case "--stream" or "-s": stream = true; console = inline ?? Next(); break;
                case "--game": game = inline ?? Next(); stream = true; break;
                case "--cloud": cloud = inline ?? Next(); break;
            }
        }
        return new LaunchArgs(stream, Blank(console), Blank(game), Blank(cloud));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Launch options for a console, quoted for a shortcut or Steam's "Launch options".</summary>
    public static string ForConsole(string name) => $"--stream \"{name.Replace("\"", "")}\"";
}
