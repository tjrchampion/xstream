using System.Text.Json.Nodes;

namespace XStream.Core.Xbox;

/// <summary>A console game from the account's play history, with its wide key art.</summary>
public sealed record LibraryGame(string TitleId, string Name, string HeroUrl, string? LogoUrl, DateTimeOffset LastPlayed);

/// <summary>Recently played console games (Xbox title history), used for the connecting screen's artwork.</summary>
public static class XboxLibrary
{
    // Wide, full-bleed art first; box art last (it's portrait and gets cropped hard).
    private static readonly string[] HeroTypes = ["SuperHeroArt", "TitledHeroArt", "Screenshot", "BrandedKeyArt", "Poster", "BoxArt"];

    /// <summary>The Xbox Home screen's title id (presence shows it when no game is running).</summary>
    private const string HomeTitleId = "750323071";

    /// <summary>
    /// What the account is running on a console right now (Xbox presence), or null if nothing / offline.
    /// Presence is per account, not per console: it's whatever this account is playing on any Xbox.
    /// </summary>
    public static async Task<(string TitleId, string Name)?> GetNowPlayingAsync(HttpClient http, string authorization, string xuid,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://userpresence.xboxlive.com/users/xuid({xuid})?level=all");
        req.Headers.Add("Authorization", authorization);
        req.Headers.Add("x-xbl-contract-version", "3");
        req.Headers.Add("Accept", "application/json");
        return ParsePresence(await XboxAuth.ReadJson(await http.SendAsync(req, ct), ct));
    }

    public static (string TitleId, string Name)? ParsePresence(JsonNode j)
    {
        foreach (var d in j["devices"]?.AsArray() ?? [])
        {
            if (d?["type"]?.GetValue<string>() is not ("XboxOne" or "Scarlett" or "XboxSeries")) continue;
            foreach (var t in d["titles"]?.AsArray() ?? [])
            {
                string id = t?["id"]?.ToString() ?? "";
                if (id.Length == 0 || id == HomeTitleId) continue;
                if (t!["placement"]?.GetValue<string>() is "Background") continue;
                if (t["state"]?.GetValue<string>() is { } st && st != "Active") continue;
                return (id, t["name"]?.GetValue<string>() ?? "");
            }
        }
        return null;
    }

    /// <summary>One title's art (for a game that isn't in the recent history yet).</summary>
    public static async Task<LibraryGame?> GetTitleAsync(HttpClient http, string authorization, string xuid, string titleId,
        string locale, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://titlehub.xboxlive.com/users/xuid({xuid})/titles/titleid({titleId})/decoration/image");
        req.Headers.Add("Authorization", authorization);
        req.Headers.Add("x-xbl-contract-version", "2");
        req.Headers.Add("Accept-Language", locale);
        return Parse(await XboxAuth.ReadJson(await http.SendAsync(req, ct), ct), 1).FirstOrDefault();
    }

    public static async Task<IReadOnlyList<LibraryGame>> GetRecentAsync(HttpClient http, string authorization, string xuid,
        string locale, int max = 24, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://titlehub.xboxlive.com/users/xuid({xuid})/titles/titlehistory/decoration/image?maxItems=60");
        req.Headers.Add("Authorization", authorization);
        req.Headers.Add("x-xbl-contract-version", "2");
        req.Headers.Add("Accept-Language", locale);
        var j = await XboxAuth.ReadJson(await http.SendAsync(req, ct), ct);
        return Parse(j, max);
    }

    public static IReadOnlyList<LibraryGame> Parse(JsonNode j, int max = 24)
    {
        var games = new List<LibraryGame>();
        foreach (var t in j["titles"]?.AsArray() ?? [])
        {
            if (t is null || t["type"]?.GetValue<string>() is not "Game") continue;
            var devices = t["devices"]?.AsArray().Select(d => d?.GetValue<string>() ?? "").ToArray() ?? [];
            if (!devices.Any(d => d.StartsWith("Xbox", StringComparison.OrdinalIgnoreCase))) continue;

            var images = (t["images"]?.AsArray() ?? []).Where(i => i is not null)
                .Select(i => (Type: i!["type"]?.GetValue<string>() ?? "", Url: i["url"]?.GetValue<string>() ?? ""))
                .Where(i => i.Url.Length > 0).ToList();
            string? hero = HeroTypes.Select(type => images.FirstOrDefault(i => i.Type == type).Url).FirstOrDefault(u => u is not null);
            if (hero is null) continue;
            string? logo = images.FirstOrDefault(i => i.Type == "Logo").Url;

            DateTimeOffset.TryParse(t["titleHistory"]?["lastTimePlayed"]?.GetValue<string>(), out var played);
            games.Add(new LibraryGame(t["titleId"]?.ToString() ?? "", t["name"]?.GetValue<string>() ?? "", Https(hero),
                logo is null ? null : Https(logo), played));
        }
        return games.OrderByDescending(g => g.LastPlayed).Take(max).ToList();
    }

    /// <summary>Store image URL at a given width (the image service resizes on request).</summary>
    public static string Sized(string url, int width) =>
        url.Contains("store-images.s-microsoft.com", StringComparison.OrdinalIgnoreCase)
            ? url + (url.Contains('?') ? "&" : "?") + $"w={width}"
            : url;

    private static string Https(string url) => url.StartsWith("//") ? "https:" + url : url.Replace("http://", "https://");
}
