using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace XStream.App.Services;

/// <summary>
/// One Vexel at a time. The first copy listens on a local pipe; a second launch (a Steam / Armoury Crate shortcut
/// with --stream, or just opening Vexel again) hands its command line to the first copy and exits, and the first
/// copy comes to the front and starts that stream. The pipe is per user and per data folder (XSTREAM_HOME), so a
/// test copy with its own folder runs separately.
/// </summary>
public static class SingleInstance
{
    private static readonly string Name = "vexel-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Environment.UserName + "|" + XStream.Core.Config.AppPaths.Root)))[..16].ToLowerInvariant();

    private static CancellationTokenSource? _listening;

    /// <summary>If Vexel is already running, give it these arguments and return true (this copy should exit).</summary>
    public static bool TryForward(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            pipe.Connect(300);
            using var w = new StreamWriter(pipe, new UTF8Encoding(false));
            w.WriteLine(string.Join('\u001f', args));
            w.Flush();
            return true;
        }
        catch { return false; } // nobody listening: we're the first copy
    }

    /// <summary>Start accepting hand-overs. <paramref name="received"/> runs on a background thread.</summary>
    public static void Listen(Action<string[]> received)
    {
        var cts = _listening = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(cts.Token);
                    using var r = new StreamReader(pipe, Encoding.UTF8);
                    var line = await r.ReadLineAsync(cts.Token) ?? "";
                    received(line.Length == 0 ? [] : line.Split('\u001f'));
                }
                catch (OperationCanceledException) { return; }
                catch { await Task.Delay(500); } // another Vexel holds the name, or a broken hand-over: keep going
            }
        });
    }

    /// <summary>Stop listening (before an update starts the new version, so it doesn't hand itself to this one).</summary>
    public static void Stop()
    {
        try { _listening?.Cancel(); } catch { }
        // Unblock a pending accept so the pipe is released at once.
        try { using var p = new NamedPipeClientStream(".", Name, PipeDirection.Out); p.Connect(50); } catch { }
    }
}
