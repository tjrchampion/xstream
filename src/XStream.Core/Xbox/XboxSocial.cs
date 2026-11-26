using System.Globalization;
using System.Text.Json.Nodes;

namespace XStream.Core.Xbox;

/// <summary>A friend (someone the account follows) with what they're doing now.</summary>
/// <param name="GameName">The game they're playing (null when online but not in a game, or offline).</param>
/// <param name="RichPresence">What the game says they're doing ("Racing in Mexico"), if it says.</param>
/// <param name="ServerText">The service's own presence line ("Last seen 3d ago: Xbox App"), as a fallback.</param>
public sealed record Friend(string Xuid, string Gamertag, string? PictureUrl, bool IsOnline, bool IsFavorite,
    string? TitleId, string? GameName, string? RichPresence, string ServerText, DateTimeOffset? LastSeen, string? Color = null)
{
    public bool IsPlaying => IsOnline && GameName is { Length: > 0 };

    /// <summary>"Playing Forza Horizon 5", "Online", "Last seen 2h ago" (or "Offline" when the service doesn't say).</summary>
    public string Status(DateTimeOffset now) =>
        IsPlaying ? "Playing " + GameName
        : IsOnline ? "Online"
        : LastSeen is { } seen ? "Last seen " + XboxSocial.Ago(now - seen)
        : "Offline";
}

/// <summary>Achievement progress in one game (title history with the achievement decoration).</summary>
public sealed record GameProgress(string TitleId, string Name, string? ImageUrl, string? HeroUrl, int Gamerscore, int TotalGamerscore,
    int Unlocked, double Percent, DateTimeOffset LastPlayed);

/// <summary>One achievement in a game. Locked ones still have a description and their gamerscore.</summary>
public sealed record Achievement(string Id, string Name, string Description, string LockedDescription, int Gamerscore,
    bool Unlocked, DateTimeOffset? UnlockedAt, string? IconUrl, bool IsSecret, int? ProgressCurrent = null, int? ProgressTarget = null)
{
    /// <summary>"Achievement unlocked: Name · 20G" (the stream's pop-up).</summary>
    public string UnlockText => $"Achievement unlocked: {Name}" + (Gamerscore > 0 ? $" · {Gamerscore}G" : "");
}

/// <summary>
/// Friends, presence and achievements from Xbox Live (peoplehub, titlehub, achievements), read-only. Same
/// xboxlive.com sign-in as the library. Contracts from OpenXbox's xbox-webapi-python, checked against live replies.
/// HTTP and parsing are separate so the parsers can be tested with saved JSON.
/// </summary>
public sealed class XboxSocial(HttpClient http, Func<CancellationToken, Task<(string Authorization, string Xuid)>> token)
{
    public async Task<IReadOnlyList<Friend>> GetFriendsAsync(string locale, CancellationToken ct = default) =>
        SortFriends(ParseFriends(await GetAsync(_ =>
            "https://peoplehub.xboxlive.com/users/me/people/social/decoration/detail,preferredColor,presenceDetail", "3", locale, ct)));

    /// <summary>Recently played games that have achievements, most recent first.</summary>
    public async Task<IReadOnlyList<GameProgress>> GetGamesAsync(string locale, int max = 30, CancellationToken ct = default) =>
        ParseGames(await GetAsync(xuid =>
            $"https://titlehub.xboxlive.com/users/xuid({xuid})/titles/titlehistory/decoration/achievement,image?maxItems={Math.Max(max * 2, 40)}",
            "2", locale, ct), max);

    /// <summary>
    /// A game's achievements (modern titles; Xbox 360 ones use an older contract and come back empty here).
    /// <paramref name="unlockedOnly"/> is the cheap form the stream polls.
    /// </summary>
    public async Task<IReadOnlyList<Achievement>> GetAchievementsAsync(string titleId, string locale, bool unlockedOnly = false,
        CancellationToken ct = default) =>
        ParseAchievements(await GetAsync(xuid =>
            $"https://achievements.xboxlive.com/users/xuid({xuid})/achievements?titleId={Uri.EscapeDataString(titleId)}&maxItems=1000"
            + (unlockedOnly ? "&unlockedOnly=true" : ""), "2", locale, ct));

    /// <summary>What this account is running right now on any device (for cloud streams: the game's Xbox title id).</summary>
    public async Task<string?> GetNowPlayingAnyDeviceAsync(CancellationToken ct = default) =>
        ParsePresenceAnyDevice(await GetAsync(xuid => $"https://userpresence.xboxlive.com/users/xuid({xuid})?level=all", "3", "en-US", ct));

    private async Task<JsonNode> GetAsync(Func<string, string> url, string contract, string locale, CancellationToken ct)
    {
        try { return await Send(await AuthAsync(ct)); }
        catch (XboxApiException ex) when (ex.Status == 401) { _auth = null; return await Send(await AuthAsync(ct)); } // expired: once more

        async Task<JsonNode> Send((string Authorization, string Xuid) a)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url(a.Xuid));
            req.Headers.TryAddWithoutValidation("Authorization", a.Authorization);
            req.Headers.Add("x-xbl-contract-version", contract);
            req.Headers.Add("Accept-Language", locale);
            req.Headers.Add("Accept", "application/json");
            return await XboxAuth.ReadJson(await http.SendAsync(req, ct), ct);
        }
    }

    // The sign-in, reused for half an hour (getting one takes round trips; the stream polls every 30 s).
    private (string Authorization, string Xuid, DateTime At)? _auth;

    private async Task<(string, string)> AuthAsync(CancellationToken ct)
    {
        if (_auth is { } a && DateTime.UtcNow - a.At < TimeSpan.FromMinutes(30)) return (a.Authorization, a.Xuid);
        var (authz, xuid) = await token(ct);
        _auth = (authz, xuid, DateTime.UtcNow);
        return (authz, xuid);
    }

    // ---- Parsing ----------------------------------------------------------------------------------------

    public static IReadOnlyList<Friend> ParseFriends(JsonNode j)
    {
        var list = new List<Friend>();
        foreach (var p in j["people"]?.AsArray() ?? [])
        {
            if (p is null) continue;
            string tag = Str(p["gamertag"]) ?? Str(p["modernGamertag"]) ?? Str(p["displayName"]) ?? "";
            if (tag.Length == 0) continue;
            bool online = Str(p["presenceState"]) is "Online";
            string? titleId = null, game = null, rich = null;
            if (online)
            {
                // The primary entry is what they're doing; a game's PresenceText is "Game - rich presence".
                var details = (p["presenceDetails"] as JsonArray ?? []).Where(d => d is not null).ToList();
                var main = details.FirstOrDefault(d => d!["IsPrimary"]?.GetValue<bool>() == true && d["IsGame"]?.GetValue<bool>() == true)
                           ?? details.FirstOrDefault(d => d!["IsGame"]?.GetValue<bool>() == true);
                if (main is not null)
                {
                    titleId = Str(main["TitleId"]);
                    rich = Str(main["RichPresenceText"]);
                    game = Str(main["PresenceText"]);
                    if (game is not null && rich is not null && game.EndsWith(" - " + rich, StringComparison.Ordinal))
                        game = game[..^(rich.Length + 3)];
                }
            }
            list.Add(new Friend(Str(p["xuid"]) ?? "", tag, Str(p["displayPicRaw"]), online, p["isFavorite"]?.GetValue<bool>() == true,
                titleId, game, rich, Str(p["presenceText"]) ?? "", UtcDate(Str(p["lastSeenDateTimeUtc"])),
                Str(p["preferredColor"]?["primaryColor"])));
        }
        return list;
    }

    /// <summary>Playing first, then online, then offline (most recently seen first); favourites first within each; then by name.</summary>
    public static IReadOnlyList<Friend> SortFriends(IEnumerable<Friend> friends) =>
        friends.OrderBy(f => f.IsPlaying ? 0 : f.IsOnline ? 1 : 2)
            .ThenByDescending(f => f.IsFavorite)
            .ThenByDescending(f => f.IsOnline ? DateTimeOffset.MinValue : f.LastSeen ?? DateTimeOffset.MinValue)
            .ThenBy(f => f.Gamertag, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static readonly string[] HeroTypes = ["SuperHeroArt", "TitledHeroArt", "Screenshot", "BrandedKeyArt", "Poster", "BoxArt"];

    public static IReadOnlyList<GameProgress> ParseGames(JsonNode j, int max = 30)
    {
        var games = new List<GameProgress>();
        foreach (var t in j["titles"]?.AsArray() ?? [])
        {
            if (t is null || Str(t["type"]) is not "Game") continue;
            var a = t["achievement"];
            int total = Int(a?["totalGamerscore"]);
            if (total <= 0) continue; // no achievements to show
            var images = (t["images"] as JsonArray ?? []).Where(i => i is not null)
                .Select(i => (Type: Str(i!["type"]) ?? "", Url: Str(i["url"]) ?? "")).Where(i => i.Url.Length > 0).ToList();
            string? hero = HeroTypes.Select(type => images.FirstOrDefault(i => i.Type == type).Url).FirstOrDefault(u => u is not null);
            string? square = Str(t["displayImage"]) ?? images.FirstOrDefault(i => i.Type is "BoxArt" or "Poster").Url;
            games.Add(new GameProgress(Str(t["titleId"]) ?? "", Str(t["name"]) ?? "", Https(square), Https(hero),
                Int(a?["currentGamerscore"]), total, Int(a?["currentAchievements"]),
                a?["progressPercentage"] is JsonValue pv && pv.TryGetValue<double>(out var pct) ? pct : 0,
                UtcDate(Str(t["titleHistory"]?["lastTimePlayed"])) ?? DateTimeOffset.MinValue));
        }
        return games.OrderByDescending(g => g.LastPlayed).Take(max).ToList();
    }

    /// <summary>A game's achievements: unlocked first (newest first), then locked (closest to done, then least gamerscore).</summary>
    public static IReadOnlyList<Achievement> ParseAchievements(JsonNode j)
    {
        var list = new List<Achievement>();
        foreach (var x in j["achievements"]?.AsArray() ?? [])
        {
            if (x is null) continue;
            bool unlocked = Str(x["progressState"]) is "Achieved";
            DateTimeOffset? at = UtcDate(Str(x["progression"]?["timeUnlocked"]));
            if (at is { Year: <= 1 }) at = null;
            int score = (x["rewards"] as JsonArray ?? []).Where(r => Str(r?["type"]) is "Gamerscore").Sum(r => Int(r!["value"]));
            string? icon = (x["mediaAssets"] as JsonArray ?? []).Where(m => Str(m?["type"]) is "Icon").Select(m => Str(m!["url"])).FirstOrDefault();
            // Progress toward a locked one ("80 / 100"), when it has a single counted requirement.
            int? cur = null, target = null;
            if (!unlocked && x["progression"]?["requirements"] is JsonArray { Count: 1 } req
                && int.TryParse(Str(req[0]?["current"]), out var c) && int.TryParse(Str(req[0]?["target"]), out var tg) && tg > 1)
                (cur, target) = (Math.Clamp(c, 0, tg), tg);
            list.Add(new Achievement(Str(x["id"]) ?? "", Str(x["name"]) ?? "", Str(x["description"]) ?? "",
                Str(x["lockedDescription"]) ?? Str(x["description"]) ?? "", score, unlocked, unlocked ? at : null, Https(icon),
                x["isSecret"]?.GetValue<bool>() == true, cur, target));
        }
        return list.OrderByDescending(a => a.Unlocked)
            .ThenByDescending(a => a.UnlockedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(a => a.ProgressTarget is { } t && t > 0 ? (double)a.ProgressCurrent!.Value / t : 0)
            .ThenBy(a => a.Gamerscore)
            .ToList();
    }

    /// <summary>The title running on any of the account's devices (console first), skipping Home and background apps.</summary>
    public static string? ParsePresenceAnyDevice(JsonNode j)
    {
        if (XboxLibrary.ParsePresence(j) is { } console) return console.TitleId;
        foreach (var d in j["devices"]?.AsArray() ?? [])
            foreach (var t in d?["titles"]?.AsArray() ?? [])
            {
                string id = t?["id"]?.ToString() ?? "";
                if (id.Length == 0 || NotGames.Contains(id)) continue;
                if (Str(t!["placement"]) is "Background" || Str(t["state"]) is { } st && st != "Active") continue;
                return id;
            }
        return null;
    }

    // Xbox Home and the Xbox app on PC (seen live with IsGame false): presence that isn't a game.
    private static readonly HashSet<string> NotGames = ["750323071", "704208617"];

    /// <summary>
    /// A small copy of an Xbox Live picture (gamerpics are ~1.5 MB PNGs at full size). The image service keeps the
    /// aspect ratio inside the box but only accepts some sizes (64, 100, 128, 208 work; 96 doesn't), and needs a format
    /// with them. Store images take a width instead. Anything else is left as it is.
    /// </summary>
    public static string Thumb(string url, int size = 128)
    {
        if (url.Contains("images-eds-ssl.xboxlive.com", StringComparison.OrdinalIgnoreCase) && url.Split('?', 2) is [var path, var q])
        {
            var keep = q.Split('&').Where(p => !(p.StartsWith("format=") || p.StartsWith("w=") || p.StartsWith("h=") || p.StartsWith("mode=")));
            return path + "?" + string.Join("&", keep) + $"&format=jpg&w={size}&h={size}";
        }
        return XboxLibrary.Sized(url, size);
    }

    /// <summary>"2h ago", "3d ago", "just now".</summary>
    public static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "just now"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m ago"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours}h ago"
        : t.TotalDays < 30 ? $"{(int)t.TotalDays}d ago"
        : t.TotalDays < 365 ? $"{(int)(t.TotalDays / 30)}mo ago"
        : $"{(int)(t.TotalDays / 365)}y ago";

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? (s.Length > 0 ? s : null)
        : n is JsonValue num ? num.ToString() : null;

    private static int Int(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<int>(out var i) ? i
        : v.TryGetValue<double>(out var d) ? (int)d
        : int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0
        : 0;

    // Times come without a zone ("2026-11-22T16:01:47.3189793") or with Z: both are UTC.
    private static DateTimeOffset? UtcDate(string? s) =>
        s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d : null;

    private static string? Https(string? url) => url is null ? null : url.StartsWith("//") ? "https:" + url : url.Replace("http://", "https://");
}

/// <summary>
/// Spots achievements unlocked during a stream. The first look at a game records what's already unlocked (except
/// anything unlocked after <paramref name="since"/>, the stream's start, which counts as new); later looks report
/// only what's new since then, each once. A game switched to mid-stream gets its own starting point.
/// </summary>
public sealed class AchievementTracker(DateTimeOffset? since = null)
{
    private readonly Dictionary<string, HashSet<string>> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this game has been seen (its starting point recorded).</summary>
    public bool Knows(string titleId) => _known.ContainsKey(titleId);

    /// <summary>Unlocked achievements that weren't unlocked at the start (oldest first).</summary>
    public IReadOnlyList<Achievement> Update(string titleId, IEnumerable<Achievement> achievements)
    {
        var unlocked = achievements.Where(a => a.Unlocked && a.Id.Length > 0).ToList();
        if (!_known.TryGetValue(titleId, out var known))
        {
            bool During(Achievement a) => since is { } s && a.UnlockedAt is { } at && at > s;
            _known[titleId] = known = unlocked.Where(a => !During(a)).Select(a => a.Id).ToHashSet();
        }
        return unlocked.Where(a => known.Add(a.Id)).OrderBy(a => a.UnlockedAt ?? DateTimeOffset.MaxValue).ToList();
    }
}
