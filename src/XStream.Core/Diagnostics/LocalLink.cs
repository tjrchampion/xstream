using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace XStream.Core.Diagnostics;

/// <summary>How this device reaches the network: wired or Wi-Fi, and for Wi-Fi the band, signal and link speed.</summary>
public sealed record LocalLink
{
    /// <summary>"Ethernet", "Wi-Fi" or "" (unknown).</summary>
    public string Kind { get; init; } = "";
    /// <summary>2.4, 5 or 6 (GHz); 0 if unknown or wired.</summary>
    public double BandGHz { get; init; }
    public int Channel { get; init; }
    /// <summary>Signal strength in dBm (macOS), 0 if unknown.</summary>
    public int RssiDbm { get; init; }
    /// <summary>Signal quality 0-100% (Windows), -1 if unknown.</summary>
    public int SignalPct { get; init; } = -1;
    /// <summary>Wi-Fi link speed the radio negotiated, Mbps; 0 if unknown.</summary>
    public double LinkMbps { get; init; }
    public bool IsWifi => Kind == "Wi-Fi";

    /// <summary>Signal as a rough 0-100% on both platforms (from dBm on macOS: -50 or better = 100, -90 = 0).</summary>
    public int SignalQuality => SignalPct >= 0 ? SignalPct : RssiDbm < 0 ? Math.Clamp((RssiDbm + 90) * 100 / 40, 0, 100) : -1;

    public string Describe()
    {
        if (!IsWifi) return Kind.Length > 0 ? Kind : "Unknown connection";
        var parts = new List<string> { "Wi-Fi" };
        if (BandGHz > 0) parts.Add($"{BandGHz:0.#} GHz");
        if (SignalQuality >= 0) parts.Add($"signal {SignalQuality}%");
        if (LinkMbps > 0) parts.Add($"{LinkMbps:F0} Mbps link");
        return string.Join(" · ", parts);
    }

    /// <summary>Best effort; never throws (unknown fields stay empty).</summary>
    public static LocalLink Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Windows();
            if (OperatingSystem.IsMacOS()) return Mac();
        }
        catch { }
        return new LocalLink();
    }

    // ---- Windows: the interface with the default gateway; for Wi-Fi, `netsh wlan show interfaces`. ------------
    private static LocalLink Windows()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)));
        if (nic is null) return new LocalLink();
        if (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) return new LocalLink { Kind = "Ethernet" };
        string text = Run("netsh", "wlan show interfaces");
        return ParseNetsh(text) with { Kind = "Wi-Fi" };
    }

    /// <summary>The fields of `netsh wlan show interfaces` (English output; other languages leave them unknown).</summary>
    public static LocalLink ParseNetsh(string text)
    {
        string? Field(string name) => Regex.Match(text, @"^\s*" + name + @"\s*:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
        double band = Field("Band") is { } b && Regex.Match(b, @"([\d.]+)\s*GHz") is { Success: true } bm ? double.Parse(bm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        int channel = int.TryParse(Field("Channel"), out var ch) ? ch : 0;
        if (band == 0 && channel > 0) band = channel <= 14 ? 2.4 : 5;
        int signal = Field("Signal") is { } sg && int.TryParse(sg.TrimEnd('%', ' '), out var s) ? s : -1;
        double rx = double.TryParse(Field(@"Receive rate \(Mbps\)"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0;
        return new LocalLink { Kind = "Wi-Fi", BandGHz = band, Channel = channel, SignalPct = signal, LinkMbps = rx };
    }

    // ---- macOS: the default route's interface; for Wi-Fi, CoreWLAN (no location permission needed for these). --
    private static LocalLink Mac()
    {
        string route = Run("/sbin/route", "-n get default");
        string iface = Regex.Match(route, @"interface:\s*(\S+)") is { Success: true } m ? m.Groups[1].Value : "";
        var wifi = CoreWlan();
        if (wifi is null || iface.Length == 0 || iface != wifi.Value.Name)
            return new LocalLink { Kind = iface.Length == 0 ? "" : iface.StartsWith("utun") ? "VPN" : "Ethernet" };
        var w = wifi.Value;
        return new LocalLink
        {
            Kind = "Wi-Fi", RssiDbm = (int)w.Rssi, LinkMbps = w.Tx, Channel = (int)w.Channel,
            BandGHz = w.Band switch { 1 => 2.4, 2 => 5, 3 => 6, _ => w.Channel is > 0 and <= 14 ? 2.4 : 0 },
        };
    }

    private static unsafe (string Name, long Rssi, double Tx, long Band, long Channel)? CoreWlan()
    {
        if (!NativeLibrary.TryLoad("/System/Library/Frameworks/CoreWLAN.framework/CoreWLAN", out _)) return null;
        var objc = NativeLibrary.Load("/usr/lib/libobjc.A.dylib");
        var send = NativeLibrary.GetExport(objc, "objc_msgSend");
        var getClass = (delegate* unmanaged<byte*, IntPtr>)NativeLibrary.GetExport(objc, "objc_getClass");
        var sel = (delegate* unmanaged<byte*, IntPtr>)NativeLibrary.GetExport(objc, "sel_registerName");
        IntPtr Sel(string s) { var b = System.Text.Encoding.UTF8.GetBytes(s + "\0"); fixed (byte* p = b) return sel(p); }
        IntPtr Cls(string s) { var b = System.Text.Encoding.UTF8.GetBytes(s + "\0"); fixed (byte* p = b) return getClass(p); }
        var id = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)send;
        var lng = (delegate* unmanaged<IntPtr, IntPtr, long>)send;
        var dbl = (delegate* unmanaged<IntPtr, IntPtr, double>)send;
        var cls = Cls("CWWiFiClient");
        if (cls == IntPtr.Zero) return null;
        var client = id(cls, Sel("sharedWiFiClient"));
        var iface = client == IntPtr.Zero ? IntPtr.Zero : id(client, Sel("interface"));
        if (iface == IntPtr.Zero) return null;
        var nameObj = id(iface, Sel("interfaceName"));
        var utf8 = nameObj == IntPtr.Zero ? IntPtr.Zero : id(nameObj, Sel("UTF8String"));
        string name = utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
        long rssi = lng(iface, Sel("rssiValue"));
        double tx = dbl(iface, Sel("transmitRate"));
        var chan = id(iface, Sel("wlanChannel"));
        long band = chan == IntPtr.Zero ? 0 : lng(chan, Sel("channelBand"));
        long number = chan == IntPtr.Zero ? 0 : lng(chan, Sel("channelNumber"));
        if (rssi == 0 && number == 0) return null; // radio off or not joined
        return (name, rssi, tx, band, number);
    }

    private static string Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null) return "";
            var text = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } return ""; }
            return text.Result;
        }
        catch { return ""; }
    }
}
