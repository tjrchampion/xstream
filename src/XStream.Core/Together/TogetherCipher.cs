using System.Buffers.Binary;
using System.Security.Cryptography;

namespace XStream.Core.Together;

/// <summary>
/// Seals and opens Play together datagrams for one guest connection: AES-256-GCM with a key of its own, every
/// packet authenticated, old and repeated packets refused.
///
/// Datagram: 'V' | flags (bit 0: sent by the host) | connection id (8) | counter (8) | ciphertext | tag (16).
/// The header is authenticated (it's the AAD), so nothing in it can be changed in flight.
///
/// Keys: everyone who has the invite shares its 32-byte key, so each guest connection uses its own key derived
/// from it (HKDF-SHA256, salted with the session id and the guest's random 64-bit connection id). Within a
/// connection the nonce is direction + counter, and each side only ever counts up, so a nonce is never reused.
/// </summary>
public sealed class TogetherCipher : IDisposable
{
    public const byte Marker = 0x56; // 'V'
    public const int HeaderSize = 18, TagSize = 16, Overhead = HeaderSize + TagSize;
    /// <summary>How far behind the newest packet one may arrive (reordering) and still be accepted once.</summary>
    public const int ReplayWindow = 2048;

    private readonly AesGcm _seal, _open;
    private readonly object _sealLock = new();
    private readonly bool _isHost;
    // Starts at the wall clock in microseconds: a connection id that comes back (a guest timed out and rejoined) gets
    // a new cipher with the same key, and its counters must carry on above the old ones, never repeat them.
    private long _sendCounter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

    // Replay protection: the newest counter accepted and a bitmap of the ReplayWindow before it.
    private readonly ulong[] _seen = new ulong[ReplayWindow / 64];
    private ulong _newest;
    private readonly object _openLock = new();

    public ulong ConnectionId { get; }
    public long Rejected { get; private set; }

    public TogetherCipher(ReadOnlySpan<byte> inviteKey, uint sessionId, ulong connectionId, bool isHost)
    {
        if (inviteKey.Length != 32) throw new ArgumentException("The key must be 32 bytes.");
        ConnectionId = connectionId;
        _isHost = isHost;
        Span<byte> salt = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(salt, sessionId);
        BinaryPrimitives.WriteUInt64BigEndian(salt[4..], connectionId);
        Span<byte> key = stackalloc byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, inviteKey, key, salt, "vexel play together 1"u8);
        // Two instances: sealing and opening run on different threads, and AesGcm isn't thread-safe.
        _seal = new AesGcm(key, TagSize);
        _open = new AesGcm(key, TagSize);
        CryptographicOperations.ZeroMemory(key);
    }

    /// <summary>The connection a datagram belongs to (read from its header, before any checks).</summary>
    public static bool TryPeek(ReadOnlySpan<byte> packet, out ulong connectionId, out bool fromHost)
    {
        connectionId = 0; fromHost = false;
        if (packet.Length < Overhead + 1 || packet[0] != Marker) return false;
        fromHost = (packet[1] & 1) != 0;
        connectionId = BinaryPrimitives.ReadUInt64BigEndian(packet[2..]);
        return true;
    }

    /// <summary>Encrypt <paramref name="body"/> into <paramref name="packet"/> (needs body + <see cref="Overhead"/> bytes). Any thread.</summary>
    public int Seal(ReadOnlySpan<byte> body, Span<byte> packet)
    {
        int len = HeaderSize + body.Length + TagSize;
        if (packet.Length < len) throw new ArgumentException("buffer too small");
        ulong counter = (ulong)Interlocked.Increment(ref _sendCounter);
        packet[0] = Marker;
        packet[1] = _isHost ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64BigEndian(packet[2..], ConnectionId);
        BinaryPrimitives.WriteUInt64BigEndian(packet[10..], counter);
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, _isHost, counter);
        lock (_sealLock)
            _seal.Encrypt(nonce, body, packet.Slice(HeaderSize, body.Length), packet.Slice(HeaderSize + body.Length, TagSize), packet[..HeaderSize]);
        return len;
    }

    /// <summary>
    /// Check and decrypt a datagram from the other side into <paramref name="body"/>. False (and nothing written that
    /// matters) when it was tampered with, encrypted with another key, sent by ourselves, or is a repeat or too old.
    /// </summary>
    public bool Open(ReadOnlySpan<byte> packet, Span<byte> body, out int length)
    {
        length = 0;
        if (!TryPeek(packet, out ulong conn, out bool fromHost) || conn != ConnectionId || fromHost == _isHost) { Rejected++; return false; }
        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(packet[10..]);
        int n = packet.Length - Overhead;
        if (body.Length < n || counter == 0) { Rejected++; return false; }
        Span<byte> nonce = stackalloc byte[12];
        Nonce(nonce, fromHost, counter);
        lock (_openLock)
        {
            if (!Fresh(counter)) { Rejected++; return false; }
            try { _open.Decrypt(nonce, packet.Slice(HeaderSize, n), packet.Slice(HeaderSize + n, TagSize), body[..n], packet[..HeaderSize]); }
            catch (AuthenticationTagMismatchException) { Rejected++; return false; }
            // Only an authentic packet moves the window: forged ones can't push real ones out of it.
            Mark(counter);
        }
        length = n;
        return true;
    }

    private static void Nonce(Span<byte> nonce, bool fromHost, ulong counter)
    {
        nonce.Clear();
        nonce[0] = fromHost ? (byte)0x48 : (byte)0x47; // 'H' / 'G'
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }

    private bool Fresh(ulong c)
    {
        if (c > _newest) return true;
        ulong age = _newest - c;
        if (age >= ReplayWindow) return false;
        return (_seen[(int)(c % ReplayWindow) / 64] & (1UL << (int)(c % 64))) == 0;
    }

    private void Mark(ulong c)
    {
        if (c > _newest)
        {
            // Clear the bits of the counters the window moves past (they're new positions now).
            ulong steps = Math.Min(c - _newest, ReplayWindow);
            for (ulong k = 1; k <= steps; k++)
            {
                ulong p = (_newest + k) % ReplayWindow;
                _seen[(int)p / 64] &= ~(1UL << (int)(p % 64));
            }
            _newest = c;
        }
        _seen[(int)(c % ReplayWindow) / 64] |= 1UL << (int)(c % 64);
    }

    public void Dispose() { _seal.Dispose(); _open.Dispose(); }
}
