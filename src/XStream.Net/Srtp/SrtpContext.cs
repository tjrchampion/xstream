using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XStream.Net.Srtp;

/// <summary>
/// One direction of SRTP and SRTCP (RFC 3711, with AES-GCM from RFC 7714): either the keys we protect our
/// packets with, or the keys the console protects its packets with. Packets are processed in place with .NET's
/// hardware AES (AES-NI / ARMv8 crypto), so the receive path allocates nothing per packet. Compared with the
/// BouncyCastle SRTP in SIPSorcery (which rebuilds the AES-GCM key schedule for every packet): about 3x faster
/// and no garbage.
///
/// Thread-safe (one lock per call): video, audio and RTCP may be handled on different threads.
/// </summary>
public sealed class SrtpContext : IDisposable
{
    /// <summary>
    /// Packets tolerated out of order. WebRTC uses 1024; a NACK resend at 1440p (about 3,000 packets a second)
    /// can arrive well over 64 packets late, and the console resends with the original sequence number.
    /// </summary>
    public const int ReplayWindowSize = 4096;

    public SrtpProfile Profile { get; }

    /// <summary>Packets dropped because their authentication tag didn't match (corrupt, or not from the console).</summary>
    public long AuthFailures { get; private set; }

    /// <summary>Packets dropped as duplicates (or older than the replay window).</summary>
    public long Replays { get; private set; }

    private readonly object _lock = new();
    private readonly Keys _rtp, _rtcp;
    private readonly int _rtpTag, _rtcpTag;
    private readonly Dictionary<uint, RtpStream> _rtpStreams = [];
    private readonly Dictionary<uint, RtcpStream> _rtcpStreams = [];
    private byte[] _counter = new byte[16 * 96], _keystream = new byte[16 * 96]; // AES-CM scratch, grown if needed
    private readonly byte[] _iv = new byte[12], _aad = new byte[64], _mac = new byte[20], _roc = new byte[4];

    public SrtpContext(SrtpProfile profile, ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> masterSalt)
    {
        if (masterKey.Length != profile.KeyLength()) throw new ArgumentException($"{profile.Name()} needs a {profile.KeyLength()}-byte key");
        if (masterSalt.Length != profile.SaltLength()) throw new ArgumentException($"{profile.Name()} needs a {profile.SaltLength()}-byte salt");
        Profile = profile;
        _rtpTag = profile.RtpTagLength();
        _rtcpTag = profile.RtcpTagLength();
        using var kdf = Aes.Create();
        kdf.Key = masterKey.ToArray();
        _rtp = new Keys(profile, kdf, masterSalt, labelBase: 0);
        _rtcp = new Keys(profile, kdf, masterSalt, labelBase: 3);
    }

    /// <summary>
    /// The SRTP key derivation function (RFC 3711 section 4.3, key derivation rate 0): AES in counter mode, keyed
    /// with the master key, over the master salt XOR the label. GCM's 96-bit salt is padded with zeros to 112 bits
    /// (RFC 7714 section 11, as libsrtp does).
    /// </summary>
    internal static byte[] Derive(Aes master, ReadOnlySpan<byte> masterSalt, byte label, int length)
    {
        int blocks = (length + 15) / 16;
        var input = new byte[blocks * 16];
        for (int b = 0; b < blocks; b++)
        {
            var block = input.AsSpan(b * 16, 16);
            masterSalt.CopyTo(block);
            block[7] ^= label;
            BinaryPrimitives.WriteUInt16BigEndian(block[14..], (ushort)b);
        }
        return master.EncryptEcb(input, PaddingMode.None)[..length];
    }

    // ---------------------------------------------------------------- RTP

    /// <summary>
    /// Authenticate and decrypt an SRTP packet in place. On success <paramref name="length"/> is the length of the
    /// plain RTP packet (the tag removed); on failure (bad tag, replay, malformed) the packet must be dropped.
    /// </summary>
    public bool UnprotectRtp(Span<byte> packet, out int length)
    {
        length = 0;
        int hlen = RtpHeaderLength(packet);
        if (hlen < 0 || packet.Length < hlen + _rtpTag) return false;
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        int bodyLen = packet.Length - hlen - _rtpTag;

        lock (_lock)
        {
            if (!_rtpStreams.TryGetValue(ssrc, out var st)) _rtpStreams[ssrc] = st = new RtpStream();
            long index = st.Estimate(seq, out uint roc);
            if (index < 0 || !st.Window.Accepts(index)) { Replays++; return false; }

            if (Profile.IsGcm())
            {
                RtpIv(ssrc, roc, seq);
                try
                {
                    _rtp.Gcm!.Decrypt(_iv, packet.Slice(hlen, bodyLen), packet.Slice(hlen + bodyLen, _rtpTag),
                        packet.Slice(hlen, bodyLen), packet[..hlen]);
                }
                catch (CryptographicException) { AuthFailures++; return false; }
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(_roc, roc);
                if (!HmacMatches(_rtp, packet[..(hlen + bodyLen)], _roc, packet.Slice(hlen + bodyLen, _rtpTag))) { AuthFailures++; return false; }
                CtrXor(_rtp, ssrc, (ulong)index, packet.Slice(hlen, bodyLen));
            }

            st.Accepted(index, seq, roc);
        }
        length = hlen + bodyLen;
        return true;
    }

    /// <summary>
    /// Encrypt and authenticate an RTP packet of <paramref name="length"/> bytes in place. The buffer needs room
    /// for the tag (<see cref="SrtpProfiles.RtpTagLength"/> more bytes). Returns the SRTP packet's length.
    /// </summary>
    public int ProtectRtp(Span<byte> buffer, int length)
    {
        var packet = buffer[..length];
        int hlen = RtpHeaderLength(packet);
        if (hlen < 0) throw new ArgumentException("not an RTP packet");
        if (buffer.Length < length + _rtpTag) throw new ArgumentException("no room for the SRTP tag");
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        int bodyLen = length - hlen;

        lock (_lock)
        {
            if (!_rtpStreams.TryGetValue(ssrc, out var st)) _rtpStreams[ssrc] = st = new RtpStream();
            uint roc = st.Sending(seq);
            if (Profile.IsGcm())
            {
                RtpIv(ssrc, roc, seq);
                _rtp.Gcm!.Encrypt(_iv, packet.Slice(hlen, bodyLen), packet.Slice(hlen, bodyLen), buffer.Slice(length, _rtpTag), packet[..hlen]);
            }
            else
            {
                CtrXor(_rtp, ssrc, ((ulong)roc << 16) | seq, packet.Slice(hlen, bodyLen));
                BinaryPrimitives.WriteUInt32BigEndian(_roc, roc);
                Hmac(_rtp, packet, _roc, buffer.Slice(length, _rtpTag));
            }
        }
        return length + _rtpTag;
    }

    // ---------------------------------------------------------------- RTCP

    /// <summary>Authenticate and decrypt an SRTCP packet in place; <paramref name="length"/> is the plain RTCP length.</summary>
    public bool UnprotectRtcp(Span<byte> packet, out int length)
    {
        length = 0;
        if (packet.Length < 8 + 4 + _rtcpTag || (packet[0] >> 6) != 2) return false;
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        bool gcm = Profile.IsGcm();
        // GCM: header | ciphertext | tag | E+index.   HMAC: header | ciphertext | E+index | tag.
        int eIndexAt = gcm ? packet.Length - 4 : packet.Length - _rtcpTag - 4;
        uint eIndex = BinaryPrimitives.ReadUInt32BigEndian(packet[eIndexAt..]);
        bool encrypted = (eIndex & 0x8000_0000) != 0;
        uint index = eIndex & 0x7FFF_FFFF;
        int bodyLen = packet.Length - 8 - 4 - _rtcpTag;

        lock (_lock)
        {
            if (!_rtcpStreams.TryGetValue(ssrc, out var st)) _rtcpStreams[ssrc] = st = new RtcpStream();
            if (!st.Window.Accepts(index)) { Replays++; return false; }

            if (gcm)
            {
                RtcpIv(ssrc, index);
                var tag = packet.Slice(8 + bodyLen, _rtcpTag);
                try
                {
                    if (encrypted)
                    {
                        // Associated data: the first 8 header bytes and the E+index word.
                        packet[..8].CopyTo(_aad);
                        BinaryPrimitives.WriteUInt32BigEndian(_aad.AsSpan(8), eIndex);
                        _rtcp.Gcm!.Decrypt(_iv, packet.Slice(8, bodyLen), tag, packet.Slice(8, bodyLen), _aad.AsSpan(0, 12));
                    }
                    else
                    {
                        // Authenticated only: the whole packet (and E+index) is associated data.
                        var aad = 8 + bodyLen + 4 <= _aad.Length ? _aad.AsSpan(0, 8 + bodyLen + 4) : new byte[8 + bodyLen + 4];
                        packet[..(8 + bodyLen)].CopyTo(aad);
                        BinaryPrimitives.WriteUInt32BigEndian(aad[(8 + bodyLen)..], eIndex);
                        _rtcp.Gcm!.Decrypt(_iv, ReadOnlySpan<byte>.Empty, tag, Span<byte>.Empty, aad);
                    }
                }
                catch (CryptographicException) { AuthFailures++; return false; }
            }
            else
            {
                if (!HmacMatches(_rtcp, packet[..(eIndexAt + 4)], default, packet.Slice(eIndexAt + 4, _rtcpTag))) { AuthFailures++; return false; }
                if (encrypted) CtrXor(_rtcp, ssrc, index, packet.Slice(8, bodyLen));
            }
            st.Window.Accept(index);
        }
        length = 8 + bodyLen;
        return true;
    }

    /// <summary>Encrypt and authenticate an RTCP packet in place (needs 4 + tag bytes of room). Returns the SRTCP length.</summary>
    public int ProtectRtcp(Span<byte> buffer, int length)
    {
        if (length < 8) throw new ArgumentException("not an RTCP packet");
        if (buffer.Length < length + 4 + _rtcpTag) throw new ArgumentException("no room for the SRTCP trailer");
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(buffer[4..]);
        int bodyLen = length - 8;
        lock (_lock)
        {
            if (!_rtcpStreams.TryGetValue(ssrc, out var st)) _rtcpStreams[ssrc] = st = new RtcpStream();
            uint index = st.NextSendIndex();
            uint eIndex = 0x8000_0000 | index;
            if (Profile.IsGcm())
            {
                RtcpIv(ssrc, index);
                buffer[..8].CopyTo(_aad);
                BinaryPrimitives.WriteUInt32BigEndian(_aad.AsSpan(8), eIndex);
                _rtcp.Gcm!.Encrypt(_iv, buffer.Slice(8, bodyLen), buffer.Slice(8, bodyLen), buffer.Slice(length, _rtcpTag), _aad.AsSpan(0, 12));
                BinaryPrimitives.WriteUInt32BigEndian(buffer[(length + _rtcpTag)..], eIndex);
            }
            else
            {
                CtrXor(_rtcp, ssrc, index, buffer.Slice(8, bodyLen));
                BinaryPrimitives.WriteUInt32BigEndian(buffer[length..], eIndex);
                Hmac(_rtcp, buffer[..(length + 4)], default, buffer.Slice(length + 4, _rtcpTag));
            }
        }
        return length + 4 + _rtcpTag;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Length of the RTP header including CSRCs and the header extension; -1 if malformed.</summary>
    public static int RtpHeaderLength(ReadOnlySpan<byte> p)
    {
        if (p.Length < 12 || (p[0] >> 6) != 2) return -1;
        int len = 12 + 4 * (p[0] & 0x0F);
        if ((p[0] & 0x10) != 0)
        {
            if (p.Length < len + 4) return -1;
            len += 4 + 4 * BinaryPrimitives.ReadUInt16BigEndian(p[(len + 2)..]);
        }
        return len <= p.Length ? len : -1;
    }

    // GCM nonce for RTP (RFC 7714 section 8.1): 00 00 | SSRC | ROC | SEQ, XOR the session salt.
    private void RtpIv(uint ssrc, uint roc, ushort seq)
    {
        _iv[0] = 0; _iv[1] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(_iv.AsSpan(2), ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(_iv.AsSpan(6), roc);
        BinaryPrimitives.WriteUInt16BigEndian(_iv.AsSpan(10), seq);
        for (int i = 0; i < 12; i++) _iv[i] ^= _rtp.Salt[i];
    }

    // GCM nonce for RTCP (RFC 7714 section 9.1): 00 00 | SSRC | 00 00 | 0 + SRTCP index, XOR the session salt.
    private void RtcpIv(uint ssrc, uint index)
    {
        _iv[0] = 0; _iv[1] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(_iv.AsSpan(2), ssrc);
        _iv[6] = 0; _iv[7] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(_iv.AsSpan(8), index & 0x7FFF_FFFF);
        for (int i = 0; i < 12; i++) _iv[i] ^= _rtcp.Salt[i];
    }

    // AES counter mode (RFC 3711 section 4.1.1): IV = salt XOR (SSRC << 64) XOR (index << 16), counter in the low 16 bits.
    private void CtrXor(Keys k, uint ssrc, ulong index, Span<byte> data)
    {
        int blocks = (data.Length + 15) / 16;
        if (_counter.Length < blocks * 16) { _counter = new byte[blocks * 16]; _keystream = new byte[blocks * 16]; }
        Span<byte> iv = stackalloc byte[16];
        k.Salt.AsSpan(0, 14).CopyTo(iv);
        iv[14] = 0; iv[15] = 0;
        iv[4] ^= (byte)(ssrc >> 24); iv[5] ^= (byte)(ssrc >> 16); iv[6] ^= (byte)(ssrc >> 8); iv[7] ^= (byte)ssrc;
        for (int i = 0; i < 6; i++) iv[13 - i] ^= (byte)(index >> (8 * i));
        for (int b = 0; b < blocks; b++)
        {
            var c = _counter.AsSpan(b * 16, 16);
            iv.CopyTo(c);
            BinaryPrimitives.WriteUInt16BigEndian(c[14..], (ushort)b);
        }
        k.Ctr!.EncryptEcb(_counter.AsSpan(0, blocks * 16), _keystream.AsSpan(0, blocks * 16), PaddingMode.None);
        Xor(data, _keystream);
    }

    private static void Xor(Span<byte> data, ReadOnlySpan<byte> key)
    {
        int i = 0;
        var dv = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(data);
        var kv = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(key[..data.Length]);
        for (; i < dv.Length; i++) dv[i] ^= kv[i];
        for (i *= 8; i < data.Length; i++) data[i] ^= key[i];
    }

    private void Hmac(Keys k, ReadOnlySpan<byte> data, ReadOnlySpan<byte> suffix, Span<byte> tagOut)
    {
        k.Hmac!.AppendData(data);
        if (!suffix.IsEmpty) k.Hmac.AppendData(suffix);
        k.Hmac.GetHashAndReset(_mac);
        _mac.AsSpan(0, tagOut.Length).CopyTo(tagOut);
    }

    private bool HmacMatches(Keys k, ReadOnlySpan<byte> data, ReadOnlySpan<byte> suffix, ReadOnlySpan<byte> tag)
    {
        k.Hmac!.AppendData(data);
        if (!suffix.IsEmpty) k.Hmac.AppendData(suffix);
        k.Hmac.GetHashAndReset(_mac);
        return CryptographicOperations.FixedTimeEquals(_mac.AsSpan(0, tag.Length), tag);
    }

    public void Dispose() { _rtp.Dispose(); _rtcp.Dispose(); }

    /// <summary>Session keys for RTP (labels 0-2) or RTCP (labels 3-5).</summary>
    private sealed class Keys : IDisposable
    {
        public readonly AesGcm? Gcm;
        public readonly Aes? Ctr;
        public readonly IncrementalHash? Hmac;
        public readonly byte[] Salt;

        public Keys(SrtpProfile p, Aes kdf, ReadOnlySpan<byte> masterSalt, byte labelBase)
        {
            var key = Derive(kdf, masterSalt, labelBase, p.KeyLength());
            Salt = Derive(kdf, masterSalt, (byte)(labelBase + 2), p.SaltLength());
            if (p.IsGcm()) Gcm = new AesGcm(key, 16);
            else
            {
                Ctr = Aes.Create();
                Ctr.Key = key;
                Hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, Derive(kdf, masterSalt, (byte)(labelBase + 1), 20));
            }
            CryptographicOperations.ZeroMemory(key);
        }

        public void Dispose() { Gcm?.Dispose(); Ctr?.Dispose(); Hmac?.Dispose(); }
    }

    /// <summary>Per-SSRC RTP state: rollover counter, highest sequence number, replay window.</summary>
    private sealed class RtpStream
    {
        public readonly ReplayWindow Window = new();
        private bool _started;
        private uint _roc;
        private ushort _highest;

        /// <summary>The packet's 48-bit index from its sequence number (RFC 3711 appendix A); -1 if before the start.</summary>
        public long Estimate(ushort seq, out uint roc)
        {
            if (!_started) { roc = 0; return seq; }
            long v = _roc;
            if (_highest < 32768) { if (seq - _highest > 32768) v--; }
            else if (_highest - 32768 > seq) v++;
            roc = (uint)Math.Max(0, v);
            return v < 0 ? -1 : (v << 16) | seq;
        }

        /// <summary>After a packet authenticated: advance the rollover counter / highest sequence number.</summary>
        public void Accepted(long index, ushort seq, uint roc)
        {
            Window.Accept(index);
            if (!_started) { _started = true; _roc = roc; _highest = seq; }
            else if (roc > _roc) { _roc = roc; _highest = seq; }
            else if (roc == _roc && seq > _highest) _highest = seq;
        }

        /// <summary>The rollover counter for a packet we send (sequence numbers only go up).</summary>
        public uint Sending(ushort seq)
        {
            if (_started && seq < _highest && _highest - seq > 32768) _roc++;
            _started = true;
            _highest = seq;
            return _roc;
        }
    }

    private sealed class RtcpStream
    {
        public readonly ReplayWindow Window = new();
        private uint _next;
        public uint NextSendIndex() { uint i = _next; _next = (_next + 1) & 0x7FFF_FFFF; return i; }
    }

    /// <summary>Which recent packet indexes have been seen (a circular bitmap behind the highest index).</summary>
    internal sealed class ReplayWindow
    {
        private readonly ulong[] _bits = new ulong[ReplayWindowSize / 64];
        private long _top = -1;

        public bool Accepts(long index)
        {
            if (index > _top) return true;
            if (_top - index >= ReplayWindowSize) return false;
            return (_bits[Slot(index)] & Bit(index)) == 0;
        }

        public void Accept(long index)
        {
            if (index > _top)
            {
                if (_top < 0 || index - _top >= ReplayWindowSize) Array.Clear(_bits);
                else for (long i = _top + 1; i < index; i++) _bits[Slot(i)] &= ~Bit(i);
                _top = index;
            }
            _bits[Slot(index)] |= Bit(index);
        }

        private static int Slot(long i) => (int)(i % ReplayWindowSize / 64);
        private static ulong Bit(long i) => 1UL << (int)(i % 64);
    }
}
