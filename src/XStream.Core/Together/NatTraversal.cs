using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace XStream.Core.Together;

/// <summary>
/// Finding addresses a friend outside the home network can reach us at, with no server of our own: the local
/// network's addresses (a friend on the same Wi-Fi), the public address the router gives us (asked of a public
/// STUN server), and a port opened on the router where it allows that (UPnP or NAT-PMP).
/// </summary>
public static class NatTraversal
{
    /// <summary>Same public STUN servers the console connection uses.</summary>
    public static readonly (string Host, int Port)[] StunServers = [("stun.l.google.com", 19302), ("stun.cloudflare.com", 3478)];

    /// <summary>This device's IPv4 addresses on networks that are up (Wi-Fi, Ethernet), best first.</summary>
    public static List<IPAddress> LanAddresses()
    {
        var list = new List<(IPAddress addr, int rank)>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var ip = n.GetIPProperties();
                bool gateway = ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                string name = (n.Name + " " + n.Description).ToLowerInvariant();
                bool virt = name.Contains("vmnet") || name.Contains("vbox") || name.Contains("docker") || name.Contains("utun") || name.Contains("bridge") || name.Contains("hyper-v");
                foreach (var u in ip.UnicastAddresses)
                {
                    var a = u.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                    if (a.GetAddressBytes() is [169, 254, ..]) continue; // link-local: no DHCP, not useful
                    list.Add((a, (gateway ? 0 : 2) + (virt ? 4 : 0)));
                }
            }
        }
        catch { /* no interface list: no LAN candidates */ }
        return list.OrderBy(x => x.rank).Select(x => x.addr).Distinct().ToList();
    }

    /// <summary>The default gateway (the home router), if there is one.</summary>
    public static IPAddress? Gateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch { return null; }
    }

    /// <summary>The local address used to reach <paramref name="remote"/> (picks the right interface).</summary>
    public static IPAddress? LocalAddressFor(IPAddress remote)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(remote, 9)); // UDP connect sends nothing
            return (s.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch { return null; }
    }

    public static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] < 128) || b[0] == 127;
    }

    // ---- STUN (RFC 8489): "what address do you see me at?" ------------------------------------------

    private const uint StunCookie = 0x2112A442;

    /// <summary>A Binding Request with no attributes (public STUN servers need none).</summary>
    public static byte[] StunRequest(out byte[] transactionId)
    {
        transactionId = RandomNumberGenerator.GetBytes(12);
        var p = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(p, 0x0001);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), StunCookie);
        transactionId.CopyTo(p, 8);
        return p;
    }

    /// <summary>Looks like a STUN message (first two bits 0, magic cookie): not one of ours.</summary>
    public static bool IsStun(ReadOnlySpan<byte> p) => p.Length >= 20 && (p[0] & 0xC0) == 0 && BinaryPrimitives.ReadUInt32BigEndian(p[4..]) == StunCookie;

    /// <summary>The mapped address from a Binding Success Response (XOR-MAPPED-ADDRESS, or the old MAPPED-ADDRESS).</summary>
    public static bool TryParseStunResponse(ReadOnlySpan<byte> p, out ReadOnlySpan<byte> transactionId, out IPEndPoint? mapped)
    {
        transactionId = default; mapped = null;
        if (!IsStun(p) || BinaryPrimitives.ReadUInt16BigEndian(p) != 0x0101) return false;
        transactionId = p.Slice(8, 12);
        int len = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(p[2..]), p.Length - 20);
        var attrs = p.Slice(20, len);
        IPEndPoint? plain = null;
        while (attrs.Length >= 4)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(attrs), alen = BinaryPrimitives.ReadUInt16BigEndian(attrs[2..]);
            if (4 + alen > attrs.Length) break;
            var v = attrs.Slice(4, alen);
            if ((type == 0x0020 || type == 0x0001) && v.Length >= 8 && v[1] == 0x01)
            {
                int port = BinaryPrimitives.ReadUInt16BigEndian(v[2..]);
                var addr = v.Slice(4, 4).ToArray();
                if (type == 0x0020)
                {
                    port ^= (int)(StunCookie >> 16);
                    for (int i = 0; i < 4; i++) addr[i] ^= (byte)(StunCookie >> (24 - 8 * i));
                    mapped = new IPEndPoint(new IPAddress(addr), port);
                    return true;
                }
                plain = new IPEndPoint(new IPAddress(addr), port);
            }
            attrs = attrs[(4 + ((alen + 3) & ~3))..];
        }
        mapped = plain;
        return plain is not null;
    }

    // ---- UPnP Internet Gateway Device: ask the router to forward a port --------------------------------

    /// <summary>A port the router forwards to us; <see cref="RemoveAsync"/> when done.</summary>
    public sealed record PortMapping(string How, IPEndPoint External, Func<Task> RemoveAsync);

    /// <summary>
    /// Find the router by SSDP and add a UDP port mapping (external port = <paramref name="port"/>, falling back to the
    /// router's choice). Null if there's no UPnP router, it refused, or it took longer than <paramref name="timeout"/>.
    /// </summary>
    public static async Task<PortMapping?> UpnpMapAsync(int port, TimeSpan timeout, Action<string>? log = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var locations = await SsdpSearchAsync(TimeSpan.FromMilliseconds(Math.Min(1500, timeout.TotalMilliseconds / 2)), cts.Token);
            if (locations.Count == 0) { log?.Invoke("UPnP: no router answered"); return null; }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            foreach (var loc in locations)
            {
                var (control, service) = await FindWanServiceAsync(http, loc, cts.Token);
                if (control is null || service is null) continue;
                var local = LocalAddressFor(IPAddress.Parse(loc.Host)) ?? LanAddresses().FirstOrDefault();
                if (local is null) continue;
                string desc = "Vexel Play together";
                int[] tryPorts = [port, port + 1, port + 2];
                foreach (int ext in tryPorts)
                {
                    string args = $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{ext}</NewExternalPort><NewProtocol>UDP</NewProtocol>" +
                                  $"<NewInternalPort>{port}</NewInternalPort><NewInternalClient>{local}</NewInternalClient><NewEnabled>1</NewEnabled>" +
                                  $"<NewPortMappingDescription>{desc}</NewPortMappingDescription><NewLeaseDuration>{{0}}</NewLeaseDuration>";
                    var (ok, body) = await SoapAsync(http, control, service, "AddPortMapping", string.Format(args, 7200), cts.Token);
                    // Some routers only take permanent mappings (error 725): those are removed on stop as well.
                    if (!ok && body.Contains("725")) (ok, body) = await SoapAsync(http, control, service, "AddPortMapping", string.Format(args, 0), cts.Token);
                    if (!ok) { log?.Invoke($"UPnP: router refused port {ext}: {Brief(body)}"); continue; }
                    var (ipOk, ipBody) = await SoapAsync(http, control, service, "GetExternalIPAddress", "", cts.Token);
                    string? ipText = ipOk ? Tag(ipBody, "NewExternalIPAddress") : null;
                    if (ipText is null || !IPAddress.TryParse(ipText, out var extIp))
                    {
                        log?.Invoke("UPnP: mapped, but the router didn't say its public address");
                        await SoapAsync(http, control, service, "DeletePortMapping", Delete(ext), CancellationToken.None);
                        return null;
                    }
                    int mappedExt = ext;
                    log?.Invoke($"UPnP: router forwards {extIp}:{ext} to {local}:{port}{(IsPrivate(extIp) ? " (the router's own address is private: another router is in front of it)" : "")}");
                    return new PortMapping("UPnP", new IPEndPoint(extIp, mappedExt), async () =>
                    {
                        using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                        try { await SoapAsync(h, control, service, "DeletePortMapping", Delete(mappedExt), CancellationToken.None); } catch { }
                    });
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or SocketException or System.Xml.XmlException)
        {
            log?.Invoke("UPnP: " + (ex is OperationCanceledException ? "router took too long" : ex.Message));
            return null;
        }

        static string Delete(int ext) => $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{ext}</NewExternalPort><NewProtocol>UDP</NewProtocol>";
        static string Brief(string body) => Tag(body, "errorDescription") is { } d ? d + (Tag(body, "errorCode") is { } c ? $" ({c})" : "") : "no reason given";
    }

    private static async Task<List<Uri>> SsdpSearchAsync(TimeSpan wait, CancellationToken ct)
    {
        var found = new List<Uri>();
        using var s = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        foreach (var st in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2" })
        {
            var msg = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: {st}\r\n\r\n");
            await s.SendAsync(msg, msg.Length, target);
        }
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(until - DateTime.UtcNow);
            UdpReceiveResult r;
            try { r = await s.ReceiveAsync(step.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
            foreach (var line in Encoding.ASCII.GetString(r.Buffer).Split("\r\n"))
                if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line[9..].Trim(), UriKind.Absolute, out var u) && !found.Contains(u))
                    found.Add(u);
            if (found.Count > 0 && DateTime.UtcNow > until - wait / 2) break; // got one; don't wait the whole time
        }
        return found;
    }

    private static async Task<(Uri? control, string? service)> FindWanServiceAsync(HttpClient http, Uri location, CancellationToken ct)
    {
        var xml = XDocument.Parse(await http.GetStringAsync(location, ct));
        XNamespace ns = xml.Root?.Name.Namespace ?? XNamespace.None;
        var baseUri = xml.Root?.Element(ns + "URLBase")?.Value is { Length: > 0 } b && Uri.TryCreate(b, UriKind.Absolute, out var bu) ? bu : location;
        foreach (var svc in xml.Descendants(ns + "service"))
        {
            string type = svc.Element(ns + "serviceType")?.Value ?? "";
            if (!type.Contains("WANIPConnection") && !type.Contains("WANPPPConnection")) continue;
            if (svc.Element(ns + "controlURL")?.Value is not { Length: > 0 } control) continue;
            return (new Uri(baseUri, control), type);
        }
        return (null, null);
    }

    private static async Task<(bool ok, string body)> SoapAsync(HttpClient http, Uri control, string service, string action, string args, CancellationToken ct)
    {
        string envelope = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                          $"<s:Body><u:{action} xmlns:u=\"{service}\">{args}</u:{action}></s:Body></s:Envelope>";
        using var req = new HttpRequestMessage(HttpMethod.Post, control) { Content = new StringContent(envelope, Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service}#{action}\"");
        using var resp = await http.SendAsync(req, ct);
        return (resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static string? Tag(string xml, string name)
    {
        int a = xml.IndexOf("<" + name, StringComparison.Ordinal);
        if (a < 0) return null;
        a = xml.IndexOf('>', a) + 1;
        int b = xml.IndexOf("</" + name, a, StringComparison.Ordinal);
        return b > a ? xml[a..b].Trim() : null;
    }

    // ---- NAT-PMP (RFC 6886): the same, for routers that speak it (Apple AirPort, many others) -----------

    public static async Task<PortMapping?> NatPmpMapAsync(int port, TimeSpan timeout, Action<string>? log = null, CancellationToken ct = default)
    {
        var gw = Gateway();
        if (gw is null) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var s = new UdpClient(AddressFamily.InterNetwork);
            s.Connect(gw, 5351);
            var ext = await PmpAsync(s, [0, 0], 12, cts.Token);
            if (ext is null || ext[1] != 128 || BinaryPrimitives.ReadUInt16BigEndian(ext.AsSpan(2)) != 0) { log?.Invoke("NAT-PMP: no answer from the router"); return null; }
            var extIp = new IPAddress(ext.AsSpan(8, 4));
            var map = new byte[12];
            map[1] = 1; // map UDP
            BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(4), (ushort)port);
            BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(6), (ushort)port);
            BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(8), 7200);
            var r = await PmpAsync(s, map, 16, cts.Token);
            if (r is null || r[1] != 129 || BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2)) != 0) { log?.Invoke("NAT-PMP: the router refused the mapping"); return null; }
            int mapped = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(10));
            log?.Invoke($"NAT-PMP: router forwards {extIp}:{mapped} to port {port}");
            return new PortMapping("NAT-PMP", new IPEndPoint(extIp, mapped), async () =>
            {
                try
                {
                    using var u = new UdpClient(AddressFamily.InterNetwork);
                    u.Connect(gw, 5351);
                    var del = new byte[12];
                    del[1] = 1;
                    BinaryPrimitives.WriteUInt16BigEndian(del.AsSpan(4), (ushort)port); // external 0, lifetime 0: remove
                    await u.SendAsync(del, del.Length);
                }
                catch { }
            });
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            log?.Invoke("NAT-PMP: " + (ex is OperationCanceledException ? "no answer from the router" : ex.Message));
            return null;
        }
    }

    private static async Task<byte[]?> PmpAsync(UdpClient s, byte[] req, int minLen, CancellationToken ct)
    {
        // RFC 6886: start at 250 ms and double; three tries is plenty on a LAN.
        for (int wait = 250; wait <= 1000 && !ct.IsCancellationRequested; wait *= 2)
        {
            await s.SendAsync(req, req.Length);
            using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
            step.CancelAfter(wait);
            try
            {
                var r = await s.ReceiveAsync(step.Token);
                if (r.Buffer.Length >= minLen) return r.Buffer;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return null;
    }
}
