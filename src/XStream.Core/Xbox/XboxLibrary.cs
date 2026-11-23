using System.Text.Json.Nodes;

namespace XStream.Core.Xbox;

/// <summary>A console game from the account's play history, with its wide key art.</summary>
public sealed record LibraryGame(string TitleId, string Name, string HeroUrl, string? LogoUrl, DateTimeOffset LastPlayed);

/// <summary>A cloud game with its store name and art (catalog.gamepass.com).</summary>
public sealed record CloudGame(string TitleId, string ProductId, string Name, string Publisher, string? TileUrl, string? PosterUrl,
    string? HeroUrl, bool Entitled, bool FreeToPlay, IReadOnlyList<string>? Genres = null, DateTimeOffset? Released = null);

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

    /// <summary>
    /// Names and art for cloud games: Game Pass catalogue lookup by product id (batches of 100, no sign-in needed).
    /// Market and language come from the locale (en-GB -> GB / en-GB). Titles without catalogue data keep their id as name.
    /// </summary>
    public static async Task<IReadOnlyList<CloudGame>> ResolveCloudGamesAsync(HttpClient http, IReadOnlyList<CloudTitle> titles,
        string locale, CancellationToken ct = default)
    {
        string market = locale.Contains('-') ? locale[(locale.IndexOf('-') + 1)..].ToUpperInvariant() : "US";
        var info = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        // Only the games this account can play need names and art (Game Pass sees ~3,000 titles, most not playable).
        var ids = titles.Where(t => t.Entitled).Select(t => t.ProductId).Where(p => p.Length > 0).Distinct().ToList();
        // Small batches, a few at a time, one retry each: the catalogue's CDN times out on big requests now and then.
        // Names and art are nice-to-have: a batch that still fails keeps those titles' ids as names.
        const int batch = 25;
        using var gate = new SemaphoreSlim(4);
        var tasks = Enumerable.Range(0, (ids.Count + batch - 1) / batch).Select(async n =>
        {
            var chunk = ids.Skip(n * batch).Take(batch).ToList();
            await gate.WaitAsync(ct);
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Post,
                            $"https://catalog.gamepass.com/v3/products?market={market}&language={locale}&hydration=RemoteHighSapphire0")
                        {
                            Content = new StringContent(new JsonObject { ["Products"] = new JsonArray(chunk.Select(x => (JsonNode)x!).ToArray()) }.ToJsonString(),
                                System.Text.Encoding.UTF8, "application/json"),
                        };
                        req.Headers.Add("ms-cv", "0.0");
                        req.Headers.Add("calling-app-name", "Xbox Cloud Gaming Web");
                        req.Headers.Add("calling-app-version", "21.0.0");
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(15));
                        var j = await XboxAuth.ReadJson(await http.SendAsync(req, cts.Token), cts.Token);
                        if (j["Products"] is JsonObject products)
                            lock (info) foreach (var (pid, node) in products) if (node is not null) info[pid] = node.DeepClone();
                        return;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is XboxApiException or HttpRequestException or TaskCanceledException)
                    {
                        if (attempt == 1) Console.Error.WriteLine($"catalog: batch {n + 1} failed ({ex.Message.Split('\n')[0]})");
                    }
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return MergeCloudGames(titles, info);
    }

    public static IReadOnlyList<CloudGame> MergeCloudGames(IReadOnlyList<CloudTitle> titles, IReadOnlyDictionary<string, JsonNode> info)
    {
        static string? Img(JsonNode? n, string key) => n?[key]?["URL"]?.GetValue<string>() is { Length: > 0 } u ? Https(u) : null;
        return titles.Select(t =>
        {
            info.TryGetValue(t.ProductId, out var p);
            string name = p?["ProductTitle"]?.GetValue<string>() ?? t.TitleId;
            var genres = (p?["LocalizedCategories"] as JsonArray ?? p?["Categories"] as JsonArray)?
                .Select(g => g?.GetValue<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g!).ToList();
            DateTimeOffset? released = DateTimeOffset.TryParse(p?["OriginalReleaseDate"]?.GetValue<string>(), out var d) ? d : null;
            return new CloudGame(t.TitleId, t.ProductId, name, p?["PublisherName"]?.GetValue<string>() ?? "",
                Img(p, "Image_Tile"), Img(p, "Image_Poster"), Img(p, "Image_Hero") ?? Img(p, "Image_TitledHero"), t.Entitled, t.FreeToPlay,
                genres, released);
        }).ToList();
    }

    /// <summary>Store image URL at a given width (the image service resizes on request).</summary>
    public static string Sized(string url, int width) =>
        url.Contains("store-images.s-microsoft.com", StringComparison.OrdinalIgnoreCase)
            ? url + (url.Contains('?') ? "&" : "?") + $"w={width}"
            : url;

    private static string Https(string url) => url.StartsWith("//") ? "https:" + url : url.Replace("http://", "https://");
}
