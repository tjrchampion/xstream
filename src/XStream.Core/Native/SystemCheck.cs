using XStream.Core.Video;

namespace XStream.Core.Native;

public sealed record CheckResult(string Name, bool Ok, string Detail, string? Fix = null);

/// <summary>Is everything the stream needs installed? Shown in the launcher's settings page.</summary>
public static class SystemCheck
{
    public static IReadOnlyList<CheckResult> Run()
    {
        NativeLibs.Ensure();
        string fix = OperatingSystem.IsMacOS() ? "brew install ffmpeg sdl2"
            : "Put the FFmpeg shared DLLs and SDL2.dll next to the app (or set XSTREAM_FFMPEG / XSTREAM_SDL2)";
        var list = new List<CheckResult>();

        bool sdl = NativeLibs.CanLoad("SDL2");
        list.Add(new("SDL2 (video window, controllers)", sdl, sdl ? "Found" : "Not found", sdl ? null : fix));

        bool ff = NativeLibs.CanLoad("avcodec") && NativeLibs.CanLoad("avutil");
        string ver = "Not found";
        if (ff)
        {
            try { ver = H264Decoder.Version; }
            catch (Exception ex) { ff = false; ver = "Found but failed to load: " + ex.Message; }
        }
        list.Add(new("FFmpeg (video decode)", ff, ver, ff ? null : fix));
        return list;
    }
}
