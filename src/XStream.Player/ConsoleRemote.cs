using XStream.Core.Xbox;

namespace XStream.Player;

/// <summary>
/// Remote control of the console being streamed (typing, power), through <see cref="XboxConsoleControl"/>.
/// Finds the console's remote-control id once (same id as the stream's server id; the name as a fallback).
/// Typing is "latest wins": the console's text box is replaced with the whole text, so only the newest
/// text needs to get there and keystrokes typed while a request is out are folded into the next one.
/// </summary>
public sealed class ConsoleRemote(XboxConsoleControl control, string serverId, string consoleName)
{
    private Task<string?>? _id;
    private readonly object _gate = new();
    private string? _pendingText;
    private bool _sending;

    public event Action<string>? Log;
    /// <summary>A command failed in a way the player should hear about (remote features off, etc.).</summary>
    public event Action<string>? Problem;

    public Task<string?> IdAsync() { lock (_gate) return _id ??= Resolve(); }

    private async Task<string?> Resolve()
    {
        try
        {
            var all = await control.GetConsolesAsync();
            var c = all.FirstOrDefault(x => x.Id.Equals(serverId, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(x => x.Name.Equals(consoleName, StringComparison.OrdinalIgnoreCase))
                    ?? (all.Count == 1 ? all[0] : null);
            if (c is null) { Log?.Invoke($"Remote control: {consoleName} not in the console list ({all.Count} consoles)"); return null; }
            if (!c.RemoteManagementEnabled) Log?.Invoke($"Remote control: remote management is off on {c.Name}");
            return c.Id;
        }
        catch (Exception ex) { Log?.Invoke("Remote control: couldn't list consoles: " + ex.Message); lock (_gate) _id = null; return null; }
    }

    /// <summary>Put this text in the console's open text box (call on every change; cheap).</summary>
    public void Type(string text)
    {
        lock (_gate)
        {
            _pendingText = text;
            if (_sending) return;
            _sending = true;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                string? next;
                lock (_gate)
                {
                    next = _pendingText; _pendingText = null;
                    if (next is null) { _sending = false; return; }
                }
                try
                {
                    if (await IdAsync() is not { } id) { Problem?.Invoke("Couldn't find the console to type on"); continue; }
                    await control.TypeAsync(id, next);
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Typing failed: " + ex.Message);
                    Problem?.Invoke(ex is XboxApiException { Status: 400 } ? ex.Message : "Couldn't send the text to the console");
                }
            }
        });
    }

    public async Task<bool> TurnOffAsync()
    {
        try
        {
            if (await IdAsync() is not { } id) return false;
            await control.TurnOffAsync(id);
            Log?.Invoke("Console turned off");
            return true;
        }
        catch (Exception ex) { Log?.Invoke("Turning the console off failed: " + ex.Message); return false; }
    }
}
