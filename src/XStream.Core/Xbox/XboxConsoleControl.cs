using System.Text;
using System.Text.Json.Nodes;

namespace XStream.Core.Xbox;

/// <summary>A console as the remote-control service sees it.</summary>
public sealed record ControlledConsole(string Id, string Name, string PowerState, bool RemoteManagementEnabled);

/// <summary>
/// Xbox console remote control (xccs.xboxlive.com, the service the Xbox mobile app uses): power, launching a
/// game, and typing text into the console's keyboard. Needs "Remote features" on the console. Works from
/// anywhere, with or without a stream running. Uses the same xboxlive.com sign-in as the library.
/// Wire format from OpenXbox's xbox-webapi-python (provider/smartglass).
/// </summary>
public sealed class XboxConsoleControl(HttpClient http, Func<CancellationToken, Task<string>> authorization)
{
    private const string Host = "https://xccs.xboxlive.com";
    private readonly string _sessionId = Guid.NewGuid().ToString();

    public async Task<IReadOnlyList<ControlledConsole>> GetConsolesAsync(CancellationToken ct = default)
    {
        using var req = await Request(HttpMethod.Get, "/lists/devices?queryCurrentDevice=false&includeStorageDevices=false", ct);
        return ParseConsoles(await XboxAuth.ReadJson(await http.SendAsync(req, ct), ct));
    }

    public static IReadOnlyList<ControlledConsole> ParseConsoles(JsonNode j) =>
        (j["result"]?.AsArray() ?? []).Where(d => d is not null).Select(d => new ControlledConsole(
            d!["id"]?.GetValue<string>() ?? "", d["name"]?.GetValue<string>() ?? "",
            d["powerState"]?.GetValue<string>() ?? "Unknown", d["remoteManagementEnabled"]?.GetValue<bool>() ?? false)).ToList();

    /// <summary>
    /// Store product ids (and title ids) of what's installed on the console. Launching something that isn't installed
    /// only opens its Store page (seen live), so "start this game" lists installed games only.
    /// </summary>
    public async Task<IReadOnlyList<(string ProductId, string TitleId, string Name)>> GetInstalledAsync(string consoleId, CancellationToken ct = default)
    {
        using var req = await Request(HttpMethod.Get, "/lists/installedApps?deviceId=" + Uri.EscapeDataString(consoleId), ct);
        return ParseInstalled(await XboxAuth.ReadJson(await http.SendAsync(req, ct), ct));
    }

    public static IReadOnlyList<(string ProductId, string TitleId, string Name)> ParseInstalled(JsonNode j) =>
        (j["result"]?.AsArray() ?? []).Where(a => a is not null)
            .Select(a => (a!["oneStoreProductId"]?.ToString() ?? "", a["titleId"]?.ToString() ?? "", a["name"]?.ToString() ?? ""))
            .ToList();

    public Task TurnOffAsync(string consoleId, CancellationToken ct = default) => SendAsync(consoleId, "Power", "TurnOff", null, ct);
    public Task RestartAsync(string consoleId, CancellationToken ct = default) => SendAsync(consoleId, "Power", "Reboot", null, ct);
    public Task WakeAsync(string consoleId, CancellationToken ct = default) => SendAsync(consoleId, "Power", "WakeUp", null, ct);

    /// <summary>Start a game or app on the console by its Microsoft Store product id (e.g. 9NBLGGH4R315).</summary>
    public Task LaunchAsync(string consoleId, string productId, CancellationToken ct = default) =>
        SendAsync(consoleId, "Shell", "ActivateApplicationWithOneStoreProductId", new JsonObject { ["oneStoreProductId"] = productId }, ct);

    /// <summary>Type text into whatever text box the console has open (replaces its contents).</summary>
    public Task TypeAsync(string consoleId, string text, CancellationToken ct = default) =>
        SendAsync(consoleId, "Shell", "InjectString", new JsonObject { ["replacementString"] = text }, ct);

    /// <summary>Press a console button (Guide, Menu, View, A, B, X, Y, Up, Down, Left, Right, Nexus).</summary>
    public Task PressAsync(string consoleId, string key, CancellationToken ct = default) =>
        SendAsync(consoleId, "Shell", "InjectKey", new JsonObject { ["keyType"] = key }, ct);

    public static string CommandBody(string sessionId, string consoleId, string type, string command, JsonObject? parameters) =>
        new JsonObject
        {
            ["destination"] = "Xbox", ["type"] = type, ["command"] = command, ["sessionId"] = sessionId,
            ["sourceId"] = "com.microsoft.smartglass", ["parameters"] = new JsonArray(parameters ?? new JsonObject()),
            ["linkedXboxId"] = consoleId,
        }.ToJsonString();

    private async Task SendAsync(string consoleId, string type, string command, JsonObject? parameters, CancellationToken ct)
    {
        JsonNode j;
        try { j = await Post(); }
        catch (XboxApiException ex) when (ex.Status == 401) { _auth = null; j = await Post(); } // token expired: once more, fresh

        async Task<JsonNode> Post()
        {
            using var req = await Request(HttpMethod.Post, "/commands", ct);
            req.Content = new StringContent(CommandBody(_sessionId, consoleId, type, command, parameters), Encoding.UTF8, "application/json");
            return await XboxAuth.ReadJson(await http.SendAsync(req, ct), ct);
        }
        if (j["status"]?["errorCode"]?.GetValue<string>() is { } code && code != "OK")
            throw new XboxApiException(code switch
            {
                "RemoteManagementDisabled" => "Remote features are off on this console. Turn them on in Settings › Devices & connections › Remote features.",
                "CurrentConsoleNotFound" or "XboxDataNotFound" => "The console isn't reachable right now.",
                _ => $"The console refused the command ({code}{(j["status"]?["errorMessage"]?.GetValue<string>() is { } m ? ": " + m : "")}).",
            }, 400);
    }

    // The sign-in token, reused for half an hour: getting one takes two round trips, and typing sends a command per burst.
    private (string Value, DateTime At)? _auth;

    private async Task<string> AuthorizationAsync(CancellationToken ct)
    {
        if (_auth is { } a && DateTime.UtcNow - a.At < TimeSpan.FromMinutes(30)) return a.Value;
        var v = await authorization(ct);
        _auth = (v, DateTime.UtcNow);
        return v;
    }

    private async Task<HttpRequestMessage> Request(HttpMethod method, string path, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, Host + path);
        req.Headers.TryAddWithoutValidation("Authorization", await AuthorizationAsync(ct));
        req.Headers.Add("x-xbl-contract-version", "4");
        req.Headers.Add("skillplatform", "RemoteManagement");
        req.Headers.Add("MS-CV", "0.0");
        req.Headers.Add("Accept", "application/json");
        return req;
    }
}
