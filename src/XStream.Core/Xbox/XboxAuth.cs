using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XStream.Core.Xbox;

public sealed record DeviceCodePrompt(string UserCode, string VerificationUri, string DeviceCode, int IntervalSec, int ExpiresInSec);

public sealed record XboxTokens(
    string MsaAccessToken, string MsaRefreshToken, DateTimeOffset MsaExpires,
    string? GsToken = null, string? StreamHost = null, DateTimeOffset GsExpires = default,
    // Xbox Cloud Gaming: the same login with offering xgpuweb (Game Pass) or xgpuwebf2p (free-to-play only).
    string? CloudToken = null, string? CloudHost = null, DateTimeOffset CloudExpires = default, string? CloudOffering = null);

/// <summary>
/// Microsoft account -> Xbox Live -> streaming token chain, the same public flow Greenlight
/// (xal-node) uses. Device-code sign-in works well on a handheld: the ROG Ally shows a short code,
/// you approve it on your phone.
/// </summary>
public sealed class XboxAuth(HttpClient http)
{
    // Public client id used by xal-node / Greenlight for the consumer device-code flow.
    public const string ClientId = "1f907974-e22b-4810-a9de-d9647380c97e";
    private const string Scope = "xboxlive.signin openid profile offline_access";

    public async Task<DeviceCodePrompt> StartDeviceCodeAsync(CancellationToken ct = default)
    {
        var res = await http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            Form(("client_id", ClientId), ("scope", Scope)), ct);
        var j = await ReadJson(res, ct);
        return new DeviceCodePrompt(j["user_code"]!.GetValue<string>(), j["verification_uri"]!.GetValue<string>(),
            j["device_code"]!.GetValue<string>(), j["interval"]?.GetValue<int>() ?? 5, j["expires_in"]?.GetValue<int>() ?? 900);
    }

    public async Task<XboxTokens> PollDeviceCodeAsync(DeviceCodePrompt prompt, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(prompt.ExpiresInSec);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(prompt.IntervalSec), ct);
            var res = await http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                Form(("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), ("client_id", ClientId),
                     ("device_code", prompt.DeviceCode)), ct);
            if (res.IsSuccessStatusCode) return MsaFrom(await ReadJson(res, ct));
            var err = await ReadJson(res, ct, throwOnError: false);
            var code = err["error"]?.GetValue<string>();
            if (code is "authorization_pending") continue;
            if (code is "slow_down") { await Task.Delay(5_000, ct); continue; }
            throw new XboxApiException($"Sign-in failed: {code}", (int)res.StatusCode);
        }
        throw new TimeoutException("Sign-in code expired");
    }

    public async Task<XboxTokens> RefreshAsync(XboxTokens t, CancellationToken ct = default)
    {
        var res = await http.PostAsync("https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            Form(("client_id", ClientId), ("grant_type", "refresh_token"), ("refresh_token", t.MsaRefreshToken), ("scope", Scope)), ct);
        return MsaFrom(await ReadJson(res, ct));
    }

    /// <summary>
    /// "LPT" console-transfer token, posted to /connect when a session reaches ReadyToConnect.
    /// </summary>
    public async Task<string> GetTransferTokenAsync(XboxTokens t, CancellationToken ct = default)
    {
        var res = await http.PostAsync("https://login.live.com/oauth20_token.srf",
            Form(("client_id", ClientId), ("scope", "service::http://Passport.NET/purpose::PURPOSE_XBOX_CLOUD_CONSOLE_TRANSFER_TOKEN"),
                 ("grant_type", "refresh_token"), ("refresh_token", t.MsaRefreshToken)), ct);
        return (await ReadJson(res, ct))["access_token"]!.GetValue<string>();
    }

    /// <summary>MSA token -> Xbox user token -> XSTS (gssv) -> xHome streaming token + host.</summary>
    public async Task<XboxTokens> GetStreamingTokenAsync(XboxTokens t, string offering = "xhome", CancellationToken ct = default)
    {
        if (t.MsaExpires < DateTimeOffset.UtcNow.AddMinutes(1)) t = await RefreshAsync(t, ct);

        var xsts = await GetXstsAsync(t.MsaAccessToken, "http://gssv.xboxlive.com/", ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{offering}.gssv-play-prod.xboxlive.com/v2/login/user")
        {
            // A plain string body (with Content-Length): the cloud login servers reject a streamed (chunked) body as empty.
            Content = new StringContent(JsonSerializer.Serialize(new { token = xsts["Token"]!.GetValue<string>(), offeringId = offering }),
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-gssv-client", "XboxComBrowser");
        var gs = await ReadJson(await http.SendAsync(req, ct), ct);

        var regions = gs["offeringSettings"]?["regions"]?.AsArray() ?? [];
        var region = regions.FirstOrDefault(r => r?["isDefault"]?.GetValue<bool>() == true) ?? regions.FirstOrDefault();
        return t with
        {
            GsToken = gs["gsToken"]!.GetValue<string>(),
            StreamHost = region?["baseUri"]?.GetValue<string>() ?? $"https://{offering}.gssv-play-prod.xboxlive.com",
            GsExpires = DateTimeOffset.UtcNow.AddSeconds(gs["durationInSeconds"]?.GetValue<int>() ?? 3600),
        };
    }

    /// <summary>
    /// Token for the Xbox Live web services (title history, game art): "XBL3.0 x={uhs};{token}" plus the
    /// account's xuid. Refreshes the account token first if it has expired (the caller saves the result).
    /// </summary>
    public async Task<(string Authorization, string Xuid, XboxTokens Tokens)> GetWebTokenAsync(XboxTokens t, CancellationToken ct = default)
    {
        if (t.MsaExpires < DateTimeOffset.UtcNow.AddMinutes(1)) t = await RefreshAsync(t, ct);
        var xsts = await GetXstsAsync(t.MsaAccessToken, "http://xboxlive.com", ct);
        var xui = xsts["DisplayClaims"]?["xui"]?[0];
        string uhs = xui?["uhs"]?.GetValue<string>() ?? "";
        string xuid = xui?["xid"]?.GetValue<string>() ?? "";
        return ($"XBL3.0 x={uhs};{xsts["Token"]!.GetValue<string>()}", xuid, t);
    }

    private async Task<JsonNode> GetXstsAsync(string msaAccessToken, string relyingParty, CancellationToken ct)
    {
        var user = await PostXbl("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", RpsTicket = "d=" + msaAccessToken, SiteName = "user.auth.xboxlive.com" },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        }, ct);
        return await PostXbl("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { user["Token"]!.GetValue<string>() } },
            RelyingParty = relyingParty,
            TokenType = "JWT",
        }, ct);
    }

    private async Task<JsonNode> PostXbl(string url, object body, CancellationToken ct)
    {
        // Xbox Live auth is case-sensitive: keep PascalCase names (JsonContent's default is camelCase -> HTTP 400).
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: XblJson) };
        req.Headers.Add("x-xbl-contract-version", "1");
        req.Headers.Add("Origin", "https://www.xbox.com");
        req.Headers.Add("Referer", "https://www.xbox.com/");
        return await ReadJson(await http.SendAsync(req, ct), ct);
    }

    private static readonly JsonSerializerOptions XblJson = new() { PropertyNamingPolicy = null };

    private static XboxTokens MsaFrom(JsonNode j) => new(
        j["access_token"]!.GetValue<string>(), j["refresh_token"]!.GetValue<string>(),
        DateTimeOffset.UtcNow.AddSeconds(j["expires_in"]?.GetValue<int>() ?? 3600));

    private static FormUrlEncodedContent Form(params (string k, string v)[] kv) =>
        new(kv.Select(x => new KeyValuePair<string, string>(x.k, x.v)));

    internal static async Task<JsonNode> ReadJson(HttpResponseMessage res, CancellationToken ct, bool throwOnError = true)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        if (throwOnError && !res.IsSuccessStatusCode)
            throw new XboxApiException($"{res.RequestMessage?.RequestUri?.AbsolutePath}: HTTP {(int)res.StatusCode} {text}", (int)res.StatusCode);
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject { ["raw"] = text }; }
    }
}

public sealed class XboxApiException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}
