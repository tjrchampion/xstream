namespace XStream.Net.Srtp;

/// <summary>SRTP protection profiles, numbered as DTLS-SRTP's use_srtp extension numbers them (RFC 5764, RFC 7714).</summary>
public enum SrtpProfile : ushort
{
    Aes128CmHmacSha1_80 = 0x0001,
    Aes128CmHmacSha1_32 = 0x0002,
    AeadAes128Gcm = 0x0007,
    AeadAes256Gcm = 0x0008,
}

public static class SrtpProfiles
{
    /// <summary>Master key length in bytes.</summary>
    public static int KeyLength(this SrtpProfile p) => p == SrtpProfile.AeadAes256Gcm ? 32 : 16;

    /// <summary>Master salt length in bytes (112 bits for AES-CM, 96 bits for AES-GCM).</summary>
    public static int SaltLength(this SrtpProfile p) => p.IsGcm() ? 12 : 14;

    public static bool IsGcm(this SrtpProfile p) => p is SrtpProfile.AeadAes128Gcm or SrtpProfile.AeadAes256Gcm;

    /// <summary>Authentication tag bytes added to an SRTP packet.</summary>
    public static int RtpTagLength(this SrtpProfile p) => p switch
    {
        SrtpProfile.AeadAes128Gcm or SrtpProfile.AeadAes256Gcm => 16,
        SrtpProfile.Aes128CmHmacSha1_32 => 4,
        _ => 10,
    };

    /// <summary>Authentication tag bytes added to an SRTCP packet (always 80 bits for the HMAC profiles).</summary>
    public static int RtcpTagLength(this SrtpProfile p) => p.IsGcm() ? 16 : 10;

    /// <summary>The name RFC 5764/7714 give the profile.</summary>
    public static string Name(this SrtpProfile p) => p switch
    {
        SrtpProfile.Aes128CmHmacSha1_80 => "SRTP_AES128_CM_HMAC_SHA1_80",
        SrtpProfile.Aes128CmHmacSha1_32 => "SRTP_AES128_CM_HMAC_SHA1_32",
        SrtpProfile.AeadAes128Gcm => "SRTP_AEAD_AES_128_GCM",
        SrtpProfile.AeadAes256Gcm => "SRTP_AEAD_AES_256_GCM",
        _ => $"0x{(ushort)p:x4}",
    };
}
