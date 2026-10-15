using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Toki.Host.Local;

/// <summary>The store server's TLS certificate: self-signed, created once in the machine store and reused until 30 days before it expires.
/// Registers trust it on first use: they pin the certificate they see when they pair with the one-time code shown on the server.</summary>
/// <remarks>shortcut: no renewal hand-off to paired registers yet; mutual TLS and re-pinning come with TK-091.</remarks>
internal static class CertificadoNodo
{
    public const string Sujeto = "CN=Toki Nodo";

    public static X509Certificate2 ObtenerOCrear(StoreLocation ubicacion, TimeProvider reloj, string sujeto = Sujeto)
    {
        using var store = new X509Store(StoreName.My, ubicacion);
        store.Open(OpenFlags.ReadWrite);
        var ahora = reloj.GetUtcNow();
        var vigente = store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, sujeto, validOnly: false)
            .Where(c => c.HasPrivateKey && c.NotAfter.ToUniversalTime() > ahora.UtcDateTime.AddDays(30))
            .OrderByDescending(c => c.NotAfter).FirstOrDefault();
        if (vigente is not null) return vigente;

        using var rsa = RSA.Create(2048);
        var solicitud = new CertificateRequest(sujeto, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var nombres = new SubjectAlternativeNameBuilder();
        nombres.AddDnsName("localhost");
        nombres.AddDnsName(Environment.MachineName);
        nombres.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()).Where(i => i.AddressFamily == AddressFamily.InterNetwork)) nombres.AddIpAddress(ip);
        solicitud.CertificateExtensions.Add(nombres.Build());
        solicitud.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        solicitud.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // server authentication

        using var temporal = solicitud.CreateSelfSigned(ahora.AddDays(-1), ahora.AddYears(5));
        // A certificate made in memory has an ephemeral key that Windows cannot store: reload it with a persisted key.
        var flags = X509KeyStorageFlags.PersistKeySet | (ubicacion == StoreLocation.LocalMachine ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.UserKeySet);
        var certificado = X509CertificateLoader.LoadPkcs12(temporal.Export(X509ContentType.Pfx), null, flags);
        store.Add(certificado);
        return certificado;
    }
}
