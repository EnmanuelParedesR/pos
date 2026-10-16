using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Toki.Client.UI.State;

/// <summary>Which store server this PC trusts (ADR-0009). Before pairing any certificate is accepted and remembered (trust on first use,
/// compared by eye with the fingerprint the owner sees). Once paired: the store CA (TK-091), so the server can renew its certificate,
/// and/or the exact server certificate seen when pairing (registers paired before TK-091, or with a server still in dual mode).</summary>
/// <remarks>Shared by every connection of the app (the HTTP handler is a singleton); <see cref="Session"/> loads and saves it. It also holds
/// this PC's own client certificate, which the handler presents to the server.</remarks>
public sealed class NodeCertificatePin
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private volatile string? _pinned;
    private volatile string? _seen;
    private volatile X509Certificate2? _ca;
    private volatile X509Certificate2? _seenCertificate;
    private volatile X509Certificate2? _client;

    /// <summary>SHA-256 of the pinned server certificate, or null (CA only, unpaired, or plain http in development).</summary>
    public string? Pinned => _pinned;

    /// <summary>The pinned store CA, or null.</summary>
    public X509Certificate2? Ca => _ca;

    public bool IsPinned => _pinned is not null || _ca is not null;

    /// <summary>SHA-256 of the server certificate of the last connection it accepted: before pairing, the one that gets pinned.</summary>
    public string? Seen => _seen;

    /// <summary>This PC's client certificate with its key (TK-091), or null; the handler opens new connections when it changes.</summary>
    public X509Certificate2? ClientCertificate => _client;

    public void Pin(string? thumbprint) => _pinned = thumbprint;

    public void Pin(X509Certificate2? ca, string? thumbprint) => (_ca, _pinned) = (ca, thumbprint);

    public void UseClientCertificate(X509Certificate2? certificate) => _client = certificate;

    /// <summary>The TLS check of every connection to the store server (SocketsHttpHandler.SslOptions).</summary>
    public bool Validate(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        var thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var accepted = !IsPinned // trust on first use
            || (_pinned is { } pin && string.Equals(thumbprint, pin, StringComparison.OrdinalIgnoreCase))
            || (_ca is { } ca && IssuedBy(certificate, ca));
        if (!accepted) return false;
        _seen = thumbprint;
        if (!IsPinned) _seenCertificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return true;
    }

    /// <summary>Whether the server certificate seen before pairing is <paramref name="thumbprint"/> (the one shown) and was issued by
    /// <paramref name="ca"/> (what the server answered when pairing).</summary>
    public bool SawIssuedBy(string thumbprint, X509Certificate2 ca) =>
        _seenCertificate is { } seen && string.Equals(seen.GetCertHashString(HashAlgorithmName.SHA256), thumbprint, StringComparison.OrdinalIgnoreCase)
        && IssuedBy(seen, ca);

    /// <summary>Issued by the store CA for a server (a register's own certificate from the same CA does not pass). The name is not checked:
    /// the CA is this store's alone, and the server's address on the LAN may change.</summary>
    public static bool IssuedBy(X509Certificate certificate, X509Certificate2 ca)
    {
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuth));
        return chain.Build(leaf);
    }

    /// <summary>What people compare: the first 64 bits of the SHA-256, "AB12 CD34 EF56 7890". Making another certificate with the same
    /// 64 bits takes ~2^64 tries, far beyond a 10-minute pairing window.</summary>
    public static string? Fingerprint(string? thumbprint) =>
        thumbprint is { Length: >= 16 } t ? string.Join(' ', Enumerable.Range(0, 4).Select(i => t.Substring(i * 4, 4).ToUpperInvariant())) : null;
}
