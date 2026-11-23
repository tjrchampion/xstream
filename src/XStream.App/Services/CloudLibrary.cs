using System.Text.Json;
using XStream.Core.Config;
using XStream.Core.Xbox;

namespace XStream.App.Services;

/// <summary>
/// Xbox Cloud Gaming library for the Cloud page: which offering the account has (Game Pass or free-to-play), the
/// playable games with names and art, and recently played. Cached in ~/.xstream/cloud-library.json so the page
/// fills straight away; refreshed in the background. Cover art is downloaded on demand and cached on disk.
/// </summary>
public sealed class CloudLibrary(XboxService xbox)
{
    private static readonly string CacheFile = Path.Combine(AppPaths.Root, "cloud-library.json");
    private static string ArtDir => Directory.CreateDirectory(Path.Combine(AppPaths.Art, "cloud")).FullName;
    private readonly SemaphoreSlim _artGate = new(6);

    public sealed record Snapshot(string Offering, IReadOnlyList<CloudGame> Games, IReadOnlyList<string> RecentTitleIds, DateTimeOffset Updated);

    public Snapshot? Cached { get; } = Load();

    /// <summary>Fetch the library from the service (throws if cloud gaming isn't available to the account).</summary>
    public async Task<Snapshot> RefreshAsync(AppSettings settings, CancellationToken ct = default)
    {
        var (offering, games, recent) = await xbox.GetCloudLibraryAsync(settings, ct);
        var snap = new Snapshot(offering, games.Where(g => g.Entitled).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            recent, DateTimeOffset.UtcNow);
        try { await File.WriteAllTextAsync(CacheFile, JsonSerializer.Serialize(snap), ct); } catch (IOException) { }
        return snap;
    }

    public static void Forget()
    {
        try { File.Delete(CacheFile); } catch (IOException) { }
        try { Directory.Delete(ArtDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Local path of a game's art (poster for tiles, hero for the connecting screen), downloading it once.</summary>
    public async Task<string?> ArtAsync(CloudGame g, bool hero = false)
    {
        string? url = hero ? g.HeroUrl ?? g.PosterUrl : g.PosterUrl ?? g.TileUrl;
        if (url is null) return null;
        string file = Path.Combine(ArtDir, $"{Safe(g.ProductId.Length > 0 ? g.ProductId : g.TitleId)}{(hero ? "-hero" : "")}.jpg");
        if (File.Exists(file)) return file;
        await _artGate.WaitAsync();
        try
        {
            if (File.Exists(file)) return file;
            var bytes = await xbox.DownloadAsync(XboxLibrary.Sized(url, hero ? 1920 : 300), CancellationToken.None);
            string tmp = file + "." + Environment.ProcessId + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);
            File.Move(tmp, file, overwrite: true);
            return file;
        }
        catch { return null; }
        finally { _artGate.Release(); }
    }

    private static Snapshot? Load()
    {
        try { return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(CacheFile)); }
        catch { return null; }
    }

    private static string Safe(string s) => string.Concat(s.Where(char.IsLetterOrDigit));
}
