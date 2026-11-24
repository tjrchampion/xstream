using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace XStream.Net.Stun;

/// <summary>
/// Just enough STUN (RFC 8489) for ICE connectivity checks (RFC 8445 7.1): build a Binding Request with the ICE
/// attributes, MESSAGE-INTEGRITY and FINGERPRINT, and recognise the matching Binding Success Response. Used to
/// measure the round trip to the console: a connected ICE agent must answer these (they double as RFC 7675
/// consent checks), so the reply time is the network round trip plus the console's turnaround.
/// </summary>
public static class StunBinding
{
    public const int HeaderLength = 20;
    public const int TransactionIdLength = 12;
    private const uint MagicCookie = 0x2112A442;
    private const ushort BindingRequest = 0x0001, BindingSuccess = 0x0101;
    private const ushort AttrUsername = 0x0006, AttrMessageIntegrity = 0x0008, AttrPriority = 0x0024,
        AttrIceControlled = 0x8029, AttrIceControlling = 0x802A, AttrFingerprint = 0x8028;
    private const uint FingerprintXor = 0x5354554E;

    /// <summary>Largest request <see cref="BuildRequest"/> writes for a username of <paramref name="usernameBytes"/> bytes.</summary>
    public static int MaxRequestLength(int usernameBytes) => HeaderLength + 4 + Pad4(usernameBytes) + 8 + 12 + 24 + 8;

    /// <summary>
    /// Write a Binding Request into <paramref name="dst"/> and return its length.
    /// <paramref name="username"/> is "remoteUfrag:localUfrag"; <paramref name="password"/> is the remote ICE password.
    /// </summary>
    public static int BuildRequest(Span<byte> dst, ReadOnlySpan<byte> transactionId, string username, string password,
        uint priority, bool controlling, ulong tiebreaker)
    {
        if (transactionId.Length != TransactionIdLength) throw new ArgumentException("transaction ID must be 12 bytes");
        int userLen = Encoding.UTF8.GetByteCount(username);
        if (dst.Length < MaxRequestLength(userLen)) throw new ArgumentException("buffer too small");

        BinaryPrimitives.WriteUInt16BigEndian(dst, BindingRequest);
        BinaryPrimitives.WriteUInt32BigEndian(dst[4..], MagicCookie);
        transactionId.CopyTo(dst[8..]);
        int at = HeaderLength;

        at = AttrHeader(dst, at, AttrUsername, userLen);
        Encoding.UTF8.GetBytes(username, dst[at..]);
        dst.Slice(at + userLen, Pad4(userLen) - userLen).Clear();
        at += Pad4(userLen);

        at = AttrHeader(dst, at, AttrPriority, 4);
        BinaryPrimitives.WriteUInt32BigEndian(dst[at..], priority);
        at += 4;

        at = AttrHeader(dst, at, controlling ? AttrIceControlling : AttrIceControlled, 8);
        BinaryPrimitives.WriteUInt64BigEndian(dst[at..], tiebreaker);
        at += 8;

        // MESSAGE-INTEGRITY: HMAC-SHA1 over the message up to (not including) this attribute, with the header
        // length already counting it (RFC 8489 14.5). Short-term credential: key = password (SASLprep is a no-op
        // for ICE's base64-ish passwords).
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..], (ushort)(at + 24 - HeaderLength));
        at = AttrHeader(dst, at, AttrMessageIntegrity, 20);
        HMACSHA1.HashData(Encoding.UTF8.GetBytes(password), dst[..(at - 4)], dst[at..]);
        at += 20;

        // FINGERPRINT: CRC-32 of everything before it, length again including it.
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..], (ushort)(at + 8 - HeaderLength));
        at = AttrHeader(dst, at, AttrFingerprint, 4);
        BinaryPrimitives.WriteUInt32BigEndian(dst[at..], Crc32(dst[..(at - 4)]) ^ FingerprintXor);
        at += 4;
        return at;
    }

    /// <summary>
    /// True if <paramref name="packet"/> is a STUN Binding Success Response; <paramref name="transactionId"/> is then
    /// its transaction ID. Cheap enough to call on every non-RTP packet.
    /// </summary>
    public static bool IsBindingSuccess(ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> transactionId)
    {
        transactionId = default;
        if (packet.Length < HeaderLength || (packet[0] & 0xC0) != 0) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(packet) != BindingSuccess) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(packet[4..]) != MagicCookie) return false;
        if (HeaderLength + BinaryPrimitives.ReadUInt16BigEndian(packet[2..]) != packet.Length) return false;
        transactionId = packet.Slice(8, TransactionIdLength);
        return true;
    }

    private static int AttrHeader(Span<byte> dst, int at, ushort type, int length)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst[at..], type);
        BinaryPrimitives.WriteUInt16BigEndian(dst[(at + 2)..], (ushort)length);
        return at + 4;
    }

    private static int Pad4(int n) => (n + 3) & ~3;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>CRC-32 (ISO-HDLC, as used by STUN FINGERPRINT and zip).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
