using System.Text;

namespace XStream.Core.Xbox;

public enum CloudSort { Relevance, Name, Newest }

/// <summary>
/// Search for the cloud library. Forgiving matching: case, accents and punctuation are ignored ("assassins" finds
/// "Assassin's Creed", "pokemon" finds "Pokémon"), every word must match somewhere (name, publisher or genre), and
/// results are ranked: title starts with the query, then a title word starts with it, then title contains it, then
/// publisher/genre matches. Optional genre filter and sort order.
/// </summary>
public static class CloudSearch
{
    public static IReadOnlyList<CloudGame> Filter(IEnumerable<CloudGame> games, string? query, string? genre = null,
        CloudSort sort = CloudSort.Relevance)
    {
        var q = Normalize(query ?? "");
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<(CloudGame Game, int Rank)>();
        foreach (var g in games)
        {
            if (!string.IsNullOrEmpty(genre) && !(g.Genres?.Contains(genre, StringComparer.CurrentCultureIgnoreCase) ?? false)) continue;
            if (words.Length == 0) { matches.Add((g, 0)); continue; }
            string name = Normalize(g.Name);
            string other = Normalize(g.Publisher + " " + string.Join(' ', g.Genres ?? []));
            if (!words.All(w => name.Contains(w) || other.Contains(w))) continue;
            int rank = name.StartsWith(q) ? 0
                : name.Split(' ').Any(part => part.StartsWith(words[0])) ? 1
                : words.All(name.Contains) ? 2
                : 3;
            matches.Add((g, rank));
        }
        IEnumerable<(CloudGame Game, int Rank)> ordered = sort switch
        {
            CloudSort.Newest => matches.OrderByDescending(m => m.Game.Released ?? DateTimeOffset.MinValue).ThenBy(m => m.Game.Name, StringComparer.CurrentCultureIgnoreCase),
            CloudSort.Name => matches.OrderBy(m => m.Game.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => matches.OrderBy(m => m.Rank).ThenBy(m => m.Game.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        return ordered.Select(m => m.Game).ToList();
    }

    /// <summary>The library's genres, most common first (for filter chips).</summary>
    public static IReadOnlyList<string> Genres(IEnumerable<CloudGame> games, int max = 14) =>
        games.SelectMany(g => g.Genres ?? []).GroupBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(x => x.Count()).ThenBy(x => x.Key).Take(max).Select(x => x.First()).ToList();

    /// <summary>Lower case, no accents, letters/digits only, single spaces ("Assassin's Creed® IV" -> "assassins creed iv").</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            char ch = Fold(char.ToLowerInvariant(c));
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch is '-' or ':' or '_' or '/' or '.' or '&') sb.Append(' ');
            // apostrophes, ™, ®, etc. are dropped: "assassin's" -> "assassins"
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // Accent folding by table: the app runs with invariant globalization, where string.Normalize(FormD) doesn't
    // decompose characters, so "é" can't be split into "e" + accent at runtime.
    private const string Accented = "àáâãäåāăąçćĉċčďđèéêëēĕėęěĝğġģĥħìíîïĩīĭįıĵķĺļľŀłñńņňòóôõöøōŏőŕŗřśŝşšţťŧùúûüũūŭůűųŵýÿŷźżžß";
    private const string Plain    = "aaaaaaaaacccccddeeeeeeeeegggghhiiiiiiiiijklllllnnnnooooooooorrrsssstttuuuuuuuuuuwyyyzzzs";

    private static char Fold(char c)
    {
        if (c < 'à') return c;
        int i = Accented.IndexOf(c);
        return i >= 0 ? Plain[i] : c;
    }
}
