using System.Text.Json.Nodes;

namespace XStream.Core.Xbox;

/// <summary>
/// The "message" data channel (protocol messageV1): handshake, client config, and transactions
/// from the console. Ported from xbox-xcloud-player channel/message.ts + message/handler.ts.
/// Pure: feed it incoming text, send whatever it returns.
/// </summary>
public sealed class MessageChannelProtocol
{
    public const string ChannelName = "message";
    public const string Protocol = "messageV1";

    public bool HandshakeDone { get; private set; }
    public event Action? HandshakeCompleted;       // start control auth + input channel now
    public event Action? ServerDisconnected;
    public event Action<string, string>? Dialog;   // title, text (auto-accepted)

    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;

    public string Handshake() => new JsonObject
    {
        ["type"] = "Handshake", ["version"] = "messageV1", ["id"] = "be0bfc6d-1e83-4c8a-90ed-fa8601c5a179", ["cv"] = "0",
    }.ToJsonString();

    /// <summary>Handle one incoming message; returns messages to send back.</summary>
    public IReadOnlyList<string> OnMessage(string text)
    {
        var replies = new List<string>();
        var j = JsonNode.Parse(text);
        switch (j?["type"]?.GetValue<string>())
        {
            case "HandshakeAck":
                HandshakeDone = true;
                replies.AddRange(ClientConfig());
                HandshakeCompleted?.Invoke();
                break;
            case "TransactionStart":
            case "Message":
                var target = j["target"]?.GetValue<string>() ?? "";
                var id = j["id"]?.GetValue<string>() ?? "";
                if (target == "/streaming/sessionLifetimeManagement/serverInitiatedDisconnect")
                {
                    replies.Add(Complete(id, ""));
                    ServerDisconnected?.Invoke();
                }
                else if (target == "/streaming/systemUi/messages/ShowMessageDialog")
                {
                    var content = JsonNode.Parse(j["content"]?.GetValue<string>() ?? "{}");
                    Dialog?.Invoke(content?["TitleText"]?.GetValue<string>() ?? "", content?["ContentText"]?.GetValue<string>() ?? "");
                    replies.Add(Complete(id, new JsonObject { ["Result"] = 0 }));
                }
                break;
        }
        return replies;
    }

    public IEnumerable<string> ClientConfig()
    {
        yield return Msg("/streaming/systemUi/configuration", new JsonObject { ["version"] = new JsonArray(0, 2, 0), ["systemUis"] = new JsonArray() });
        yield return Msg("/streaming/properties/clientappinstallidchanged", new JsonObject { ["clientAppInstallId"] = "c97d7ee0-73b2-4239-bf1d-9d805a338429" });
        yield return Msg("/streaming/characteristics/orientationchanged", new JsonObject { ["orientation"] = 0 });
        yield return Msg("/streaming/characteristics/touchinputenabledchanged", new JsonObject { ["touchInputEnabled"] = false });
        yield return Msg("/streaming/characteristics/clientdevicecapabilities", new JsonObject());
        yield return Msg("/streaming/characteristics/dimensionschanged", new JsonObject
        {
            ["horizontal"] = Width, ["vertical"] = Height, ["preferredWidth"] = Width, ["preferredHeight"] = Height,
            ["safeAreaLeft"] = 0, ["safeAreaTop"] = 0, ["safeAreaRight"] = Width, ["safeAreaBottom"] = Height,
            ["supportsCustomResolution"] = true,
        });
    }

    private static string Msg(string target, JsonNode data) => new JsonObject
    {
        ["type"] = "Message", ["content"] = data.ToJsonString(), ["id"] = Guid.NewGuid().ToString(), ["target"] = target, ["cv"] = "",
    }.ToJsonString();

    private static string Complete(string id, JsonNode? data) => new JsonObject
    {
        ["type"] = "TransactionComplete", ["content"] = data is null ? "\"\"" : data.ToJsonString(), ["id"] = id, ["cv"] = "",
    }.ToJsonString();

    private static string Complete(string id, string empty) => Complete(id, (JsonNode?)null);
}
