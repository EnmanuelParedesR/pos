using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Abastalia.Client.UI.State;

namespace Abastalia.Client.Wpf;

/// <summary>R11 C5: this PC's client-certificate key as a named, non-exportable CNG key of the Windows user: in the TPM (Microsoft Platform Crypto
/// Provider) when there is one, else in the software provider. client.dat only keeps "provider|name": copying it (or the profile) to another
/// PC gives nothing that signs.</summary>
internal sealed class CngDeviceKeys : IDeviceKeys
{
    private const string Tpm = "Microsoft Platform Crypto Provider";

    public (ECDsa Key, string Reference) Create()
    {
        var name = $"Abastalia-caja-{Guid.NewGuid():N}";
        foreach (var provider in new[] { Tpm, CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider })
        {
            try
            {
                using var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters { Provider = new CngProvider(provider) });
                return (new ECDsaCng(key), $"{provider}|{name}");
            }
            catch (CryptographicException) when (provider == Tpm) { } // no TPM, or it is busy or off: software
        }
        throw new UnreachableException();
    }

    public X509Certificate2 Load(string certificatePem, string reference)
    {
        using var key = Open(reference);
        using var ecdsa = new ECDsaCng(key);
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        return certificate.CopyWithPrivateKey(ecdsa); // bound to the persisted key: Windows' TLS opens it by name
    }

    public void Delete(string reference)
    {
        try { Open(reference).Delete(); }
        catch (CryptographicException) { } // already gone
    }

    private static CngKey Open(string reference)
    {
        var parts = reference.Split('|');
        if (parts.Length != 2) throw new CryptographicException("Not a key reference.");
        return CngKey.Open(parts[1], new CngProvider(parts[0]));
    }
}
