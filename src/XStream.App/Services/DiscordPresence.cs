using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XStream.App.Services;

/// <summary>
/// "Playing … via Vexel" on Discord, through the Discord app's local IPC (a named pipe on Windows, a
/// Unix socket on macOS/Linux). Does nothing if Discord isn't running or no application id is set.
/// All calls are fire-and-forget and never throw.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    /// <summary>
    /// The Discord application whose name shows as "Playing …". Create one at discord.com/developers
    /// (name it "Vexel"), then put its Application ID here or in settings.json as "DiscordAppId".
    /// </summary>
    public const string DefaultAppId = "1526590132805959780";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Stream? _pipe;
    private string? _appId;

    public async void Show(string appId, string details, string state, string? imageUrl, string? imageText)
    {
        var activity = new JsonObject
        {
            ["details"] = Clip(details),
            ["state"] = Clip(state),
            ["timestamps"] = new JsonObject { ["start"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
            ["assets"] = imageUrl is null ? null : new JsonObject { ["large_image"] = imageUrl, ["large_text"] = Clip(imageText ?? details) },
        };
        await SendActivityAsync(appId, activity);
    }

    public async void Clear()
    {
        if (_pipe is null || _appId is null) return;
        await SendActivityAsync(_appId, null);
    }

    private async Task SendActivityAsync(string appId, JsonObject? activity)
    {
        if (string.IsNullOrWhiteSpace(appId)) return;
        await _gate.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (_pipe is null || _appId != appId) await ConnectAsync(appId);
                    if (_pipe is null) return; // Discord isn't running
                    var cmd = new JsonObject
                    {
                        ["cmd"] = "SET_ACTIVITY",
                        ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
                        ["nonce"] = Guid.NewGuid().ToString(),
                    };
                    await WriteAsync(1, cmd);
                    await ReadAsync(); // response (ignored)
                    return;
                }
                catch { Drop(); } // Discord restarted: reconnect once
            }
        }
        finally { _gate.Release(); }
    }

    private async Task ConnectAsync(string appId)
    {
        Drop();
        for (int i = 0; i < 10 && _pipe is null; i++)
        {
            try { _pipe = await OpenAsync(i); }
            catch { _pipe = null; }
        }
        if (_pipe is null) return;
        _appId = appId;
        await WriteAsync(0, new JsonObject { ["v"] = 1, ["client_id"] = appId });
        await ReadAsync(); // READY
    }

    private static async Task<Stream?> OpenAsync(int i)
    {
        string name = $"discord-ipc-{i}";
        if (OperatingSystem.IsWindows())
        {
            var p = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await p.ConnectAsync(150);
            return p;
        }
        foreach (var dir in new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" }.Select(Environment.GetEnvironmentVariable).Append("/tmp"))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { await s.ConnectAsync(new UnixDomainSocketEndPoint(path)); return new NetworkStream(s, ownsSocket: true); }
            catch { s.Dispose(); }
        }
        return null;
    }

    private async Task WriteAsync(int op, JsonNode payload)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await _pipe!.WriteAsync(frame);
        await _pipe.FlushAsync();
    }

    private async Task<JsonNode?> ReadAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var header = new byte[8];
        await _pipe!.ReadExactlyAsync(header, cts.Token);
        int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (len is < 0 or > 1 << 20) throw new InvalidDataException("bad frame");
        var body = new byte[len];
        await _pipe.ReadExactlyAsync(body, cts.Token);
        try { return JsonNode.Parse(body); } catch (JsonException) { return null; }
    }

    private static string Clip(string s) => s.Length <= 120 ? (s.Length < 2 ? s + "  " : s) : s[..120]; // Discord: 2-128 chars

    private void Drop()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _appId = null;
    }

    public void Dispose() => Drop();
}
