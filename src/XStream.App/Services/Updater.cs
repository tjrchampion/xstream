using System.Diagnostics;
using System.Reflection;
using XStream.Core.Config;
using XStream.Core.Update;

namespace XStream.App.Services;

/// <summary>
/// Keeps the Windows build up to date: checks the release feed (latest.json) at launch and every 6 hours,
/// downloads a newer XStream.exe in the background, verifies its signature and hash (see UpdateManifest),
/// then offers "Restart to update". Applying swaps the exe in place: the running file is renamed (Windows
/// allows that), the new one takes its name, it's started, and this copy exits. The old file is deleted on
/// the next launch. Never touches a running stream.
///
/// Only the single-file Windows build updates itself; a dev build (dotnet run) just reports what's available.
/// </summary>
public sealed class Updater(SettingsStore settings)
{
    /// <summary>
    /// Where releases are published. Set this (or "UpdateFeedUrl" in settings.json) to the URL of the latest.json
    /// that scripts/publish-windows.sh writes, e.g. https://github.com/OWNER/xstream/releases/latest/download/latest.json
    /// </summary>
    public const string DefaultFeed = "";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static Version Current { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>A verified update waiting to be installed (null: none).</summary>
    public UpdateManifest? Ready { get; private set; }
    private string? _readyFile;
    public string Status { get; private set; } = "";

    /// <summary>Raised (on a background thread) when Ready or Status changes.</summary>
    public event Action? Changed;

    public string Feed => settings.Current.UpdateFeedUrl is { Length: > 0 } u ? u : DefaultFeed;
    public bool Configured => Feed.Length > 0;

    /// <summary>True when this process is the installed single-file exe that can replace itself.</summary>
    public static bool CanSelfUpdate =>
        OperatingSystem.IsWindows() && Environment.ProcessPath is { } p
        && Path.GetFileName(p).Equals("XStream.exe", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location); // single-file publish (not dotnet run)

    private static string UpdatesDir => Directory.CreateDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XStream", "updates")).FullName;

    /// <summary>Delete what a previous update left behind (the replaced exe, old downloads).</summary>
    public static void CleanUp()
    {
        try
        {
            if (Environment.ProcessPath is { } self && File.Exists(self + ".old")) File.Delete(self + ".old");
        }
        catch { /* still locked by a closing process: next time */ }
        try
        {
            foreach (var f in Directory.EnumerateFiles(UpdatesDir, "*.exe"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)) File.Delete(f);
        }
        catch { }
    }

    /// <summary>Check the feed; download and verify a newer version if there is one. Never throws.</summary>
    public async Task CheckAsync(bool userAsked = false)
    {
        if (!Configured) { SetStatus(userAsked ? "Updates aren't set up for this build." : ""); return; }
        if (!userAsked && !settings.Current.AutoUpdate) return;
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            SetStatus("Checking for updates…");
            var manifest = UpdateManifest.Parse(await Http.GetStringAsync(Feed));
            if (!manifest.SignatureValid()) { SetStatus("Ignored an update that wasn't signed by XStream."); return; }
            if (!manifest.IsNewerThan(Current)) { SetStatus($"XStream {Current} is up to date."); return; }
            if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && !url.IsLoopback))
            { SetStatus("Ignored an update with an insecure download link."); return; }

            string file = Path.Combine(UpdatesDir, $"XStream-{manifest.Version}.exe");
            if (!manifest.Matches(file))
            {
                SetStatus($"Downloading XStream {manifest.Version}…");
                string part = file + ".part";
                using (var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    res.EnsureSuccessStatusCode();
                    await using var src = await res.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(part);
                    await src.CopyToAsync(dst);
                }
                File.Move(part, file, overwrite: true);
                if (!manifest.Matches(file)) { File.Delete(file); SetStatus("The update download was damaged; it'll be retried later."); return; }
            }
            _readyFile = file;
            Ready = manifest;
            SetStatus(CanSelfUpdate ? $"XStream {manifest.Version} is ready. Restart to update." : $"XStream {manifest.Version} is available.");
        }
        catch (Exception ex) { SetStatus(userAsked ? "Couldn't check for updates: " + ex.Message : ""); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Install the downloaded update and restart into it. Returns false (with Status saying why) if it can't;
    /// on success the caller should shut down.
    /// </summary>
    public bool ApplyAndRestart()
    {
        if (Ready is null || _readyFile is null || !CanSelfUpdate || Environment.ProcessPath is not { } self) return false;
        string old = self + ".old";
        try
        {
            if (!Ready.Matches(_readyFile)) { SetStatus("The update file changed on disk; downloading it again."); Ready = null; return false; }
            if (File.Exists(old)) File.Delete(old);
            File.Move(self, old);                 // a running exe can be renamed, not overwritten
            try { File.Copy(_readyFile, self); }
            catch { File.Move(old, self); throw; } // put it back: never leave the user without an exe
            Process.Start(new ProcessStartInfo(self) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(self)! });
            return true;
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't install the update ({ex.Message}). If XStream is in Program Files, move it to a folder you own.");
            return false;
        }
    }

    private void SetStatus(string s) { Status = s; Changed?.Invoke(); }
}
