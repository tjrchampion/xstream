using System.Buffers.Binary;
using System.Security.Cryptography;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using XStream.Net.Srtp;

/// <summary>Our SRTP against the RFC test vector and, both ways, against SIPSorcery's (known to work with the Xbox).</summary>
static class SrtpTests
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("SRTP key derivation matches RFC 3711 B.3", () =>
        {
            using var aes = Aes.Create();
            aes.Key = Convert.FromHexString("E1F97A0D3E018BE0D64FA32C06DE4139");
            var salt = Convert.FromHexString("0EC675AD498AFEEBB6960B3AABE6");
            Same("C61E7A93744F39EE10734AFE3FF7A087", SrtpContext.Derive(aes, salt, 0, 16));
            Same("30CBBC08863D8C85D49DB34A9AE1", SrtpContext.Derive(aes, salt, 2, 14));
            Same("CEBE321F6FF7716B6FD4AB49AF256A156D38BAA4", SrtpContext.Derive(aes, salt, 1, 20));
        }),
        ("SRTP round trip, every profile, across a sequence number wrap", () =>
        {
            foreach (var profile in Enum.GetValues<SrtpProfile>())
            {
                var (key, salt) = Keys(profile);
                using var tx = new SrtpContext(profile, key, salt);
                using var rx = new SrtpContext(profile, key, salt);
                for (int i = 0; i < 300; i++)
                {
                    ushort seq = (ushort)(65_400 + i); // wraps at i = 136: the rollover counter must follow
                    var (plain, buf) = Rtp(seq, 0x1234_5678, 200 + i, extension: i % 3 == 0);
                    int n = tx.ProtectRtp(buf, plain.Length);
                    Check(!buf.AsSpan(12, 40).SequenceEqual(plain.AsSpan(12, 40)), $"{profile} payload encrypted");
                    Check(rx.UnprotectRtp(buf.AsSpan(0, n), out int len), $"{profile} unprotect #{i}");
                    Check(buf.AsSpan(0, len).SequenceEqual(plain), $"{profile} plaintext #{i}");
                }
                var (rtcp, rbuf) = Rtcp(0x1234_5678);
                int m = tx.ProtectRtcp(rbuf, rtcp.Length);
                Check(rx.UnprotectRtcp(rbuf.AsSpan(0, m), out int rl) && rbuf.AsSpan(0, rl).SequenceEqual(rtcp), $"{profile} RTCP");
            }
        }),
        ("SRTP rejects tampering and replays, accepts late resends", () =>
        {
            var p = SrtpProfile.AeadAes256Gcm;
            var (key, salt) = Keys(p);
            using var tx = new SrtpContext(p, key, salt);
            using var rx = new SrtpContext(p, key, salt);
            var sent = new List<byte[]>();
            for (int i = 0; i < 2000; i++)
            {
                var (plain, buf) = Rtp((ushort)i, 7, 1100, false);
                sent.Add(buf[..tx.ProtectRtp(buf, plain.Length)]);
            }
            for (int i = 0; i < 2000; i++) if (i != 10) Check(rx.UnprotectRtp(sent[i].ToArray(), out _), $"packet {i}");
            Check(rx.UnprotectRtp(sent[10].ToArray(), out _), "resend 1990 packets late is accepted");
            False(rx.UnprotectRtp(sent[10].ToArray(), out _), "duplicate");
            var bad = sent[1999].ToArray(); bad[40] ^= 1;
            var rx2 = new SrtpContext(p, key, salt);
            False(rx2.UnprotectRtp(bad, out _), "tampered packet");
            Check(rx2.AuthFailures == 1 && rx.Replays == 1, "counted");
        }),
        ("SRTP interoperates with SIPSorcery both ways (AES-256-GCM, AES-128-GCM, AES-128-CM)", () =>
        {
            foreach (var (profile, suite) in new[]
                     {
                         (SrtpProfile.AeadAes256Gcm, "AEAD_AES_256_GCM"), (SrtpProfile.AeadAes128Gcm, "AEAD_AES_128_GCM"),
                         (SrtpProfile.Aes128CmHmacSha1_80, "AES_CM_128_HMAC_SHA1_80"),
                     })
            {
                var (key, salt) = Keys(profile);
                var desc = new List<SDPSecurityDescription> { SDPSecurityDescription.Parse($"a=crypto:1 {suite} inline:" + Convert.ToBase64String([.. key, .. salt])) };
                var sipTx = new SrtpHandler(); sipTx.SetupLocal(desc, SdpType.offer); sipTx.SetupRemote(desc, SdpType.answer);
                var sipRx = new SrtpHandler(); sipRx.SetupRemote(desc, SdpType.offer); sipRx.SetupLocal(desc, SdpType.answer);
                using var ours = new SrtpContext(profile, key, salt);
                using var oursTx = new SrtpContext(profile, key, salt);
                for (int i = 0; i < 50; i++)
                {
                    // SIPSorcery protects, we unprotect.
                    var (plain, buf) = Rtp((ushort)(1000 + i), 0xCAFE, 900 + i, extension: i % 2 == 0);
                    Check(sipTx.ProtectRTP(buf, plain.Length, out int n) == 0, "SIPSorcery protect");
                    Check(ours.UnprotectRtp(buf.AsSpan(0, n), out int len) && buf.AsSpan(0, len).SequenceEqual(plain), $"{suite}: ours reads SIPSorcery's #{i}");
                    // We protect, SIPSorcery unprotects.
                    var (plain2, buf2) = Rtp((ushort)(5000 + i), 0xBEEF, 700 + i, extension: false);
                    int n2 = oursTx.ProtectRtp(buf2, plain2.Length);
                    Check(sipRx.UnprotectRTP(buf2, n2, out int len2) == 0 && buf2.AsSpan(0, len2).SequenceEqual(plain2), $"{suite}: SIPSorcery reads ours #{i}");
                }
                var (rtcp, rbuf) = Rtcp(0xCAFE);
                Check(sipTx.ProtectRTCP(rbuf, rtcp.Length, out int rn) == 0, "SIPSorcery RTCP protect");
                Check(ours.UnprotectRtcp(rbuf.AsSpan(0, rn), out int rl) && rbuf.AsSpan(0, rl).SequenceEqual(rtcp), $"{suite}: ours reads SIPSorcery's RTCP");
                var (rtcp2, rbuf2) = Rtcp(0xBEEF);
                int rn2 = oursTx.ProtectRtcp(rbuf2, rtcp2.Length);
                Check(sipRx.UnprotectRTCP(rbuf2, rn2, out int rl2) == 0 && rbuf2.AsSpan(0, rl2).SequenceEqual(rtcp2), $"{suite}: SIPSorcery reads our RTCP");
            }
        }),
    ];

    private static (byte[] key, byte[] salt) Keys(SrtpProfile p)
    {
        var key = new byte[p.KeyLength()]; var salt = new byte[p.SaltLength()];
        RandomNumberGenerator.Fill(key); RandomNumberGenerator.Fill(salt);
        return (key, salt);
    }

    /// <summary>A plain RTP packet, and a copy in a buffer with room for the tag.</summary>
    private static (byte[] plain, byte[] buf) Rtp(ushort seq, uint ssrc, int payload, bool extension)
    {
        int ext = extension ? 8 : 0;
        var p = new byte[12 + ext + payload];
        p[0] = (byte)(0x80 | (extension ? 0x10 : 0)); p[1] = 96;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), seq);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), seq * 1500u);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), ssrc);
        if (extension) { p[12] = 0xBE; p[13] = 0xDE; p[15] = 1; p[16] = 0x10; p[17] = 0x42; }
        RandomNumberGenerator.Fill(p.AsSpan(12 + ext));
        var buf = new byte[p.Length + 64];
        p.CopyTo(buf, 0);
        return (p, buf);
    }

    /// <summary>A receiver report with one report block.</summary>
    private static (byte[] plain, byte[] buf) Rtcp(uint ssrc)
    {
        var p = new byte[32];
        p[0] = 0x81; p[1] = 201; BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), ssrc);
        RandomNumberGenerator.Fill(p.AsSpan(8));
        var buf = new byte[p.Length + 64];
        p.CopyTo(buf, 0);
        return (p, buf);
    }

    private static void Same(string hex, byte[] actual)
    {
        if (!Convert.ToHexString(actual).Equals(hex, StringComparison.OrdinalIgnoreCase))
            throw new Exception($"expected {hex}, got {Convert.ToHexString(actual)}");
    }

    private static void Check(bool c, string what) { if (!c) throw new Exception("failed: " + what); }
    private static void False(bool c, string what) { if (c) throw new Exception("expected false: " + what); }
}
