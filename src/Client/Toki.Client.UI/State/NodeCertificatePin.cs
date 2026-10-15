using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Toki.Client.UI.State;

/// <summary>TK-122 (ADR-0009): the store server's self-signed certificate, pinned when this PC pairs. Before pairing any certificate is
/// accepted and remembered (trust on first use, compared by eye with the fingerprint the owner sees); after, only the pinned one.</summary>
/// <remarks>Shared by every connection of the app (the HTTP handler is a singleton); <see cref="Session"/> loads and saves the pin.</remarks>
public sealed class NodeCertificatePin
{
    private volatile string? _pinned;
    private volatile string? _seen;

    /// <summary>SHA-256 of the pinned certificate, or null while this PC is not paired (or talks plain http in development).</summary>
    public string? Pinned => _pinned;

    /// <summary>SHA-256 of the certificate of the last connection while unpaired: the one that gets pinned when pairing succeeds.</summary>
    public string? Seen => _seen;

    public void Pin(string? thumbprint) => _pinned = thumbprint;

    /// <summary>The TLS check of every connection to the store server (SocketsHttpHandler.SslOptions).</summary>
    public bool Validate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        var thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (_pinned is { } pin) return string.Equals(thumbprint, pin, StringComparison.OrdinalIgnoreCase);
        _seen = thumbprint;
        return true;
    }

    /// <summary>What people compare: the first 64 bits of the SHA-256, "AB12 CD34 EF56 7890". Making another certificate with the same
    /// 64 bits takes ~2^64 tries, far beyond a 10-minute pairing window.</summary>
    public static string? Fingerprint(string? thumbprint) =>
        thumbprint is { Length: >= 16 } t ? string.Join(' ', Enumerable.Range(0, 4).Select(i => t.Substring(i * 4, 4).ToUpperInvariant())) : null;
}
