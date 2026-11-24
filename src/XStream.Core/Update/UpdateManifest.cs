using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XStream.Core.Update;

/// <summary>
/// latest.json, published next to each release's XStream.exe by scripts/publish-windows.sh:
/// what the newest version is, where to get it, its SHA-256, and an ECDSA P-256 signature over those fields
/// made with the release key (kept offline, in ~/.xstream-release). The app only installs an update whose
/// signature checks out against the public key built into it, and whose download matches the hash, so a
/// compromised download host can't push code to anyone.
/// </summary>
public sealed record UpdateManifest(string Version, string Url, string Sha256, long Size, string? Notes, string Signature)
{
    /// <summary>The release signing key's public half (the private half never leaves the release machine).</summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE+XL/FjzD4jPchx0g/J6qKm9Ti+2T
        OAGp/gS9qzNXWummvo2/DqEvWNeUToW6tmjv9x5pbP9vpyOwSMr3DurGcA==
        -----END PUBLIC KEY-----
        """;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The exact bytes that are signed (one field per line).</summary>
    public static byte[] SignedPayload(string version, string url, string sha256, long size) =>
        Encoding.UTF8.GetBytes($"xstream-update-v1\n{version}\n{url}\n{sha256.ToLowerInvariant()}\n{size}");

    public static UpdateManifest Parse(string json) =>
        JsonSerializer.Deserialize<UpdateManifest>(json, Json) ?? throw new InvalidDataException("Empty update manifest");

    /// <summary>True if the signature was made by the release key over these fields.</summary>
    public bool SignatureValid(string publicKeyPem = PublicKeyPem) => SignatureValid(out _, publicKeyPem);

    /// <param name="why">Why it didn't verify (for the update log): a crypto error, or null for a plain mismatch.</param>
    public bool SignatureValid(out string? why, string publicKeyPem = PublicKeyPem)
    {
        why = null;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            var sig = Convert.FromBase64String(Signature);
            var data = SignedPayload(Version, Url, Sha256, Size);
            // openssl writes DER signatures; accept the raw (r||s) form too.
            return key.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
                || key.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    /// <summary>Newer than the running version? (Versions like 1.2.3; anything unparsable counts as not newer.)</summary>
    public bool IsNewerThan(Version current) =>
        System.Version.TryParse(Version, out var v) && v > current;

    /// <summary>Does this file match the manifest's size and SHA-256?</summary>
    public bool Matches(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length != Size) return false;
        using var f = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(f)).Equals(Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
