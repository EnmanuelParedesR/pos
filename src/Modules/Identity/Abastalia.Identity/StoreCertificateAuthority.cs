using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace Abastalia.Identity;

/// <summary>TK-091 (ADR-0009): the store's own CA. Created on first use in the keys folder (<c>Abastalia:KeysFolder</c>): the certificate in
/// <c>store-ca.pem</c>, its private key encrypted with Data Protection (DPAPI of the machine on Windows) in <c>store-ca.key</c>.
/// It signs the server's TLS certificate and each register's client certificate; registers pin this CA, so the server can renew its own
/// certificate without pairing them again.</summary>
public sealed class StoreCertificateAuthority : IDisposable
{
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuth = "1.3.6.1.5.5.7.3.2";
    /// <summary>R11 C5: one year; the register renews it over mTLS in its last 30 days (<c>/api/identity/devices/current/certificate</c>).</summary>
    public static readonly TimeSpan DeviceValidity = TimeSpan.FromDays(365);
    public static readonly TimeSpan ServerValidity = TimeSpan.FromDays(365);

    private readonly string _folder;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly Lazy<(X509Certificate2 Certificate, ECDsa Key)> _ca;
    private readonly Lock _lock = new();
    private X509Certificate2? _server;
    private readonly List<X509Certificate2> _loaded = [];

    public StoreCertificateAuthority(IConfiguration config, IDataProtectionProvider protection, TimeProvider clock)
    {
        // The same default as the Settings module's key ring.
        _folder = config["Abastalia:KeysFolder"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Abastalia", "keys");
        _protector = protection.CreateProtector("Abastalia.Identity.StoreCa");
        _clock = clock;
        _ca = new(LoadOrCreate);
    }

    /// <summary>The CA certificate (public part), PEM: what a register pins when it pairs.</summary>
    public string CertificatePem => _ca.Value.Certificate.ExportCertificatePem();

    /// <summary>The server's TLS certificate, issued by the CA and renewed 30 days before it expires (a register that pinned the CA keeps working).
    /// R9 #15: kept in <c>store-server.pfx</c> (protected like the CA key), so a restart shows the same one and its fingerprint stays.</summary>
    public X509Certificate2 ServerCertificate
    {
        get
        {
            lock (_lock)
            {
                _server ??= LoadServerCertificate();
                return _server is not null && _server.NotAfter.ToUniversalTime() > _clock.GetUtcNow().UtcDateTime.AddDays(30) ? _server : IssueServerCertificate();
            }
        }
    }

    private string ServerFile => Path.Combine(_folder, "store-server.pfx");

    private X509Certificate2? LoadServerCertificate()
    {
        var ca = _ca.Value.Certificate; // first: replacing an unreadable CA deletes the server certificate it signed
        if (!File.Exists(ServerFile)) return null;
        try
        {
            var certificate = Track(X509CertificateLoader.LoadPkcs12(_protector.Unprotect(File.ReadAllBytes(ServerFile)), null));
            return IssuedBy(certificate, ca, ServerAuth) ? certificate : null;
        }
        catch (CryptographicException)
        {
            return null; // made again
        }
    }

    /// <summary>On Windows a certificate loaded from a PFX keeps its key in a file until it is disposed: the host disposes this singleton when it stops.</summary>
    private X509Certificate2 Track(X509Certificate2 certificate)
    {
        _loaded.Add(certificate);
        return certificate;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var c in _loaded) c.Dispose();
            _loaded.Clear();
        }
    }

    /// <summary>Signs a register's key (the CSR it sent with the pairing code). Only its public key is used: the subject and usage are
    /// set here, bound to the device id.</summary>
    /// <remarks>Check the CSR with <see cref="IsValidRequest"/> first (the validator does): an invalid one throws here.</remarks>
    public (string Pem, string Thumbprint) IssueDeviceCertificate(string csrPem, Guid deviceId)
    {
        var csr = ReadRequest(csrPem) ?? throw new ArgumentException("Invalid certificate request.", nameof(csrPem));
        var request = new CertificateRequest(new X500DistinguishedName($"CN=Abastalia Caja {deviceId}"), csr.PublicKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ClientAuth)], critical: false));
        using var certificate = Sign(request, DeviceValidity);
        return (certificate.ExportCertificatePem(), Thumbprint(certificate));
    }

    /// <summary>A PEM CSR whose signature matches its own key (the register holds the private key).</summary>
    public static bool IsValidRequest(string csrPem) => ReadRequest(csrPem) is not null;

    private static CertificateRequest? ReadRequest(string pem)
    {
        try { return CertificateRequest.LoadSigningRequestPem(pem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default); }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return null; }
    }

    /// <summary>Kestrel's check of a client certificate: issued by this CA for client authentication. Which device it is, and whether it
    /// was revoked, is decided per request (the certificate's thumbprint against the devices table).</summary>
    public bool IsDeviceCertificate(X509Certificate2 certificate) => IssuedBy(certificate, _ca.Value.Certificate, ClientAuth);

    /// <summary>SHA-256 of the whole certificate, upper-case hex: how a device is found from its client certificate.</summary>
    public static string Thumbprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    internal static bool IssuedBy(X509Certificate2 certificate, X509Certificate2 ca, string usage)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // revocation is the devices table
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(usage));
        return chain.Build(certificate);
    }

    /// <summary>A new server certificate now (renewal); <see cref="ServerCertificate"/> calls it when the current one is about to expire.</summary>
    internal X509Certificate2 IssueServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Abastalia Nodo", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(Environment.MachineName);
        names.AddIpAddress(IPAddress.Loopback);
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()).Where(i => i.AddressFamily == AddressFamily.InterNetwork)) names.AddIpAddress(ip);
        }
        catch (SocketException) { } // a machine whose own name does not resolve (a CI container): registers do not check the name anyway
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(ServerAuth)], critical: false));
        using var issued = Sign(request, ServerValidity);
        using var withKey = issued.CopyWithPrivateKey(key);
        // Windows' TLS needs a key it can load, not the ephemeral one made in memory.
        var pfx = withKey.Export(X509ContentType.Pfx);
        lock (_lock)
        {
            File.WriteAllBytes(ServerFile, _protector.Protect(pfx));
            return _server = Track(X509CertificateLoader.LoadPkcs12(pfx, null));
        }
    }

    private X509Certificate2 Sign(CertificateRequest request, TimeSpan validity)
    {
        var (ca, key) = _ca.Value;
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        var now = _clock.GetUtcNow();
        var notAfter = now + validity < ca.NotAfter ? now + validity : new DateTimeOffset(ca.NotAfter);
        return request.Create(ca.SubjectName, X509SignatureGenerator.CreateForECDsa(key), now.AddDays(-1), notAfter, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>When this CA was made (its certificate starts a day earlier, for clocks a bit behind). A register paired before it holds a
    /// certificate of an older CA (one that could not be read and was replaced) and has to be paired again.</summary>
    public DateTime CreatedUtc => _ca.Value.Certificate.NotBefore.ToUniversalTime().AddDays(1);

    private (X509Certificate2, ECDsa) LoadOrCreate()
    {
        var certificateFile = Path.Combine(_folder, "store-ca.pem");
        var keyFile = Path.Combine(_folder, "store-ca.key");
        var key = ECDsa.Create();
        if (File.Exists(certificateFile) && File.Exists(keyFile))
        {
            try
            {
                key.ImportPkcs8PrivateKey(_protector.Unprotect(File.ReadAllBytes(keyFile)), out _);
                return (X509Certificate2.CreateFromPem(File.ReadAllText(certificateFile)), key);
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException)
            {
                // R9 #8: a key ring that cannot open it (the keys folder copied to a new PC, another account) or a damaged file must not stop
                // the store: the old CA is kept aside and a new one made; the health check asks to pair the registers again.
                var aside = $".unreadable-{_clock.GetUtcNow():yyyyMMddHHmmss}";
                File.Move(keyFile, keyFile + aside);
                File.Move(certificateFile, certificateFile + aside);
                File.Delete(ServerFile);
                key.Dispose();
                key = ECDsa.Create();
            }
        }

        key.GenerateKey(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=Abastalia Tienda CA {Guid.NewGuid():N}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        var now = _clock.GetUtcNow();
        using var ca = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(30));
        Directory.CreateDirectory(_folder);
        // The key first: a CA certificate without its key would be useless, a key without the certificate is simply made again.
        File.WriteAllBytes(keyFile, _protector.Protect(key.ExportPkcs8PrivateKey()));
        File.WriteAllText(certificateFile, ca.ExportCertificatePem());
        return (X509CertificateLoader.LoadCertificate(ca.RawData), key);
    }
}
