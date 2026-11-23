using System.Reflection;
using System.Runtime.InteropServices;

namespace XStream.Core.Native;

/// <summary>
/// Finds the native libraries XStream uses (SDL2 for controllers and the video window, FFmpeg for
/// decoding) in the usual places on macOS (Homebrew), Windows (next to the app) and Linux.
/// Override with XSTREAM_SDL2 (file) and XSTREAM_FFMPEG (folder).
/// </summary>
public static class NativeLibs
{
    private static int _registered;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibs).Assembly, Resolve);
    }

    private static readonly List<string> SearchDirs =
    [
        AppContext.BaseDirectory, "/opt/homebrew/lib", "/usr/local/lib", "/usr/lib/x86_64-linux-gnu", "/usr/lib",
    ];

    /// <summary>
    /// Search <paramref name="dir"/> first (the single-file Windows build unpacks SDL2/FFmpeg there).
    /// Call before the first native call.
    /// </summary>
    public static void AddSearchDirectory(string dir)
    {
        lock (SearchDirs) if (!SearchDirs.Contains(dir)) SearchDirs.Insert(0, dir);
    }

    private static string[] Dirs() { lock (SearchDirs) return SearchDirs.ToArray(); }

    /// <summary>True if the library can be found and loaded (for the launcher's system check).</summary>
    public static bool CanLoad(string name) => name switch
    {
        "SDL2" => Sdl2(typeof(NativeLibs).Assembly, null) != IntPtr.Zero,
        _ => FFmpeg(name) != IntPtr.Zero,
    };

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        return name switch
        {
            "SDL2" => Sdl2(asm, path),
            "avcodec" or "avutil" => FFmpeg(name),
            _ => IntPtr.Zero,
        };
    }

    private static IntPtr Sdl2(Assembly asm, DllImportSearchPath? path)
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("XSTREAM_SDL2") is { Length: > 0 } env) candidates.Add(env);
        candidates.AddRange(new[] { "SDL2", "SDL2.dll", "libSDL2.dylib", "libSDL2-2.0.0.dylib", "libSDL2-2.0.so.0", "libSDL2.so" });
        foreach (var d in Dirs())
            candidates.AddRange(new[] { Path.Combine(d, "libSDL2.dylib"), Path.Combine(d, "SDL2.dll"), Path.Combine(d, "libSDL2-2.0.so.0") });
        foreach (var c in candidates)
            if (NativeLibrary.TryLoad(c, asm, path, out var h) || NativeLibrary.TryLoad(c, out h)) return h;
        return IntPtr.Zero;
    }

    /// <summary>FFmpeg libraries carry their major version in the file name (avcodec-61.dll, libavcodec.so.61 ...).</summary>
    private static IntPtr FFmpeg(string lib)
    {
        var dirs = new List<string>();
        if (Environment.GetEnvironmentVariable("XSTREAM_FFMPEG") is { Length: > 0 } env) dirs.Add(env);
        dirs.AddRange(Dirs());
        dirs.Add("/opt/homebrew/opt/ffmpeg/lib");
        foreach (var d in dirs.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(d); } catch { continue; }
            foreach (var f in files.OrderByDescending(f => f, StringComparer.Ordinal))
            {
                var n = Path.GetFileName(f);
                bool match = n.StartsWith(lib + "-", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                          || n.StartsWith("lib" + lib, StringComparison.Ordinal) && (n.Contains(".so") || n.EndsWith(".dylib"));
                if (match && NativeLibrary.TryLoad(f, out var h)) return h;
            }
        }
        foreach (var c in new[] { lib, "lib" + lib })
            if (NativeLibrary.TryLoad(c, out var h)) return h;
        return IntPtr.Zero;
    }
}
