using System.Diagnostics;
using System.Reflection;
using XStream.Core.Config;
using XStream.Core.Update;

namespace XStream.App.Services;

/// <summary>
/// Keeps the Windows build up to date: checks the release feed (latest.json) at launch and every 6 hours,
/// downloads a newer XStream.exe in the background, verifies its signature and hash (see UpdateManifest),
/// then offers "Install and restart" (banner, and in Settings). A downloaded update is also installed the next
/// time Vexel opens (<see cref="TryInstallPending"/>), so quitting and reopening updates too. Installing swaps the
/// exe in place: the running file is renamed (Windows allows that), the new one takes its name, it's started, and
/// this copy exits. The old file is deleted on the next launch. Never touches a running stream.
///
/// The single-file Windows build and the Mac app (Vexel.app, from latest-mac.json) update themselves; a dev build
/// (dotnet run) just reports what's available.
/// </summary>
public sealed class Updater(SettingsStore settings)
{
    /// <summary>
    /// Where releases are published. Set this (or "UpdateFeedUrl" in settings.json) to the URL of the latest.json
    /// that scripts/publish-windows.sh writes, e.g. https://github.com/OWNER/xstream/releases/latest/download/latest.json
    /// </summary>
    public const string DefaultFeed = "https://github.com/tjrchampion/xstream/releases/latest/download/latest.json";
    /// <summary>The Mac app's feed (latest-mac.json, for Vexel-mac.zip; signed with the same key).</summary>
    public const string DefaultMacFeed = "https://github.com/tjrchampion/xstream/releases/latest/download/latest-mac.json";

    /// <summary>The Mac app bundle this process runs from (…/Vexel.app), or null (Windows, dev builds).</summary>
    public static string? MacBundle =>
        OperatingSystem.IsMacOS() && Environment.ProcessPath is { } p && p.Contains(".app/Contents/MacOS/", StringComparison.Ordinal)
            ? p[..(p.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal) + 4)] : null;

    /// <summary>What a release file is called here: Vexel.exe on Windows, Vexel-mac.zip on the Mac.</summary>
    private static string Ext => MacBundle is not null ? ".zip" : ".exe";

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

    public string Feed => settings.Current.UpdateFeedUrl is { Length: > 0 } u ? u : MacBundle is not null ? DefaultMacFeed : DefaultFeed;
    public bool Configured => Feed.Length > 0;

    /// <summary>
    /// True when this process is the single-file Windows exe, which can replace itself. Whatever the file is called:
    /// it used to have to be exactly Vexel.exe, so a second browser download ("Vexel (1).exe") never updated and
    /// never showed an install button.
    /// </summary>
    public static bool CanSelfUpdate =>
        (OperatingSystem.IsWindows() && Environment.ProcessPath is { } p
         && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
         && string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location)) // single-file publish (not dotnet run)
        || MacBundle is not null;                                        // Vexel.app (replaced as a whole bundle)

    /// <summary>A verified update is downloaded and this build can install it.</summary>
    public bool CanInstall => Ready is not null && CanSelfUpdate;

    /// <summary>Where to get the update by hand when this build can't install it itself (e.g. macOS, dev builds).</summary>
    public const string ReleasesPage = "https://github.com/tjrchampion/xstream/releases/latest";

    private static string UpdatesDir => Directory.CreateDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XStream", "updates")).FullName;

    /// <summary>Delete what a previous update left behind (the replaced exe, old downloads).</summary>
    public static void CleanUp()
    {
        try
        {
            if (Environment.ProcessPath is { } self && File.Exists(self + ".old")) File.Delete(self + ".old");
            if (MacBundle is { } app && Directory.Exists(app + ".old")) Directory.Delete(app + ".old", recursive: true);
            if (Directory.Exists(Path.Combine(UpdatesDir, "unpack"))) Directory.Delete(Path.Combine(UpdatesDir, "unpack"), recursive: true);
        }
        catch { /* still locked by a closing process: next time */ }
        try
        {
            foreach (var f in Directory.EnumerateFiles(UpdatesDir))
            {
                // Installed (this version or older) or abandoned: no longer needed.
                bool installed = Path.GetFileNameWithoutExtension(f) is var n && n.StartsWith("Vexel-")
                                 && Version.TryParse(n["Vexel-".Length..], out var v) && v <= Current;
                if (installed || File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)) File.Delete(f);
            }
        }
        catch { }
    }

    /// <summary>
    /// At launch, before any window: if an update was downloaded and verified earlier (its manifest is saved next to
    /// it), install it and start the new version. True = done, this copy should exit. Checks the signature and the
    /// file's hash again first, so nothing that changed on disk since gets run.
    /// </summary>
    public static bool TryInstallPending()
    {
        if (!CanSelfUpdate) return false;
        try
        {
            foreach (var json in Directory.EnumerateFiles(UpdatesDir, "Vexel-*.json").OrderByDescending(f => f))
            {
                var m = UpdateManifest.Parse(File.ReadAllText(json));
                string exe = Path.ChangeExtension(json, Ext);
                if (!UpdateSignature.Valid(m, out _) || !m.IsNewerThan(Current) || !m.Matches(exe)) continue;
                Log($"Installing {m.Version}, downloaded earlier, at launch");
                if (Install(exe, out var error)) return true;
                Log($"Couldn't install {m.Version} at launch: {error}");
                return false;
            }
        }
        catch { /* a bad leftover never stops Vexel opening */ }
        return false;
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
            if (!UpdateSignature.Valid(manifest, out var why))
            {
                Log($"Signature check failed for {manifest.Version}: {why ?? "signature doesn't match"} (sig {manifest.Signature?.Length ?? 0} chars, sha {manifest.Sha256})");
                SetStatus("Ignored an update that wasn't signed by Vexel.");
                return;
            }
            if (!manifest.IsNewerThan(Current)) { SetStatus($"Vexel {Current} is up to date."); return; }
            if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && !url.IsLoopback))
            { SetStatus("Ignored an update with an insecure download link."); return; }

            if (!url.AbsolutePath.EndsWith(Ext, StringComparison.OrdinalIgnoreCase))
            { SetStatus($"Ignored an update that isn't a {(Ext == ".zip" ? "Mac" : "Windows")} build."); return; }
            string file = Path.Combine(UpdatesDir, $"Vexel-{manifest.Version}{Ext}");
            if (!manifest.Matches(file))
            {
                SetStatus($"Downloading Vexel {manifest.Version}…");
                string part = file + ".part";
                using (var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    res.EnsureSuccessStatusCode();
                    await using var src = await res.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(part);
                    var buf = new byte[81920];
                    long got = 0; int lastPct = -1, n;
                    while ((n = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        got += n;
                        int pct = manifest.Size > 0 ? (int)(got * 100 / manifest.Size) : 0;
                        if (pct / 5 != lastPct / 5) { lastPct = pct; SetStatus($"Downloading Vexel {manifest.Version}… {Math.Min(pct, 100)}%"); }
                    }
                }
                File.Move(part, file, overwrite: true);
                if (!manifest.Matches(file)) { File.Delete(file); SetStatus("The update download was damaged; it'll be retried later."); return; }
            }
            // Kept next to the exe so the next launch can install it (TryInstallPending re-verifies both).
            await File.WriteAllTextAsync(Path.ChangeExtension(file, ".json"), System.Text.Json.JsonSerializer.Serialize(manifest));
            _readyFile = file;
            Ready = manifest;
            SetStatus(CanSelfUpdate
                ? $"Vexel {manifest.Version} is ready to install. It also installs next time you open Vexel."
                : $"Vexel {manifest.Version} is available. This build can't update itself: download it from the releases page.");
        }
        catch (Exception ex)
        {
            Log("Check failed: " + ex);
            // A background check stays quiet in the window, but says why in Settings (it used to say nothing at all).
            SetStatus("Couldn't check for updates: " + ex.Message);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Install the downloaded update and restart into it. Returns false (with Status saying why) if it can't;
    /// on success the caller should shut down.
    /// </summary>
    public bool ApplyAndRestart()
    {
        if (Ready is null || _readyFile is null || !CanSelfUpdate) return false;
        if (!Ready.Matches(_readyFile)) { SetStatus("The update file changed on disk; downloading it again."); Ready = null; Changed?.Invoke(); return false; }
        Log($"Installing {Ready.Version} (asked to)");
        if (Install(_readyFile, out var error)) return true;
        SetStatus($"Couldn't install the update ({error}). If Vexel is in Program Files, move it to a folder you own.");
        return false;
    }

    /// <summary>Swap the running exe (or Mac app) for <paramref name="newExe"/> and start it (the caller then exits).</summary>
    private static bool Install(string newExe, out string? error)
    {
        error = null;
        if (MacBundle is { } bundle) return InstallMac(newExe, bundle, out error);
        if (Environment.ProcessPath is not { } self) { error = "unknown exe path"; return false; }
        string old = self + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(self, old);                 // a running exe can be renamed, not overwritten
            try { File.Copy(newExe, self); }
            catch { File.Move(old, self); throw; } // put it back: never leave the user without an exe
            SingleInstance.Stop(); // the new version must not hand itself over to this one
            Process.Start(new ProcessStartInfo(self) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(self)! });
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>
    /// Mac: unpack Vexel-mac.zip (already verified), move the running Vexel.app aside to Vexel.app.old (a running
    /// app can be moved; its open files stay valid), put the new one in its place, and open it. Downloads made by
    /// Vexel itself aren't quarantined, so the new version opens without the "unidentified developer" prompt.
    /// </summary>
    private static bool InstallMac(string zip, string bundle, out string? error)
    {
        error = null;
        string unpack = Path.Combine(UpdatesDir, "unpack");
        string old = bundle + ".old";
        try
        {
            if (Directory.Exists(unpack)) Directory.Delete(unpack, recursive: true);
            Directory.CreateDirectory(unpack);
            using (var ditto = Process.Start(new ProcessStartInfo("/usr/bin/ditto", ["-x", "-k", zip, unpack]) { UseShellExecute = false }))
            {
                ditto!.WaitForExit(60_000);
                if (ditto.ExitCode != 0) { error = "couldn't unpack the update"; return false; }
            }
            string fresh = Path.Combine(unpack, "Vexel.app");
            if (!File.Exists(Path.Combine(fresh, "Contents", "MacOS", "XStream"))) { error = "the update has no Vexel.app inside"; return false; }
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
            Directory.Move(bundle, old);
            try { Directory.Move(fresh, bundle); }
            catch { Directory.Move(old, bundle); throw; } // put it back: never leave the user without the app
            SingleInstance.Stop(); // the new version must not hand itself over to this one
            Process.Start(new ProcessStartInfo("/usr/bin/open", ["-n", bundle]) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) { error = ex.Message + (ex is UnauthorizedAccessException or IOException ? " (is Vexel in a folder you can write to?)" : ""); return false; }
    }

    private void SetStatus(string s)
    {
        if (s.Length > 0 && !s.StartsWith("Downloading", StringComparison.Ordinal) || s.EndsWith(" 100%", StringComparison.Ordinal)) Log(s);
        Status = s;
        Changed?.Invoke();
    }

    /// <summary>logs/updates.log (included in Export diagnostics): what the updater did and why.</summary>
    private static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(AppPaths.Logs, "updates.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Current}] {line}{Environment.NewLine}"); }
        catch { }
    }
}
