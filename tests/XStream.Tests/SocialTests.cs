using System.Text.Json.Nodes;
using XStream.Core.Config;
using XStream.Core.Xbox;

/// <summary>Friends, presence and achievements: parsers (sample JSON shaped like live replies) and the stream's unlock tracking.</summary>
static class SocialTests
{
    private static void Eq<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}");
    }
    private static void True(bool c, string what) { if (!c) throw new Exception("expected true: " + what); }

    // peoplehub /users/me/people/social/decoration/detail,preferredColor,presenceDetail (contract 3), trimmed.
    private const string People = """
        {"people":[
          {"xuid":"1","gamertag":"Offline Old","displayPicRaw":"https://images-eds-ssl.xboxlive.com/image?url=abc&format=png","isFavorite":false,
           "presenceState":"Offline","presenceText":"Last seen 4d ago: Xbox App","presenceDetails":[],"lastSeenDateTimeUtc":"2026-11-22T10:00:00.1234567"},
          {"xuid":"2","gamertag":"Racer","displayPicRaw":"https://images-eds-ssl.xboxlive.com/image?url=def&format=png","isFavorite":false,
           "presenceState":"Online","presenceText":"Forza Horizon 5 - Racing in Mexico","lastSeenDateTimeUtc":null,
           "preferredColor":{"primaryColor":"107c10"},
           "presenceDetails":[
             {"IsBroadcasting":false,"Device":"Scarlett","PresenceText":"Home","State":"Active","TitleId":"750323071","IsPrimary":false,"IsGame":false,"RichPresenceText":null},
             {"IsBroadcasting":false,"Device":"Scarlett","PresenceText":"Forza Horizon 5 - Racing in Mexico","State":"Active","TitleId":"2030093255","IsPrimary":true,"IsGame":true,"RichPresenceText":"Racing in Mexico"}]},
          {"xuid":"3","gamertag":"Chatter","displayPicRaw":null,"isFavorite":true,"presenceState":"Online","presenceText":"Online",
           "presenceDetails":[{"Device":"WindowsOneCore","PresenceText":"Online","State":"Active","TitleId":"704208617","IsPrimary":true,"IsGame":false,"RichPresenceText":null}]},
          {"xuid":"4","gamertag":"Never Seen","presenceState":"Offline","presenceText":"Offline","lastSeenDateTimeUtc":null},
          {"xuid":"5","gamertag":"Offline Recent","presenceState":"Offline","presenceText":"Last seen 2h ago: Halo","lastSeenDateTimeUtc":"2026-11-26T10:00:00Z"},
          {"xuid":"6","gamertag":"","presenceState":"Online"}
        ]}
        """;

    // titlehub titlehistory/decoration/achievement,image (contract 2): totalAchievements is 0 for started games (seen live).
    private const string Titles = """
        {"xuid":"9","titles":[
          {"titleId":"893395429","name":"Shooter","type":"Game","devices":["XboxSeries"],"displayImage":"http://store-images.s-microsoft.com/image/apps.1",
           "images":[{"type":"BoxArt","url":"https://x/box.png"},{"type":"SuperHeroArt","url":"https://x/hero.jpg"}],
           "achievement":{"currentAchievements":25,"totalAchievements":0,"currentGamerscore":900,"totalGamerscore":1000,"progressPercentage":90.0},
           "titleHistory":{"lastTimePlayed":"2026-11-25T21:52:16.3827713Z"}},
          {"titleId":"2035456122","name":"Newest","type":"Game","devices":["XboxSeries"],"displayImage":"https://x/d.png","images":[],
           "achievement":{"currentAchievements":4,"totalAchievements":0,"currentGamerscore":55,"totalGamerscore":1000,"progressPercentage":5.0},
           "titleHistory":{"lastTimePlayed":"2026-11-26T11:21:58.185516Z"}},
          {"titleId":"1","name":"No Achievements","type":"Game","devices":["PC"],
           "achievement":{"currentAchievements":0,"totalAchievements":0,"currentGamerscore":0,"totalGamerscore":0,"progressPercentage":0.0}},
          {"titleId":"2","name":"An App","type":"Application","achievement":{"totalGamerscore":100}}
        ]}
        """;

    // achievements.xboxlive.com /users/xuid(x)/achievements?titleId=… (contract 2), trimmed.
    private const string Achievements = """
        {"achievements":[
          {"id":"1","name":"Locked Easy","progressState":"NotStarted","isSecret":false,"description":"Do it","lockedDescription":"Do it",
           "progression":{"requirements":[{"current":"0","target":"100"}],"timeUnlocked":"0001-01-01T00:00:00.0000000Z"},
           "rewards":[{"type":"Gamerscore","value":"15","valueType":"Int"}],"mediaAssets":[{"name":"i","type":"Icon","url":"https://images-eds-ssl.xboxlive.com/image?url=i1"}]},
          {"id":"2","name":"Old Unlock","progressState":"Achieved","isSecret":false,"description":"Done long ago","lockedDescription":"Hidden text",
           "progression":{"requirements":[],"timeUnlocked":"2023-11-03T22:20:51.0200000Z"},
           "rewards":[{"type":"Gamerscore","value":"15","valueType":"Int"}],"mediaAssets":[{"type":"Icon","url":"https://x/2.png"}]},
          {"id":"3","name":"Almost","progressState":"InProgress","isSecret":false,"description":"Get 100","lockedDescription":"Get 100",
           "progression":{"requirements":[{"current":"80","target":"100"}],"timeUnlocked":"0001-01-01T00:00:00.0000000Z"},
           "rewards":[{"type":"Gamerscore","value":"30","valueType":"Int"}],"mediaAssets":[]},
          {"id":"4","name":"Recent Unlock","progressState":"Achieved","isSecret":true,"description":"Shh","lockedDescription":"Secret",
           "progression":{"requirements":[],"timeUnlocked":"2025-07-12T15:26:45.0000000Z"},
           "rewards":[{"type":"Gamerscore","value":"20","valueType":"Int"},{"type":"Art","value":"x"}],"mediaAssets":[]}
        ],"pagingInfo":{"continuationToken":null,"totalRecords":4}}
        """;

    private static Achievement A(string id, bool unlocked, DateTimeOffset? at = null, int score = 10) =>
        new(id, "A" + id, "", "", score, unlocked, at, null, false);

    public static IEnumerable<(string, Action)> All =>
    [
        ("Friends: presence parsed and online first", () =>
        {
            var friends = XboxSocial.SortFriends(XboxSocial.ParseFriends(JsonNode.Parse(People)!));
            Eq(5, friends.Count); // the one without a gamertag is skipped
            Eq("Racer", friends[0].Gamertag);
            Eq("Forza Horizon 5", friends[0].GameName);
            Eq("Racing in Mexico", friends[0].RichPresence);
            Eq("2030093255", friends[0].TitleId);
            Eq("107c10", friends[0].Color);
            Eq("Chatter", friends[1].Gamertag);
            True(friends[1].IsOnline && !friends[1].IsPlaying && friends[1].IsFavorite, "online, not in a game, favourite");
            // Offline: most recently seen first, never-seen last.
            Eq("Offline Recent", friends[2].Gamertag);
            Eq("Offline Old", friends[3].Gamertag);
            Eq("Never Seen", friends[4].Gamertag);
            var now = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
            Eq("Playing Forza Horizon 5", friends[0].Status(now));
            Eq("Online", friends[1].Status(now));
            Eq("Last seen 2h ago", friends[2].Status(now));
            Eq("Last seen 4d ago", friends[3].Status(now)); // zone-less time read as UTC
            Eq("Offline", friends[4].Status(now));
        }),
        ("Friends: relative times", () =>
        {
            Eq("just now", XboxSocial.Ago(TimeSpan.FromSeconds(20)));
            Eq("5m ago", XboxSocial.Ago(TimeSpan.FromMinutes(5.5)));
            Eq("23h ago", XboxSocial.Ago(TimeSpan.FromHours(23.9)));
            Eq("3d ago", XboxSocial.Ago(TimeSpan.FromDays(3)));
            Eq("2mo ago", XboxSocial.Ago(TimeSpan.FromDays(70)));
            Eq("1y ago", XboxSocial.Ago(TimeSpan.FromDays(400)));
        }),
        ("Picture thumbnails use sizes the image service accepts", () =>
        {
            Eq("https://images-eds-ssl.xboxlive.com/image?url=abc&format=jpg&w=128&h=128",
                XboxSocial.Thumb("https://images-eds-ssl.xboxlive.com/image?url=abc&format=png", 128));
            Eq("https://store-images.s-microsoft.com/image/x?w=208", XboxSocial.Thumb("https://store-images.s-microsoft.com/image/x", 208));
            Eq("https://other/x.png", XboxSocial.Thumb("https://other/x.png"));
        }),
        ("Achievements: games with gamerscore progress", () =>
        {
            var games = XboxSocial.ParseGames(JsonNode.Parse(Titles)!);
            Eq(2, games.Count); // no-achievement game and the app left out
            Eq("2035456122", games[0].TitleId); // most recently played first
            var g = games[1];
            Eq(900, g.Gamerscore); Eq(1000, g.TotalGamerscore); Eq(25, g.Unlocked); Eq(90.0, g.Percent);
            Eq("https://store-images.s-microsoft.com/image/apps.1", g.ImageUrl);
            Eq("https://x/hero.jpg", g.HeroUrl);
        }),
        ("Achievements: a game's list, unlocked first with dates", () =>
        {
            var list = XboxSocial.ParseAchievements(JsonNode.Parse(Achievements)!);
            Eq(4, list.Count);
            Eq("4", list[0].Id); // newest unlock first
            Eq("2", list[1].Id);
            Eq("3", list[2].Id); // locked: furthest along first
            Eq("1", list[3].Id);
            Eq(20, list[0].Gamerscore); // only gamerscore rewards count
            True(list[0].IsSecret && list[0].Unlocked, "secret, unlocked");
            Eq(new DateTimeOffset(2023, 11, 3, 22, 20, 51, 20, TimeSpan.Zero), list[1].UnlockedAt!.Value);
            True(list[3].UnlockedAt is null && !list[3].Unlocked, "locked has no date (0001-01-01 ignored)");
            Eq((int?)80, list[2].ProgressCurrent); Eq((int?)100, list[2].ProgressTarget);
            Eq("https://images-eds-ssl.xboxlive.com/image?url=i1", list[3].IconUrl);
            Eq("Achievement unlocked: Recent Unlock · 20G", list[0].UnlockText);
        }),
        ("Achievements: only unlocks since the stream started are new", () =>
        {
            var start = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
            var t = new AchievementTracker(start);
            // First look: what's unlocked already is the starting point.
            Eq(0, t.Update("100", [A("1", true, start.AddDays(-3)), A("2", false), A("3", false)]).Count);
            True(t.Knows("100"), "game seen");
            Eq(0, t.Update("100", [A("1", true, start.AddDays(-3)), A("2", false)]).Count);
            // One unlocks: reported once.
            var fresh = t.Update("100", [A("1", true, start.AddDays(-3)), A("2", true, start.AddMinutes(5)), A("3", false)]);
            Eq(1, fresh.Count); Eq("2", fresh[0].Id);
            Eq(0, t.Update("100", [A("1", true), A("2", true, start.AddMinutes(5))]).Count);
            // Two at once: oldest first.
            fresh = t.Update("100", [A("3", true, start.AddMinutes(9)), A("4", true, start.AddMinutes(8))]);
            Eq("4", fresh[0].Id); Eq("3", fresh[1].Id);
        }),
        ("Achievements: a game switched to mid-stream gets its own starting point", () =>
        {
            var start = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
            var t = new AchievementTracker(start);
            t.Update("100", [A("1", true, start.AddDays(-1))]);
            // Another game: old unlocks aren't new, but one unlocked after the stream began (before the first look) is.
            var fresh = t.Update("200", [A("1", true, start.AddDays(-9)), A("2", true, start.AddSeconds(20))]);
            Eq(1, fresh.Count); Eq("2", fresh[0].Id);
            // Same achievement id in the first game is a different achievement and still known there.
            Eq(0, t.Update("100", [A("1", true, start.AddDays(-1))]).Count);
            // Unlocked-only replies also carry in-progress ones: those never count.
            Eq(0, t.Update("200", [A("3", false)]).Count);
        }),
        ("Presence for a cloud stream: console game first, else any device's game", () =>
        {
            Eq("123", XboxSocial.ParsePresenceAnyDevice(JsonNode.Parse("""
                {"devices":[{"type":"WindowsOneCore","titles":[{"id":"704208617","placement":"Full","state":"Active"}]},
                            {"type":"Scarlett","titles":[{"id":"750323071","placement":"Background","state":"Active"},{"id":"123","name":"G","placement":"Full","state":"Active"}]}]}
                """)!));
            Eq("555", XboxSocial.ParsePresenceAnyDevice(JsonNode.Parse("""
                {"devices":[{"type":"WindowsOneCore","titles":[{"id":"704208617","placement":"Full","state":"Active"},{"id":"555","placement":"Full","state":"Active"}]}]}
                """)!));
            Eq((string?)null, XboxSocial.ParsePresenceAnyDevice(JsonNode.Parse("""
                {"devices":[{"type":"Scarlett","titles":[{"id":"750323071","placement":"Background","state":"Active"}]}]}
                """)!));
            Eq((string?)null, XboxSocial.ParsePresenceAnyDevice(JsonNode.Parse("""{"xuid":"1","state":"Offline"}""")!));
        }),
        ("Achievement alerts are on by default and kept across saves", () =>
        {
            True(new AppSettings().AchievementAlerts, "default on");
            string path = Path.Combine(Path.GetTempPath(), $"xstream-ach-{Environment.ProcessId}.json");
            try
            {
                (new AppSettings() with { AchievementAlerts = false }).Save(path);
                True(!AppSettings.Load(path).AchievementAlerts, "off survives a reload");
                File.WriteAllText(path, """{"SettingsVersion":4}""");
                True(AppSettings.Load(path).AchievementAlerts, "an older file without the field gets the default");
            }
            finally { File.Delete(path); }
        }),
    ];
}
