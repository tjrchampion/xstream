using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace XStream.Core.Together;

/// <summary>How a candidate address was found (the order to try them in, roughly).</summary>
public enum CandidateKind : byte { Lan = 0, Stun = 1, Upnp = 2, NatPmp = 3, Loopback = 4 }

/// <summary>An address one side can maybe be reached at.</summary>
public readonly record struct Candidate(CandidateKind Kind, IPEndPoint EndPoint)
{
    public override string ToString() => $"{Kind} {EndPoint}";
}

/// <summary>
/// What the host's friend pastes to join: where the host might be reachable, the session's key, and its id.
/// Text form: "VX1-" + Crockford base32 of the bytes (with a 2-byte check so a mangled paste is caught).
/// </summary>
public sealed record TogetherInvite(uint SessionId, byte[] Key, IReadOnlyList<Candidate> Candidates)
{
    public const string Prefix = "VX1-";

    public static TogetherInvite New(IReadOnlyList<Candidate> candidates) =>
        new(BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)), RandomNumberGenerator.GetBytes(32), candidates);

    public string Encode()
    {
        var b = new List<byte> { 1 };
        Span<byte> id = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(id, SessionId);
        b.AddRange(id.ToArray());
        b.AddRange(Key);
        TogetherCode.WriteCandidates(b, Candidates);
        return Prefix + TogetherCode.Seal(b);
    }

    /// <summary>Read an invite (spaces, dashes, line breaks and letter case don't matter). Null with a reason when it isn't one.</summary>
    public static TogetherInvite? TryParse(string? text, out string problem)
    {
        if (!TogetherCode.Open(text, Prefix, out var b, out problem)) return null;
        if (b.Length < 1 + 4 + 32 + 1 || b[0] != 1) { problem = "That invite is from a different version of Vexel."; return null; }
        uint session = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(1));
        var key = b.AsSpan(5, 32).ToArray();
        int o = 37;
        if (!TogetherCode.ReadCandidates(b, ref o, out var cands) || cands.Count == 0)
        {
            problem = "That invite has no addresses in it. Ask your friend for a new one.";
            return null;
        }
        problem = "";
        return new TogetherInvite(session, key, cands);
    }
}

/// <summary>
/// The guest's answer when it couldn't reach the host by itself: its own addresses and connection id. The host
/// pastes it, and both sides then send to each other at the same time, which opens most home routers.
/// </summary>
public sealed record TogetherReply(ulong ConnectionId, IReadOnlyList<Candidate> Candidates)
{
    public const string Prefix = "VXR1-";

    public string Encode()
    {
        var b = new List<byte> { 1 };
        Span<byte> id = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(id, ConnectionId);
        b.AddRange(id.ToArray());
        TogetherCode.WriteCandidates(b, Candidates);
        return Prefix + TogetherCode.Seal(b);
    }

    public static TogetherReply? TryParse(string? text, out string problem)
    {
        if (!TogetherCode.Open(text, Prefix, out var b, out problem)) return null;
        if (b.Length < 1 + 8 + 1 || b[0] != 1) { problem = "That reply code is from a different version of Vexel."; return null; }
        ulong conn = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(1));
        int o = 9;
        if (!TogetherCode.ReadCandidates(b, ref o, out var cands) || cands.Count == 0) { problem = "That reply code has no addresses in it."; return null; }
        problem = "";
        return new TogetherReply(conn, cands);
    }
}

/// <summary>Crockford base32 with a short check, and the candidate list format shared by invites and replies.</summary>
public static class TogetherCode
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int MaxCandidates = 8;

    public static string Base32(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (byte x in data)
        {
            buffer = (buffer << 8) | x; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>Decode, forgiving what people do to codes: lower case, O for 0, I/L for 1, spaces, dashes, line breaks.</summary>
    public static byte[]? FromBase32(string text)
    {
        var o = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (char raw in text)
        {
            char ch = char.ToUpperInvariant(raw);
            if (ch is '-' or ' ' or '\n' or '\r' or '\t' or '.') continue;
            ch = ch switch { 'O' => '0', 'I' or 'L' => '1', _ => ch };
            int v = Alphabet.IndexOf(ch);
            if (v < 0) return null;
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { o.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return o.ToArray();
    }

    internal static string Seal(List<byte> payload)
    {
        var hash = SHA256.HashData(payload.ToArray());
        payload.Add(hash[0]); payload.Add(hash[1]);
        return Base32(payload.ToArray());
    }

    internal static bool Open(string? text, string prefix, out byte[] payload, out string problem)
    {
        payload = [];
        text = (text ?? "").Trim();
        // Pasted from a chat: find the code inside whatever came with it.
        int at = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            problem = prefix == TogetherInvite.Prefix && text.Contains(TogetherReply.Prefix, StringComparison.OrdinalIgnoreCase)
                ? "That's a reply code. It goes in the host's stream menu, under Play together."
                : prefix == TogetherReply.Prefix && text.Contains(TogetherInvite.Prefix, StringComparison.OrdinalIgnoreCase)
                    ? "That's an invite code, not a reply code."
                    : $"That doesn't look like a Vexel code (they start with {prefix}).";
            return false;
        }
        // A chat app may have wrapped it over lines, or the friend wrote something after it: try the words after the
        // prefix joined one by one until the check matches.
        var words = text[(at + prefix.Length)..].Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        string joined = "";
        bool anyDecoded = false;
        foreach (var w in words.Take(12))
        {
            joined += w.TrimEnd('.', ',', '!', '?', ')', '"', '\'');
            var raw = FromBase32(joined);
            if (raw is null) break;
            anyDecoded = true;
            if (raw.Length < 4) continue;
            var data = raw.AsSpan(0, raw.Length - 2);
            var hash = SHA256.HashData(data);
            if (hash[0] == raw[^2] && hash[1] == raw[^1])
            {
                payload = data.ToArray();
                problem = "";
                return true;
            }
        }
        problem = anyDecoded ? "That code is incomplete or changed. Copy the whole thing again."
                             : "That code has letters in it that a Vexel code never has. Copy it again.";
        return false;
    }

    internal static void WriteCandidates(List<byte> b, IReadOnlyList<Candidate> cands)
    {
        var list = cands.Take(MaxCandidates).ToList();
        b.Add((byte)list.Count);
        Span<byte> port = stackalloc byte[2];
        foreach (var c in list)
        {
            bool v6 = c.EndPoint.AddressFamily == AddressFamily.InterNetworkV6;
            b.Add((byte)((byte)c.Kind | (v6 ? 0x80 : 0)));
            b.AddRange(c.EndPoint.Address.GetAddressBytes());
            BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)c.EndPoint.Port);
            b.AddRange(port.ToArray());
        }
    }

    internal static bool ReadCandidates(byte[] b, ref int o, out List<Candidate> cands)
    {
        cands = [];
        if (o >= b.Length) return false;
        int n = b[o++];
        for (int i = 0; i < n; i++)
        {
            if (o >= b.Length) return false;
            byte kind = b[o++];
            int alen = (kind & 0x80) != 0 ? 16 : 4;
            if (o + alen + 2 > b.Length) return false;
            var addr = new IPAddress(b.AsSpan(o, alen));
            int port = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o + alen));
            o += alen + 2;
            if (port == 0) continue;
            cands.Add(new Candidate((CandidateKind)(kind & 0x7F), new IPEndPoint(addr, port)));
        }
        return true;
    }
}
