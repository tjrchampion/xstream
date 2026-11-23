using System.Text.Json;
using XStream.Core.Xbox;

namespace XStream.Core.Config;

/// <summary>Saved sign-in (~/.xstream/tokens.json) plus the refresh needed before talking to the streaming service.</summary>
public sealed class TokenStore(XboxAuth auth, string? path = null)
{
    private readonly string _path = path ?? AppPaths.Tokens;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public bool SignedIn => File.Exists(_path);

    public XboxTokens? Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<XboxTokens>(File.ReadAllText(_path)) : null; }
        catch (JsonException) { return null; }
    }

    public void Save(XboxTokens t) => File.WriteAllText(_path, JsonSerializer.Serialize(t));

    public void SignOut()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>Tokens with a streaming token valid for at least 5 more minutes (refreshed and saved if needed).</summary>
    public async Task<XboxTokens> GetFreshAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var t = Load() ?? throw new XboxApiException("Not signed in.", 401);
            if (t.GsToken is null || t.StreamHost is null || t.GsExpires < DateTimeOffset.UtcNow.AddMinutes(5))
            {
                t = await auth.GetStreamingTokenAsync(t, ct: ct);
                Save(t);
            }
            return t;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Tokens with an Xbox Cloud Gaming token valid for 5+ more minutes: Game Pass (xgpuweb) if the account has it,
    /// else free-to-play only (xgpuwebf2p). Throws if cloud gaming isn't available to the account or region.
    /// </summary>
    public async Task<XboxTokens> GetFreshCloudAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var t = Load() ?? throw new XboxApiException("Not signed in.", 401);
            if (t.CloudToken is null || t.CloudHost is null || t.CloudExpires < DateTimeOffset.UtcNow.AddMinutes(5))
            {
                XboxTokens? got = null;
                Exception? last = null;
                foreach (var offering in new[] { "xgpuweb", "xgpuwebf2p" })
                {
                    try { got = await auth.GetStreamingTokenAsync(t, offering, ct); t = got with { GsToken = t.GsToken, StreamHost = t.StreamHost, GsExpires = t.GsExpires, CloudToken = got.GsToken, CloudHost = got.StreamHost, CloudExpires = got.GsExpires, CloudOffering = offering }; break; }
                    catch (Exception ex) when (ex is XboxApiException or HttpRequestException) { last = ex; }
                }
                if (got is null) throw new XboxApiException("Xbox Cloud Gaming isn't available for this account or region" + (last is null ? "" : $" ({last.Message})"), 403);
                Save(t);
            }
            return t;
        }
        finally { _lock.Release(); }
    }

    /// <summary>A streaming client for Xbox Cloud Gaming (see <see cref="GetFreshCloudAsync"/>).</summary>
    public async Task<(XboxStreamingClient client, XboxTokens tokens)> CreateCloudClientAsync(HttpClient http, AppSettings settings, CancellationToken ct = default)
    {
        var t = await GetFreshCloudAsync(ct);
        return (new XboxStreamingClient(http, t.CloudHost!, t.CloudToken!) { Locale = settings.Locale, Resolution = StreamResolution.R1080 }, t);
    }

    /// <summary>Xbox Live web-services authorization + xuid (for title history / game art).</summary>
    public async Task<(string Authorization, string Xuid)> GetWebTokenAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var t = Load() ?? throw new XboxApiException("Not signed in.", 401);
            var (authz, xuid, fresh) = await auth.GetWebTokenAsync(t, ct);
            if (fresh != t) Save(fresh);
            return (authz, xuid);
        }
        finally { _lock.Release(); }
    }

    public async Task<(XboxStreamingClient client, XboxTokens tokens)> CreateClientAsync(HttpClient http, AppSettings settings, CancellationToken ct = default)
    {
        var t = await GetFreshAsync(ct);
        return (new XboxStreamingClient(http, t.StreamHost!, t.GsToken!) { Locale = settings.Locale, Resolution = settings.Resolution }, t);
    }
}
