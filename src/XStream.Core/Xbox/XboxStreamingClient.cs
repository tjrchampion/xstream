using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XStream.Core.Xbox;

/// <summary>Resolution the client asks for: Max lets the console send its highest tier (1440p/4K where offered).</summary>
public enum StreamResolution { Max, R1080, R720 }

public sealed record XboxConsole(string DeviceName, string ServerId, string PowerState, string ConsoleType,
    bool OutOfHomeWarning, bool WirelessWarning);

public sealed record IceCandidate(string Candidate, string? SdpMid, ushort SdpMLineIndex);

/// <summary>A game playable on Xbox Cloud Gaming for this account (from /v2/titles).</summary>
public sealed record CloudTitle(string TitleId, string ProductId, bool Entitled, bool FreeToPlay);

/// <summary>
/// xHome (console streaming) REST API, ported from xbox-xcloud-player's apiclient.ts / stream.ts.
/// </summary>
public sealed class XboxStreamingClient
{
    private readonly HttpClient _http;
    private readonly string _host;
    private readonly string _gsToken;
    public string Locale { get; init; } = "en-GB";
    /// <summary>
    /// The display we report to the service, which picks the stream's resolution tier from it (a console
    /// can still send less). Android is offered 720p; Windows gets 1080p and up.
    /// </summary>
    public StreamResolution Resolution { get; init; } = StreamResolution.Max;
    /// <summary>XSTREAM_DEBUG=1 prints every streaming API call and its response to stderr.</summary>
    private static readonly bool Debug = Environment.GetEnvironmentVariable("XSTREAM_DEBUG") is "1" or "true";

    public XboxStreamingClient(HttpClient http, string streamHost, string gsToken)
    {
        _http = http;
        _host = streamHost.TrimEnd('/');
        _gsToken = gsToken;
    }

    public async Task<IReadOnlyList<XboxConsole>> GetConsolesAsync(CancellationToken ct = default)
    {
        var j = await SendAsync(HttpMethod.Get, "/v6/servers/home", null, ct);
        return (j["results"]?.AsArray() ?? []).Where(r => r is not null).Select(r => new XboxConsole(
            r!["deviceName"]?.GetValue<string>() ?? "Xbox", r["serverId"]!.GetValue<string>(),
            r["powerState"]?.GetValue<string>() ?? "", r["consoleType"]?.GetValue<string>() ?? "",
            r["outOfHomeWarning"]?.GetValue<bool>() ?? false, r["wirelessWarning"]?.GetValue<bool>() ?? false)).ToList();
    }

    /// <summary>Start a session on one of your consoles (remote play).</summary>
    public Task<StreamSession> StartHomeStreamAsync(string serverId, CancellationToken ct = default) =>
        StartStreamAsync("home", serverId: serverId, titleId: "", ct);

    /// <summary>Start an Xbox Cloud Gaming session for a game (client must use a cloud token).</summary>
    public Task<StreamSession> StartCloudStreamAsync(string titleId, CancellationToken ct = default) =>
        StartStreamAsync("cloud", serverId: "", titleId: titleId, ct);

    private async Task<StreamSession> StartStreamAsync(string kind, string serverId, string titleId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["clientSessionId"] = "",
            ["titleId"] = titleId,
            ["systemUpdateGroup"] = "",
            ["settings"] = new JsonObject
            {
                ["nanoVersion"] = "V3;WebrtcTransport.dll",
                ["enableOptionalDataCollection"] = false,
                ["enableTextToSpeech"] = false,
                ["highContrast"] = 0,
                ["locale"] = Locale,
                ["useIceConnection"] = false,
                ["timezoneOffsetMinutes"] = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes,
                ["sdkType"] = "web",
                ["osName"] = "windows",
            },
            ["serverId"] = serverId,
            ["fallbackRegionNames"] = new JsonArray(),
        };
        var j = await SendAsync(HttpMethod.Post, $"/v5/sessions/{kind}/play", body, ct);
        return new StreamSession(this, j["sessionId"]?.GetValue<string>() ?? "", "/" + j["sessionPath"]!.GetValue<string>().TrimStart('/'), titleId);
    }

    /// <summary>Cloud games this account can see (all, or recently played with <paramref name="recent"/>).</summary>
    public async Task<IReadOnlyList<CloudTitle>> GetCloudTitlesAsync(bool recent = false, CancellationToken ct = default)
    {
        var j = await SendAsync(HttpMethod.Get, recent ? "/v2/titles/mru?mr=25" : "/v2/titles", null, ct);
        return ParseCloudTitles(j);
    }

    public static IReadOnlyList<CloudTitle> ParseCloudTitles(JsonNode j) =>
        (j["results"]?.AsArray() ?? []).Where(r => r?["titleId"] is not null).Select(r =>
        {
            var d = r!["details"];
            bool free = d?["isFreeInStore"]?.GetValue<bool>() ?? false;
            bool entitled = (d?["hasEntitlement"]?.GetValue<bool>() ?? false) || free;
            return new CloudTitle(r["titleId"]!.GetValue<string>(), d?["productId"]?.GetValue<string>() ?? "", entitled, free);
        }).ToList();

    /// <summary>Estimated queue time for a cloud game, in seconds (null if unknown).</summary>
    public async Task<int?> GetCloudWaitSecondsAsync(string titleId, CancellationToken ct = default)
    {
        try
        {
            var j = await SendAsync(HttpMethod.Get, "/v1/waittime/" + titleId, null, ct);
            return j["estimatedTotalWaitTimeInSeconds"]?.GetValue<int>();
        }
        catch (XboxApiException) { return null; }
    }

    internal async Task<(HttpStatusCode status, JsonNode body)> SendRawAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, _host + path);
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("X-Gssv-Client", "XboxComBrowser");
        req.Headers.Add("X-MS-Device-Info", DeviceInfo());
        req.Headers.Add("Authorization", "Bearer " + _gsToken);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        else if (method == HttpMethod.Post) req.Content = new StringContent("", Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (Debug)
            Console.Error.WriteLine($"[api] {method} {path} -> {(int)res.StatusCode} {(text.Length > 600 ? text[..600] + "…" : text)}");
        if (!res.IsSuccessStatusCode)
            throw new XboxApiException($"{method} {path}: HTTP {(int)res.StatusCode} {text}", (int)res.StatusCode);
        JsonNode node;
        try { node = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject(); }
        catch (JsonException) { node = new JsonObject { ["raw"] = text }; }
        return (res.StatusCode, node);
    }

    internal async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
        => (await SendRawAsync(method, path, body, ct)).body;

    private string DeviceInfo() => new JsonObject
    {
        ["appInfo"] = new JsonObject
        {
            ["env"] = new JsonObject
            {
                ["clientAppId"] = "www.xbox.com", ["clientAppType"] = "browser", ["clientAppVersion"] = "21.1.98",
                ["clientSdkVersion"] = "8.5.3", ["httpEnvironment"] = "prod", ["sdkInstallId"] = "",
            },
        },
        ["dev"] = new JsonObject
        {
            ["hw"] = new JsonObject { ["make"] = "Microsoft", ["model"] = "unknown", ["sdktype"] = "web" },
            ["os"] = new JsonObject { ["name"] = Resolution == StreamResolution.R720 ? "android" : "windows", ["ver"] = "22631.2715", ["platform"] = "desktop" },
            ["displayInfo"] = new JsonObject
            {
                ["dimensions"] = Resolution switch
                {
                    StreamResolution.Max => new JsonObject { ["widthInPixels"] = 3840, ["heightInPixels"] = 2160 },
                    StreamResolution.R720 => new JsonObject { ["widthInPixels"] = 1280, ["heightInPixels"] = 720 },
                    _ => new JsonObject { ["widthInPixels"] = 1920, ["heightInPixels"] = 1080 },
                },
                ["pixelDensity"] = new JsonObject { ["dpiX"] = 2, ["dpiY"] = 2 },
            },
            ["browser"] = new JsonObject { ["browserName"] = "chrome", ["browserVersion"] = "131.0" },
        },
    }.ToJsonString();
}

/// <summary>One streaming session to a console: state machine, SDP/ICE exchange, keepalive.</summary>
public sealed class StreamSession
{
    private readonly XboxStreamingClient _api;
    public string SessionId { get; }
    public string Path { get; }
    public string State { get; private set; } = "New";

    /// <summary>The cloud game (empty for a console session).</summary>
    public string TitleId { get; }
    public bool IsCloud => TitleId.Length > 0;

    internal StreamSession(XboxStreamingClient api, string id, string path, string titleId = "")
    { _api = api; SessionId = id; Path = path; TitleId = titleId; }

    /// <summary>
    /// Polls until Provisioned. Calls <paramref name="onReadyToConnect"/> (send the transfer token) when asked.
    /// </summary>
    /// <param name="onQueue">Cloud only: called while waiting for a server, with the estimated wait in seconds (if known).</param>
    public async Task WaitUntilProvisionedAsync(Func<Task<string>> getTransferToken, TimeSpan timeout, CancellationToken ct = default,
        Action<int?>? onQueue = null)
    {
        var until = DateTime.UtcNow + timeout;
        bool sentAuth = false;
        DateTime nextWaitCheck = DateTime.MinValue;
        while (DateTime.UtcNow < until)
        {
            var j = await _api.SendAsync(HttpMethod.Get, Path + "/state", null, ct);
            State = j["state"]?.GetValue<string>() ?? "Unknown";
            switch (State)
            {
                case "Provisioned": return;
                case "WaitingForResources":
                    // In the cloud queue. The queue can be long, so the timeout keeps moving while we're in it.
                    until = DateTime.UtcNow + timeout;
                    if (IsCloud && DateTime.UtcNow >= nextWaitCheck)
                    {
                        nextWaitCheck = DateTime.UtcNow.AddSeconds(15);
                        onQueue?.Invoke(await _api.GetCloudWaitSecondsAsync(TitleId, ct));
                    }
                    break;
                case "ReadyToConnect" when !sentAuth:
                    sentAuth = true;
                    await _api.SendAsync(HttpMethod.Post, Path + "/connect", new JsonObject { ["userToken"] = await getTransferToken() }, ct);
                    break;
                case "Failed":
                    throw new XboxApiException("Session failed: " + j.ToJsonString(), 0);
            }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"Session stuck in state {State}");
    }

    /// <summary>Send our SDP offer, return the console's SDP answer.</summary>
    public async Task<string> ExchangeSdpAsync(string offerSdp, CancellationToken ct = default)
    {
        static JsonObject Ver(int min, int max) => new() { ["minVersion"] = min, ["maxVersion"] = max };
        var body = new JsonObject
        {
            ["messageType"] = "offer",
            ["sdp"] = offerSdp,
            ["requestId"] = "1",
            ["configuration"] = new JsonObject
            {
                ["chatConfiguration"] = new JsonObject
                {
                    ["bytesPerSample"] = 2, ["expectedClipDurationMs"] = 20,
                    ["format"] = new JsonObject { ["codec"] = "opus", ["container"] = "webm" },
                    ["numChannels"] = 1, ["sampleFrequencyHz"] = 24000,
                },
                ["chat"] = Ver(1, 1), ["control"] = Ver(1, 3), ["input"] = Ver(1, 9), ["message"] = Ver(1, 1),
                ["reliableinput"] = Ver(9, 9), ["unreliableinput"] = Ver(9, 9),
            },
        };
        await _api.SendAsync(HttpMethod.Post, Path + "/sdp", body, ct);
        var answer = await PollAsync(Path + "/sdp", 500, ct);
        // Response: { exchangeResponse: "<json string with sdp>", ... }
        var inner = JsonNode.Parse(answer["exchangeResponse"]!.GetValue<string>())!;
        return inner["sdp"]?.GetValue<string>()
               ?? throw new XboxApiException("SDP rejected: " + inner.ToJsonString(), 0);
    }

    public async Task<IReadOnlyList<IceCandidate>> ExchangeIceAsync(IEnumerable<IceCandidate> local, CancellationToken ct = default)
    {
        // Same shape as the xbox.com web client: { messageType: "iceCandidate", candidate: [ {candidate, sdpMid, sdpMLineIndex} ] }.
        var arr = new JsonArray(local.Select(c => (JsonNode)new JsonObject
        {
            ["candidate"] = NormalizeCandidate(c.Candidate),
            ["sdpMid"] = c.SdpMid ?? "0",
            ["sdpMLineIndex"] = c.SdpMLineIndex,
        }).ToArray());
        await _api.SendAsync(HttpMethod.Post, Path + "/ice", new JsonObject { ["messageType"] = "iceCandidate", ["candidate"] = arr }, ct);
        var res = await PollAsync(Path + "/ice", 1000, ct);
        var inner = JsonNode.Parse(res["exchangeResponse"]!.GetValue<string>())!;
        return inner.AsArray().Where(n => n?["candidate"] is not null).Select(n => new IceCandidate(
                n!["candidate"]!.GetValue<string>(), n["sdpMid"]?.GetValue<string>(),
                (ushort)(n["sdpMLineIndex"]?.GetValue<int>() ?? 0)))
            .Where(c => !c.Candidate.Contains("end-of-candidates")).ToList();
    }

    private static string NormalizeCandidate(string c)
    {
        c = c.Trim();
        if (c.StartsWith("a=")) c = c[2..];
        return c.StartsWith("candidate:") ? c : "candidate:" + c;
    }

    public Task KeepaliveAsync(CancellationToken ct = default) => _api.SendAsync(HttpMethod.Post, Path + "/keepalive", null, ct);

    public Task StopAsync(CancellationToken ct = default) => _api.SendAsync(HttpMethod.Delete, Path, null, ct);

    private async Task<JsonNode> PollAsync(string path, int intervalMs, CancellationToken ct)
    {
        for (int i = 0; i < 60; i++)
        {
            var (status, body) = await _api.SendRawAsync(HttpMethod.Get, path, null, ct);
            // The service reports a failed exchange in-band; it never turns into a response, so stop polling.
            if (body["errorDetails"] is JsonObject err && body["exchangeResponse"] is null)
                throw new XboxApiException($"Console rejected {path.Split('/')[^1]} exchange: {err["code"]} {err["message"]}", 0);
            if (status != HttpStatusCode.NoContent && body["exchangeResponse"] is not null) return body;
            await Task.Delay(intervalMs, ct);
        }
        throw new TimeoutException("No response from console for " + path);
    }
}
