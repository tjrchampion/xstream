using System.Runtime.InteropServices;

namespace XStream.Core.Native;

/// <summary>
/// Windows tweaks for the length of a stream (what Moonlight and Steam Link do too). Each returns an undo;
/// all are no-ops elsewhere and never throw.
/// </summary>
public static class WindowsStreaming
{
    // ---- Wi-Fi: no background scans, media streaming mode ---------------------------------------------
    // Windows scans for networks about once a minute even while connected; each scan takes the radio off
    // channel for a moment and shows up as a lag spike / burst of loss on a Wi-Fi stream.

    private const int WlanIntfOpcodeBackgroundScanEnabled = 2, WlanIntfOpcodeMediaStreamingMode = 3;
    private const int WlanInterfaceStateConnected = 1;

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanSetInterface(IntPtr handle, ref Guid iface, int opcode, uint size, ref int data, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);

    /// <summary>Turn off Wi-Fi background scanning and turn on media streaming mode until disposed.</summary>
    public static (string? Change, IDisposable Undo) QuietWifi()
    {
        if (!OperatingSystem.IsWindows()) return (null, new Undo(null));
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return (null, new Undo(null));
            var changed = new List<Guid>();
            var results = new List<string>();
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) == 0)
            {
                try
                {
                    int count = Marshal.ReadInt32(list);
                    const int itemSize = 16 + 512 + 4; // GUID, WCHAR[256] description, state
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = list + 8 + i * itemSize;
                        var guid = Marshal.PtrToStructure<Guid>(item);
                        if (Marshal.ReadInt32(item + 16 + 512) != WlanInterfaceStateConnected) continue;
                        int off = 0, on = 1;
                        int scan = WlanSetInterface(h, ref guid, WlanIntfOpcodeBackgroundScanEnabled, 4, ref off, IntPtr.Zero);
                        int media = WlanSetInterface(h, ref guid, WlanIntfOpcodeMediaStreamingMode, 4, ref on, IntPtr.Zero);
                        string name = Marshal.PtrToStringUni(item + 16) ?? "adapter";
                        results.Add($"{name}: background scanning {(scan == 0 ? "off" : $"NOT off (error {scan})")}, " +
                                    $"media streaming mode {(media == 0 ? "on" : $"NOT on (error {media})")}");
                        changed.Add(guid);
                    }
                }
                finally { WlanFreeMemory(list); }
            }
            if (changed.Count == 0) { WlanCloseHandle(h, IntPtr.Zero); return (null, new Undo(null)); }
            return ("Wi-Fi: " + string.Join("; ", results), new Undo(() =>
            {
                foreach (var g in changed)
                {
                    var guid = g; int on = 1;
                    WlanSetInterface(h, ref guid, WlanIntfOpcodeBackgroundScanEnabled, 4, ref on, IntPtr.Zero);
                }
                WlanCloseHandle(h, IntPtr.Zero); // also ends media streaming mode
            }));
        }
        catch { return (null, new Undo(null)); } // no Wi-Fi service (wired desktop): nothing to do
    }

    // ---- Battery ---------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>Battery percentage and whether it's running on battery (null: no battery, or not Windows).</summary>
    public static (int Percent, bool OnBattery)? Battery()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!GetSystemPowerStatus(out var s) || s.BatteryLifePercent > 100 || (s.BatteryFlag & 128) != 0) return null; // 128: no battery
            return (s.BatteryLifePercent, s.ACLineStatus == 0);
        }
        catch { return null; }
    }

    // ---- Scheduling: game-class priority for the threads that put frames on screen ----------------------

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] private static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
    [DllImport("avrt.dll")] private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [DllImport("libSystem.dylib")] private static extern int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);
    private const uint QosUserInteractive = 0x21, QosDefault = 0x15;

    /// <summary>
    /// Give the calling thread real-time-ish scheduling for the stream. Windows: register it with the Multimedia
    /// Class Scheduler as a "Games" thread, so background work doesn't delay it (decode and present stay on time when
    /// the Ally is busy). macOS: the user-interactive QoS class, which keeps it on the performance cores (a .NET
    /// thread otherwise often runs on an efficiency core: the clarity filter measured 5.1 ms there vs 1.4 ms).
    /// Undo on the same thread.
    /// </summary>
    public static IDisposable GameThread()
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                return pthread_set_qos_class_self_np(QosUserInteractive, 0) == 0
                    ? new Undo(() => pthread_set_qos_class_self_np(QosDefault, 0)) : new Undo(null);
            }
            catch { return new Undo(null); }
        }
        if (!OperatingSystem.IsWindows()) return new Undo(null);
        try
        {
            uint index = 0;
            var h = AvSetMmThreadCharacteristicsW("Games", ref index);
            return h == IntPtr.Zero ? new Undo(null) : new Undo(() => AvRevertMmThreadCharacteristics(h));
        }
        catch { return new Undo(null); }
    }

    /// <summary>Raise this process above normal priority for the stream.</summary>
    public static void RaiseProcessPriority()
    {
        try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal; }
        catch { }
    }

    private sealed class Undo(Action? undo) : IDisposable
    {
        private Action? _undo = undo;
        public void Dispose() { try { Interlocked.Exchange(ref _undo, null)?.Invoke(); } catch { } }
    }
}
