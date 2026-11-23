using System.Runtime.InteropServices;

namespace XStream.Core.Native;

/// <summary>
/// Windows display helpers for a stream: keep the screen on and the device awake (a controller-only
/// session looks "idle" to Windows), and run the panel at the stream's 60 Hz. No-ops elsewhere
/// (SDL already stops the screen saver on macOS).
/// </summary>
public static class WindowsDisplay
{
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x1, EsDisplayRequired = 0x2;
    private const int EnumCurrentSettings = -1;
    private const uint CdsFullscreen = 0x4; // temporary: Windows restores the mode if we exit or crash
    private const int DispChangeSuccessful = 0;
    private const uint DmDisplayFrequency = 0x400000, DmPelsWidth = 0x80000, DmPelsHeight = 0x100000;

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DevMode dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsW(ref DevMode dm, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsW(IntPtr dm, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;

        public static DevMode Create() => new() { DeviceName = "", FormName = "", Size = (ushort)Marshal.SizeOf<DevMode>() };
    }

    /// <summary>Keep the display on and the system awake until disposed. Call from the thread that streams.</summary>
    public static IDisposable KeepAwake()
    {
        if (!OperatingSystem.IsWindows()) return Nothing.Instance;
        try { SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired); }
        catch { return Nothing.Instance; }
        return new Undo(() => { try { SetThreadExecutionState(EsContinuous); } catch { } });
    }

    /// <summary>
    /// Run the main display at <paramref name="hz"/> (same resolution) if it's above that and the panel offers
    /// it, e.g. the ROG Ally's 120 Hz panel showing a 60 fps stream: steadier frame pacing and less power.
    /// Returns what was done (for the log) and an undo; the change is temporary, so Windows also undoes it
    /// if the process dies.
    /// </summary>
    public static (string? Change, IDisposable Undo) MatchRefreshRate(int hz = 60)
    {
        if (!OperatingSystem.IsWindows()) return (null, Nothing.Instance);
        try
        {
            var cur = DevMode.Create();
            if (!EnumDisplaySettingsW(null, EnumCurrentSettings, ref cur)) return (null, Nothing.Instance);
            uint from = cur.DisplayFrequency;
            if (from <= hz + 1) return (null, Nothing.Instance);

            // Only switch to a mode the panel lists at the same resolution.
            bool offered = false;
            var m = DevMode.Create();
            for (int i = 0; EnumDisplaySettingsW(null, i, ref m); i++)
                if (m.PelsWidth == cur.PelsWidth && m.PelsHeight == cur.PelsHeight && m.DisplayFrequency == hz) { offered = true; break; }
            if (!offered) return (null, Nothing.Instance);

            var want = cur;
            want.DisplayFrequency = (uint)hz;
            want.Fields = DmDisplayFrequency | DmPelsWidth | DmPelsHeight;
            if (ChangeDisplaySettingsW(ref want, CdsFullscreen) != DispChangeSuccessful) return (null, Nothing.Instance);
            return ($"Display {from} Hz -> {hz} Hz for the stream",
                new Undo(() => { try { ChangeDisplaySettingsW(IntPtr.Zero, 0); } catch { } }));
        }
        catch { return (null, Nothing.Instance); }
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        private Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
