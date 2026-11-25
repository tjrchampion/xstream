using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XStream.Core.Native;

/// <summary>
/// Shows the Windows touch keyboard (handhelds like the ROG Ally have no physical one). Uses the shell's
/// ITipInvocation, which is what tapping the taskbar keyboard button does; starts TabTip first if it isn't
/// running. No-op elsewhere, never throws.
/// </summary>
public static class WindowsTouchKeyboard
{
    [ComImport, Guid("37c994e7-432b-4834-a2f7-dce1f13b834b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITipInvocation { void Toggle(IntPtr hwnd); }

    private static readonly Guid TipInvocationClsid = new("4ce576fa-83dc-4f88-951c-9d0782b4e376");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>Whether the touch keyboard is on screen now.</summary>
    public static bool Visible => OperatingSystem.IsWindows() && FindWindow("IPTip_Main_Window", null) is var h && h != IntPtr.Zero && IsWindowVisible(h);

    public static void Show()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (Visible) return;
            if (FindWindow("IPTip_Main_Window", null) == IntPtr.Zero)
            {
                var tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), @"microsoft shared\ink\TabTip.exe");
                if (File.Exists(tabTip)) Process.Start(new ProcessStartInfo(tabTip) { UseShellExecute = true })?.Dispose();
                Thread.Sleep(300);
            }
            var type = Type.GetTypeFromCLSID(TipInvocationClsid);
            if (type is not null && Activator.CreateInstance(type) is ITipInvocation tip)
            {
                tip.Toggle(GetDesktopWindow());
                Marshal.ReleaseComObject(tip);
            }
        }
        catch { /* no touch keyboard on this PC */ }
    }
}
