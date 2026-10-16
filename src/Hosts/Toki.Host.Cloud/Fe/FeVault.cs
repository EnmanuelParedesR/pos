using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace Toki.Host.Cloud.Fe;

/// <summary>Where the cloud keeps the stores' e-CF certificates (TK-151). The key is imported non-exportable and never leaves the vault:
/// signing hands it a SHA-256 digest.</summary>
public interface IFeCertificateVault
{
    /// <summary>Imports the .p12 under <paramref name="name"/>; returns the public certificate.</summary>
    Task<X509Certificate2> ImportAsync(string name, byte[] pfx, string? password, CancellationToken ct);
    /// <summary>Withdrawn consent: the key stops working now (Key Vault keeps it soft-deleted, inaccessible, for its retention period).</summary>
    Task DeleteAsync(string name, CancellationToken ct);
    /// <summary>The public certificate and an <see cref="RSA"/> that signs with the vault key. Fails when the key was deleted.</summary>
    Task<(X509Certificate2 Certificate, RSA Key)> KeyAsync(string name, CancellationToken ct);
}

/// <summary><c>Fe:Vault</c> = <c>KeyVault</c> (Production; <c>Fe:KeyVaultUri</c>, the app's managed identity with "Key Vault Certificates Officer"
/// and "Key Vault Crypto User", see deploy/azure/main.bicep) or <c>Local</c> (development and tests only).</summary>
internal sealed class KeyVaultFeVault(CertificateClient certificates, TokenCredential credential) : IFeCertificateVault
{
    public async Task<X509Certificate2> ImportAsync(string name, byte[] pfx, string? password, CancellationToken ct)
    {
        using var opened = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
        var options = new ImportCertificateOptions(name, pfx)
        {
            Password = password,
            Policy = new CertificatePolicy(WellKnownIssuerNames.Unknown, opened.Subject)
            {
                Exportable = false, KeyType = CertificateKeyType.Rsa, ContentType = CertificateContentType.Pkcs12,
            },
        };
        var imported = await certificates.ImportCertificateAsync(options, ct);
        return X509CertificateLoader.LoadCertificate(imported.Value.Cer);
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        try { await certificates.StartDeleteCertificateAsync(name, ct); }
        catch (RequestFailedException e) when (e.Status == 404) { } // already gone
    }

    public async Task<(X509Certificate2 Certificate, RSA Key)> KeyAsync(string name, CancellationToken ct)
    {
        var stored = (await certificates.GetCertificateAsync(name, ct)).Value;
        var certificate = X509CertificateLoader.LoadCertificate(stored.Cer);
        return (certificate, new KeyVaultRsa(new CryptographyClient(stored.KeyId, credential), certificate.GetRSAPublicKey()!));
    }
}

/// <summary>An <see cref="RSA"/> whose private operation is a Key Vault <c>sign</c> (RS256), so <c>SignedXml</c> signs with a key it never sees.</summary>
internal sealed class KeyVaultRsa : RSA
{
    private readonly CryptographyClient _crypto;
    private readonly RSA _public;

    public KeyVaultRsa(CryptographyClient crypto, RSA publicKey)
    {
        (_crypto, _public) = (crypto, publicKey);
        KeySizeValue = publicKey.KeySize;
        LegalKeySizesValue = [new KeySizes(publicKey.KeySize, publicKey.KeySize, 0)];
    }

    public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding)
    {
        if (hashAlgorithm != HashAlgorithmName.SHA256 || padding != RSASignaturePadding.Pkcs1)
            throw new NotSupportedException("The e-CF signature is RSA-SHA256 with PKCS#1 padding.");
        return _crypto.Sign(Azure.Security.KeyVault.Keys.Cryptography.SignatureAlgorithm.RS256, hash).Signature;
    }

    public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
        _public.VerifyHash(hash, signature, hashAlgorithm, padding);

    public override RSAParameters ExportParameters(bool includePrivateParameters) =>
        includePrivateParameters ? throw new CryptographicException("The key is not exportable.") : _public.ExportParameters(false);

    public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _public.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Development and tests: the certificate in memory, lost on restart. Never in Production (see <see cref="FeServices"/>).</summary>
internal sealed class LocalFeVault : IFeCertificateVault
{
    private readonly ConcurrentDictionary<string, X509Certificate2> _certificates = new();

    public Task<X509Certificate2> ImportAsync(string name, byte[] pfx, string? password, CancellationToken ct)
    {
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet);
        _certificates[name] = certificate;
        return Task.FromResult(X509CertificateLoader.LoadCertificate(certificate.RawData));
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        if (_certificates.TryRemove(name, out var removed)) removed.Dispose();
        return Task.CompletedTask;
    }

    public Task<(X509Certificate2 Certificate, RSA Key)> KeyAsync(string name, CancellationToken ct) =>
        _certificates.TryGetValue(name, out var c)
            ? Task.FromResult((X509CertificateLoader.LoadCertificate(c.RawData), c.GetRSAPrivateKey()!))
            : throw new InvalidOperationException($"No certificate '{name}' in the local vault.");
}
