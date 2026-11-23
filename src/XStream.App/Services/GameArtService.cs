using System.Text.Json;
using XStream.Core.Config;
using XStream.Core.Xbox;

namespace XStream.App.Services;

/// <summary>A game's key art saved on disk (Url is the original, for Discord).</summary>
public sealed record GameArt(string Name, string Path, string TitleId = "", string Url = "");

/// <summary>What the account is running on an Xbox right now, with its art if we could get it.</summary>
public sealed record NowPlaying(string TitleId, string Name, GameArt? Art);

/// <summary>
/// Key art from the account's recently played console games, cached in ~/.xstream/art so the connecting
/// screen and console cards have pictures straight away (and offline). Refreshed in the background once
/// per launch; what's running now (presence) is looked up on each console refresh.
/// </summary>
public sealed class GameArtService(XboxService xbox)
{
    private const int MaxGames = 12;
    private const int Width = 1920;
    private static readonly string Index = Path.Combine(AppPaths.Art, "library.json");
    private Task? _refresh;

    /// <summary>Cached art, most recently played first. Empty until something has been downloaded.</summary>
    public IReadOnlyList<GameArt> Items { get; private set; } = LoadIndex();

    /// <summary>Last presence result (null: nothing running, offline, or not looked up yet).</summary>
    public NowPlaying? Current { get; private set; }

    /// <summary>Items or Current changed. Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Starts a refresh unless one already ran this launch.</summary>
    public Task RefreshSoon(AppSettings settings) => _refresh ??= RefreshAsync(settings);

    /// <summary>The art in slideshow order with this title first, if we have it.</summary>
    public IReadOnlyList<GameArt> StartingWith(string? titleId)
    {
        var items = Items;
        if (string.IsNullOrEmpty(titleId)) return items;
        var first = items.FirstOrDefault(i => i.TitleId == titleId) ?? (Current?.TitleId == titleId ? Current.Art : null);
        return first is null ? items : [first, .. items.Where(i => i.Path != first.Path)];
    }

    public void Forget()
    {
        _refresh = null;
        Items = [];
        Current = null;
        try { File.Delete(Index); } catch (IOException) { }
        Prune([]); // another account shouldn't see this one's games
        Changed?.Invoke();
    }

    /// <summary>Looks up what's running on the account's Xbox (and fetches that game's art if it's new).</summary>
    public async Task<NowPlaying?> RefreshNowPlayingAsync(AppSettings settings)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var p = await xbox.GetNowPlayingAsync(cts.Token);
            NowPlaying? now = null;
            if (p is { } playing)
            {
                var art = Items.FirstOrDefault(i => i.TitleId == playing.TitleId);
                if (art is null)
                {
                    var game = await xbox.GetTitleAsync(playing.TitleId, settings, cts.Token);
                    if (game is not null) art = await DownloadAsync(game, cts.Token);
                }
                now = new NowPlaying(playing.TitleId, playing.Name.Length > 0 ? playing.Name : art?.Name ?? "", art);
            }
            if (now != Current) { Current = now; Changed?.Invoke(); }
            return now;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("now playing: " + ex.Message);
            return Current;
        }
    }

    private async Task RefreshAsync(AppSettings settings)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var games = (await xbox.GetLibraryAsync(settings, cts.Token)).Take(MaxGames).ToList();
            var items = new List<GameArt>();
            foreach (var g in games)
                if (await DownloadAsync(g, cts.Token) is { } art) items.Add(art);
            if (items.Count == 0) return;
            Items = items;
            await File.WriteAllTextAsync(Index, JsonSerializer.Serialize(items));
            var keep = items.ToList();
            if (Current?.Art is { } now) keep.Add(now);
            Prune(keep);
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // Art is a nice-to-have: keep whatever is cached. Allow another try on the next stream.
            Console.Error.WriteLine("game art: " + ex.Message);
            _refresh = null;
        }
    }

    private async Task<GameArt?> DownloadAsync(LibraryGame g, CancellationToken ct)
    {
        string file = Path.Combine(AppPaths.Art, SafeName(g.TitleId) + ".jpg");
        if (!File.Exists(file))
        {
            try
            {
                var bytes = await xbox.DownloadAsync(XboxLibrary.Sized(g.HeroUrl, Width), ct);
                string tmp = file + "." + Environment.ProcessId + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct);
                File.Move(tmp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { return null; }
        }
        return new GameArt(g.Name, file, g.TitleId, g.HeroUrl);
    }

    private static IReadOnlyList<GameArt> LoadIndex()
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<GameArt>>(File.ReadAllText(Index)) ?? [];
            return items.Where(i => File.Exists(i.Path)).ToList();
        }
        catch { return []; }
    }

    private static void Prune(List<GameArt> keep)
    {
        var wanted = keep.Select(k => Path.GetFullPath(k.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(AppPaths.Art, "*.jpg"))
            if (!wanted.Contains(Path.GetFullPath(f))) { try { File.Delete(f); } catch (IOException) { } }
    }

    private static string SafeName(string s) => string.Concat(s.Where(char.IsLetterOrDigit)) is { Length: > 0 } n ? n : "game";
}
