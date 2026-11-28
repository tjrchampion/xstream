using System.Runtime.InteropServices;

namespace XStream.Player;

/// <summary>The system clipboard through SDL (the stream window has SDL's video side running, which this needs).</summary>
public static class SdlClipboard
{
    [DllImport("SDL2")] private static extern int SDL_SetClipboardText([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport("SDL2")] private static extern IntPtr SDL_GetClipboardText();
    [DllImport("SDL2")] private static extern void SDL_free(IntPtr p);

    /// <summary>Put text on the clipboard. False if it couldn't (no SDL, no window system).</summary>
    public static bool Set(string text)
    {
        try { return SDL_SetClipboardText(text) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>What's on the clipboard ("" if nothing or unavailable).</summary>
    public static string Get()
    {
        try
        {
            var p = SDL_GetClipboardText();
            if (p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUTF8(p) ?? ""; }
            finally { SDL_free(p); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return ""; }
    }
}
