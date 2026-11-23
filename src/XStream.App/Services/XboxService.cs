using XStream.Core.Config;
using XStream.Core.Xbox;

namespace XStream.App.Services;

/// <summary>Sign-in and console list for the launcher.</summary>
public sealed class XboxService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly XboxAuth _auth;
    private readonly TokenStore _store;

    public XboxService()
    {
        _auth = new XboxAuth(_http);
        _store = new TokenStore(_auth);
    }

    public bool SignedIn => _store.SignedIn;

    public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken ct) => _auth.StartDeviceCodeAsync(ct);

    /// <summary>Waits until the code is approved on the phone, then saves the account tokens.</summary>
    public async Task WaitForApprovalAsync(DeviceCodePrompt prompt, CancellationToken ct) =>
        _store.Save(await _auth.PollDeviceCodeAsync(prompt, ct));

    /// <summary>Exchanges the account tokens for a streaming token (also checks the account can stream).</summary>
    public Task FinishSignInAsync(CancellationToken ct) => _store.GetFreshAsync(ct);

    public async Task<IReadOnlyList<XboxConsole>> GetConsolesAsync(AppSettings settings, CancellationToken ct = default)
    {
        var (client, _) = await _store.CreateClientAsync(_http, settings, ct);
        return await client.GetConsolesAsync(ct);
    }

    /// <summary>Recently played console games with their key art.</summary>
    public async Task<IReadOnlyList<LibraryGame>> GetLibraryAsync(AppSettings settings, CancellationToken ct = default)
    {
        var (authz, xuid) = await _store.GetWebTokenAsync(ct);
        return await XboxLibrary.GetRecentAsync(_http, authz, xuid, settings.Locale, ct: ct);
    }

    /// <summary>What this account is running on an Xbox right now (presence).</summary>
    public async Task<(string TitleId, string Name)?> GetNowPlayingAsync(CancellationToken ct = default)
    {
        var (authz, xuid) = await _store.GetWebTokenAsync(ct);
        return await XboxLibrary.GetNowPlayingAsync(_http, authz, xuid, ct);
    }

    public async Task<LibraryGame?> GetTitleAsync(string titleId, AppSettings settings, CancellationToken ct = default)
    {
        var (authz, xuid) = await _store.GetWebTokenAsync(ct);
        return await XboxLibrary.GetTitleAsync(_http, authz, xuid, titleId, settings.Locale, ct);
    }

    /// <summary>
    /// Xbox Cloud Gaming: the offering this account gets (xgpuweb = Game Pass, xgpuwebf2p = free-to-play only),
    /// every title with names and art, and the recently played title ids.
    /// </summary>
    public async Task<(string Offering, IReadOnlyList<CloudGame> Games, IReadOnlyList<string> Recent)> GetCloudLibraryAsync(
        AppSettings settings, CancellationToken ct = default)
    {
        var (client, tokens) = await _store.CreateCloudClientAsync(_http, settings, ct);
        var all = await client.GetCloudTitlesAsync(recent: false, ct);
        IReadOnlyList<CloudTitle> recent = [];
        try { recent = await client.GetCloudTitlesAsync(recent: true, ct); } catch (XboxApiException) { }
        var games = await XboxLibrary.ResolveCloudGamesAsync(_http, all, settings.Locale, ct);
        return (tokens.CloudOffering ?? "xgpuweb", games, recent.Select(r => r.TitleId).ToList());
    }

    /// <summary>Downloads a URL's bytes (game art).</summary>
    public Task<byte[]> DownloadAsync(string url, CancellationToken ct) => _http.GetByteArrayAsync(url, ct);

    public void SignOut() => _store.SignOut();

    public void Dispose() => _http.Dispose();
}
