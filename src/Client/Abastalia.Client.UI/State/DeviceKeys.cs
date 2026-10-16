using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Abastalia.Client.UI.State;

/// <summary>Where this PC's client-certificate key lives (TK-091). The WPF register keeps it non-exportable in Windows (TPM when it can, R11 C5);
/// the local store only keeps a reference to it.</summary>
public interface IDeviceKeys
{
    /// <summary>A new P-256 key pair, and what to keep in the local store to find it again.</summary>
    (ECDsa Key, string Reference) Create();

    /// <summary>The certificate with the key the reference names, usable by Windows' TLS.</summary>
    X509Certificate2 Load(string certificatePem, string reference);

    void Delete(string reference);
}

/// <summary>The dev web host and tests: the key itself (PKCS#8) is the reference, kept in the local store.</summary>
public sealed class ExportableDeviceKeys : IDeviceKeys
{
    public (ECDsa Key, string Reference) Create()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
    }

    public X509Certificate2 Load(string certificatePem, string reference)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(reference), out _);
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        using var withKey = certificate.CopyWithPrivateKey(key);
        // Windows' TLS needs a key it can load, not an in-memory one.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    public void Delete(string reference) { }
}
