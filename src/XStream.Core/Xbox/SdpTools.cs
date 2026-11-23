using System.Text.RegularExpressions;

namespace XStream.Core.Xbox;

/// <summary>SDP edits applied to our offer before it goes to the console.</summary>
public static partial class SdpTools
{
    /// <summary>Sets b=AS (kbps) for a media section. The console treats this as its ceiling for the session.</summary>
    public static string SetBitrate(string sdp, string media, int kbps)
    {
        var lines = Split(sdp);
        int m = lines.FindIndex(l => l.StartsWith("m=" + media));
        if (m < 0) return sdp;
        int i = m + 1;
        while (i < lines.Count && (lines[i].StartsWith("i=") || lines[i].StartsWith("c="))) i++;
        if (i < lines.Count && lines[i].StartsWith("b=")) lines[i] = "b=AS:" + kbps;
        else lines.Insert(i, "b=AS:" + kbps);
        return Join(lines);
    }

    public static string EnableStereo(string sdp) => sdp.Replace("useinbandfec=1", "useinbandfec=1;stereo=1");

    /// <summary>
    /// Advertises feedback on every video payload: goog-remb (we steer bitrate mid-stream),
    /// nack (we ask for lost packets) and nack pli (we ask for keyframes).
    /// </summary>
    public static string AddRemb(string sdp)
    {
        var lines = Split(sdp);
        int m = lines.FindIndex(l => l.StartsWith("m=video"));
        if (m < 0) return sdp;
        int end = lines.FindIndex(m + 1, l => l.StartsWith("m="));
        if (end < 0) end = lines.Count;
        var pts = lines.Skip(m + 1).Take(end - m - 1).Select(l => RtpMap().Match(l)).Where(x => x.Success)
                       .Select(x => x.Groups[1].Value).ToList();
        var section = lines.GetRange(m + 1, end - m - 1);
        int insertAt = end;
        foreach (var pt in pts)
            foreach (var fb in new[] { "goog-remb", "nack", "nack pli" })
                if (!section.Contains($"a=rtcp-fb:{pt} {fb}")) lines.Insert(insertAt++, $"a=rtcp-fb:{pt} {fb}");
        return Join(lines);
    }

    /// <summary>True if the console's answer agreed to REMB feedback.</summary>
    public static bool AnswerSupportsRemb(string answerSdp) => answerSdp.Contains("goog-remb");

    /// <summary>True if the console agreed to resend lost packets.</summary>
    public static bool AnswerSupportsNack(string answerSdp) =>
        answerSdp.Replace("\r\n", "\n").Split('\n').Any(l => l.StartsWith("a=rtcp-fb:") && l.TrimEnd().EndsWith(" nack"));

    /// <summary>
    /// Reorders video payload types: H.264 High (4d) first, then Main/Constrained (42e0/4200), then the rest.
    /// High profile gives noticeably better quality per bit, which matters when we cap bitrate.
    /// </summary>
    public static string PreferH264High(string sdp)
    {
        var lines = Split(sdp);
        int m = lines.FindIndex(l => l.StartsWith("m=video"));
        if (m < 0) return sdp;
        var parts = lines[m].Split(' ');
        if (parts.Length <= 3) return sdp;
        var pts = parts.Skip(3).ToList();
        int Rank(string pt)
        {
            var fmtp = lines.FirstOrDefault(l => l.StartsWith($"a=fmtp:{pt} ")) ?? "";
            var map = lines.FirstOrDefault(l => l.StartsWith($"a=rtpmap:{pt} ")) ?? "";
            if (!map.Contains("H264")) return map.Contains("rtx") ? 5 : 4;
            if (fmtp.Contains("profile-level-id=4d")) return 0;
            if (fmtp.Contains("profile-level-id=42e")) return 1;
            if (fmtp.Contains("profile-level-id=420")) return 2;
            return 3;
        }
        var ordered = pts.Select((pt, idx) => (pt, idx)).OrderBy(x => Rank(x.pt)).ThenBy(x => x.idx).Select(x => x.pt);
        lines[m] = string.Join(' ', parts.Take(3).Concat(ordered));
        return Join(lines);
    }

    /// <summary>Everything we apply to an outgoing offer.</summary>
    public static string PrepareOffer(string sdp, int videoKbps, bool stereo = true)
    {
        sdp = PreferH264High(sdp);
        sdp = AddRemb(sdp);
        if (videoKbps > 0) sdp = SetBitrate(sdp, "video", videoKbps);
        if (stereo) sdp = EnableStereo(sdp);
        return sdp;
    }

    private static List<string> Split(string sdp) => sdp.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
    private static string Join(List<string> lines) => string.Join("\r\n", lines) + "\r\n";

    [GeneratedRegex(@"^a=rtpmap:(\d+) ")]
    private static partial Regex RtpMap();
}

/// <summary>
/// Xbox consoles advertise Teredo IPv6 candidates. The embedded IPv4 address/port (bit-inverted) is often
/// the path that actually works through home NATs, so we add it as an extra candidate.
/// </summary>
public static class Teredo
{
    public static bool IsTeredo(string address) => address.StartsWith("2001:0:") || address.StartsWith("2001:0000:");

    public static (string ipv4, int port) Decode(string ipv6)
    {
        var addr = System.Net.IPAddress.Parse(ipv6).GetAddressBytes(); // 16 bytes, handles :: compression
        int port = ~((addr[10] << 8) | addr[11]) & 0xFFFF;
        string ip = $"{~addr[12] & 0xFF}.{~addr[13] & 0xFF}.{~addr[14] & 0xFF}.{~addr[15] & 0xFF}";
        return (ip, port);
    }

    /// <summary>Expands the console's remote candidates with Teredo-derived IPv4 ones (as xbox-xcloud-player does).</summary>
    public static IEnumerable<IceCandidate> Expand(IEnumerable<IceCandidate> remote)
    {
        foreach (var c in remote)
        {
            if (c.Candidate.Contains("end-of-candidates")) continue;
            var parts = c.Candidate.Split(' ');
            if (parts.Length > 4 && IsTeredo(parts[4]))
            {
                var (ip, port) = Decode(parts[4]);
                yield return c with { Candidate = $"a=candidate:10 1 UDP 1 {ip} 9002 typ host " };
                yield return c with { Candidate = $"a=candidate:11 1 UDP 1 {ip} {port} typ host " };
            }
            yield return c;
        }
    }
}

/// <summary>Messages for the "control" data channel (JSON over SCTP).</summary>
public static class ControlMessages
{
    public const string ChannelName = "control";
    public const string Protocol = "controlV1";

    public static string Authorization() =>
        """{"message":"authorizationRequest","accessKey":"4BDB3609-C1F1-4195-9B37-FEFF45DA8B8E"}""";

    public static string GamepadChanged(int index, bool added) =>
        $$"""{"message":"gamepadChanged","gamepadIndex":{{index}},"wasAdded":{{(added ? "true" : "false")}}}""";

    /// <summary>Ask the console for an instant-decoder-refresh frame.</summary>
    public static string KeyframeRequest(bool ifr = true) =>
        $$"""{"message":"videoKeyframeRequested","ifrRequested":{{(ifr ? "true" : "false")}}}""";
}
