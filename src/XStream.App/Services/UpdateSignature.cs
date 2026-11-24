using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using XStream.Core.Update;

namespace XStream.App.Services;

/// <summary>
/// Verifies an update's signature, falling back to BouncyCastle (already shipped with the WebRTC library) only when
/// the operating system's crypto can't do ECDSA at all and throws (seen under Wine: NTE_NOT_SUPPORTED, so every
/// update was "not signed by Vexel"). A signature the OS checked and found wrong is still rejected: the fallback
/// is never asked to overrule it.
/// </summary>
internal static class UpdateSignature
{
    public static bool Valid(UpdateManifest m, out string? why)
    {
        if (m.SignatureValid(out why)) return true;
        if (why is null) return false; // the OS checked it: it doesn't match
        try
        {
            var key = (ECPublicKeyParameters)PublicKeyFactory.CreateKey(Convert.FromBase64String(string.Concat(
                UpdateManifest.PublicKeyPem.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("-----")))));
            var sig = Convert.FromBase64String(m.Signature);
            var data = UpdateManifest.SignedPayload(m.Version, m.Url, m.Sha256, m.Size);
            // openssl writes DER signatures; the raw (r||s) form is accepted too, as with the OS check.
            foreach (var alg in new[] { "SHA-256withECDSA", "SHA-256withPLAIN-ECDSA" })
            {
                var v = SignerUtilities.GetSigner(alg);
                v.Init(false, key);
                v.BlockUpdate(data, 0, data.Length);
                try { if (v.VerifySignature(sig)) { why = null; return true; } } catch { /* wrong encoding for this form */ }
            }
            why += "; BouncyCastle: signature doesn't match";
            return false;
        }
        catch (Exception ex) { why += "; BouncyCastle: " + ex.Message; return false; }
    }
}
