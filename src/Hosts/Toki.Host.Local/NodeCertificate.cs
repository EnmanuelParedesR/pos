using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Toki.Host.Local;

/// <summary>The TLS certificate of stores installed before TK-091: self-signed, in the machine store. Used only in dual mode
/// (<c>Toki:LegacyDeviceSecrets</c>, ADR-0009), so registers that pinned it keep working until they are paired again.
/// New installs use a certificate issued by the store CA (<see cref="Toki.Identity.StoreCertificateAuthority"/>).</summary>
internal static class NodeCertificate
{
    public const string Subject = "CN=Toki Nodo";

    /// <remarks>Reading needs no admin rights, so the service account can start with a certificate the (elevated) installer created;
    /// creating one needs write access to the store, which only the installer or LocalSystem has.</remarks>
    public static X509Certificate2 GetOrCreate(StoreLocation location, TimeProvider clock, string subject = Subject)
    {
        var now = clock.GetUtcNow();
        using (var reader = new X509Store(StoreName.My, location))
        {
            reader.Open(OpenFlags.ReadOnly);
            var current = reader.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, subject, validOnly: false)
                .Where(c => c.HasPrivateKey && c.NotAfter.ToUniversalTime() > now.UtcDateTime.AddDays(30))
                .OrderByDescending(c => c.NotAfter).FirstOrDefault();
            if (current is not null) return current;
        }
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadWrite);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(Environment.MachineName);
        names.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()).Where(i => i.AddressFamily == AddressFamily.InterNetwork)) names.AddIpAddress(ip);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // server authentication

        using var temporary = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
        // A certificate made in memory has an ephemeral key that Windows cannot store: reload it with a persisted key.
        var flags = X509KeyStorageFlags.PersistKeySet | (location == StoreLocation.LocalMachine ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.UserKeySet);
        var certificate = X509CertificateLoader.LoadPkcs12(temporary.Export(X509ContentType.Pfx), null, flags);
        store.Add(certificate);
        return certificate;
    }
}
