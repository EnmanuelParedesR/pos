using System.Security.Cryptography;
using System.Text;

namespace Abastalia.BuildingBlocks.Security;

/// <summary>TK-241: <c>Authorization: Node &lt;nodeId&gt;.&lt;unixSeconds&gt;.&lt;nonce&gt;.&lt;signature&gt;</c>, the signature (ECDSA P-256, SHA-256,
/// IEEE P1363, Base64Url) over <c>nodeId.unixSeconds.nonce.METHOD /path?query bodyHash</c> with the node's private key, bodyHash being
/// Base64Url(SHA-256(body)) (of nothing for an empty body, R10 #7; of the JSON for a gzip batch, which RequestDecompression inflates first). Valid for
/// <see cref="Window"/> either side of the cloud's clock and once only (the nonce). The store signs the same text (Abastalia.Sync CloudClient).
/// TK-321: the Lite host checks its selling devices with it too (header <c>X-Device-Signature</c>, the device id in place of the node id).</summary>
public static class NodeSignature
{
    /// <summary>Wide on purpose: shop PCs' clocks drift; the store also corrects by the cloud's Date header.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public static string Payload(Guid nodeId, long unixSeconds, string nonce, string method, string pathAndQuery, string bodyHash) =>
        $"{nodeId}.{unixSeconds}.{nonce}.{method.ToUpperInvariant()} {pathAndQuery} {bodyHash}";

    public static string BodyHash(ReadOnlySpan<byte> body) => Base64Url(SHA256.HashData(body));

    /// <summary>R12 #1: the bodyHash the node signed, sent in clear so the signature is checked before the body is read.</summary>
    public const string BodyHashHeader = "X-Body-Sha256";

    /// <summary>The SubjectPublicKeyInfo of a P-256 key, or null.</summary>
    public static byte[]? PublicKey(string? base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > 200) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out _);
            return key.KeySize == 256 ? bytes : null;
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return null; }
    }

    public static bool Verify(byte[] publicKey, string payload, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(payload), FromBase64Url(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return false; }
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
