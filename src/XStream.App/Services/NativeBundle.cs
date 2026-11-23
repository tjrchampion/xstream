using System.IO.Compression;
using System.Reflection;
using XStream.Core.Native;

namespace XStream.App.Services;

/// <summary>
/// The single-file Windows build carries SDL2 and FFmpeg inside XStream.exe as one compressed resource
/// ("natives.zip", added by scripts/publish-windows.sh). Windows can only load a DLL from a file, so on first
/// launch they're unpacked once under %LOCALAPPDATA%\XStream\native, with a progress window (SplashWindow).
/// Later launches find a ready copy and skip straight to the app.
///
/// Windows won't delete or rename a folder while a DLL in it is loaded (the launcher keeps SDL2 loaded for
/// controller navigation) or while antivirus is scanning a fresh DLL, so this never deletes or renames a
/// folder that might be in use: each unpack goes into its own new folder, a ".complete" marker written last
/// says it's usable, and any complete folder for this version is accepted, by the launcher and the stream
/// process alike. Old versions and abandoned half-unpacked folders are cleaned up only on a best-effort basis.
/// Dev builds (no resource) use the system libraries as before (Homebrew on Mac).
/// </summary>
public static class NativeBundle
{
    private const string ResourceName = "natives.zip";
    private const string Marker = ".complete";

    private static readonly Assembly Asm = typeof(NativeBundle).Assembly;
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XStream", "native");
    private static readonly string Version = Key();

    public static bool Present => Asm.GetManifestResourceInfo(ResourceName) is not null;

    /// <summary>A complete unpacked copy for this version, if there is one.</summary>
    public static string? ReadyDir
    {
        get
        {
            try
            {
                return Directory.Exists(Root)
                    ? Directory.GetDirectories(Root, Version + "*").FirstOrDefault(d => File.Exists(Path.Combine(d, Marker)))
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public static bool IsReady => !Present || ReadyDir is not null;

    /// <summary>Where the libraries are (or will be) unpacked; for messages.</summary>
    public static string Location => ReadyDir ?? Root;

    /// <summary>Point the native loader at the unpacked copy. Call before anything touches SDL2/FFmpeg.</summary>
    public static void Register()
    {
        if (Present && ReadyDir is { } dir) NativeLibs.AddSearchDirectory(dir);
    }

    /// <summary>Unpack if needed, then register. Progress: 0..1 plus a short description.</summary>
    public static void EnsureExtracted(IProgress<(double fraction, string what)>? progress = null)
    {
        if (IsReady) { Register(); return; }

        // A new folder of our own: nothing else can be using it, and nothing needs renaming afterwards.
        string dir = Path.Combine(Root, $"{Version}-{Environment.ProcessId}-{DateTime.UtcNow:HHmmssfff}");
        Directory.CreateDirectory(dir);
        using (var stream = Asm.GetManifestResourceStream(ResourceName)!)
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
            int lastPct = -1;
            var buffer = new byte[1 << 20];
            foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0))
            {
                string what = Describe(entry.Name);
                using var src = entry.Open();
                using var dst = new FileStream(Path.Combine(dir, entry.Name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dst.Write(buffer, 0, n);
                    done += n;
                    int pct = (int)(done * 100 / total);
                    if (pct != lastPct) { lastPct = pct; progress?.Report((done / (double)total, what)); } // whole percents only
                }
            }
        }
        File.WriteAllText(Path.Combine(dir, Marker), DateTime.UtcNow.ToString("O")); // last: the copy is now usable

        Register();
        CleanUp();
        progress?.Report((1, "Ready"));
    }

    private static string Describe(string file) => file.ToLowerInvariant() switch
    {
        var f when f.StartsWith("avcodec") => "video and audio decoder (FFmpeg)",
        var f when f.StartsWith("avutil") || f.StartsWith("swresample") => "FFmpeg support libraries",
        var f when f.StartsWith("sdl2") => "video window, controller and sound (SDL2)",
        _ => file,
    };

    /// <summary>Folder name prefix: app version plus resource size, so an update never reuses stale DLLs.</summary>
    private static string Key()
    {
        var ver = Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        int plus = ver.IndexOf('+');
        if (plus > 0) ver = ver[..plus];
        long size = 0;
        try { using var s = Asm.GetManifestResourceStream(ResourceName); size = s?.Length ?? 0; } catch { }
        return $"{ver}-{size:x}";
    }

    /// <summary>
    /// Best effort, never fatal: remove other versions' copies, and copies of this version that never finished
    /// (no marker, over 10 minutes old). Anything in use is simply left for next time.
    /// </summary>
    private static void CleanUp()
    {
        string? keep = ReadyDir;
        try
        {
            foreach (var d in Directory.GetDirectories(Root))
            {
                if (string.Equals(d, keep, StringComparison.OrdinalIgnoreCase)) continue;
                bool otherVersion = !Path.GetFileName(d).StartsWith(Version, StringComparison.Ordinal);
                bool abandoned = !File.Exists(Path.Combine(d, Marker)) && Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddMinutes(-10);
                if (otherVersion || abandoned)
                    try { Directory.Delete(d, true); } catch { /* in use (loaded DLL or antivirus scan): leave it */ }
            }
        }
        catch { }
    }
}
