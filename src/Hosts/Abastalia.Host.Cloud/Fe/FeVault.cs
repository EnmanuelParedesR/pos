using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace Abastalia.Host.Cloud.Fe;

/// <summary>A tenant's e-CF certificate (public part, shared: do not dispose) and the RS256 signature of a SHA-256 digest with its key.</summary>
public sealed record FeKey(X509Certificate2 Certificate, Func<byte[], CancellationToken, Task<byte[]>> SignSha256Async);

/// <summary>Where the cloud keeps the stores' e-CF certificates (TK-151). The key is imported non-exportable and never leaves the vault:
/// signing hands it a SHA-256 digest.</summary>
public interface IFeCertificateVault
{
    /// <summary>Imports the .p12 under <paramref name="name"/>; returns the public certificate.</summary>
    Task<X509Certificate2> ImportAsync(string name, byte[] pfx, string? password, CancellationToken ct);
    /// <summary>Withdrawn consent: the key stops working now (Key Vault keeps it soft-deleted, inaccessible, for its retention period).</summary>
    Task DeleteAsync(string name, CancellationToken ct);
    /// <summary>The certificate and its signing key. Fails when the key was deleted.</summary>
    Task<FeKey> KeyAsync(string name, CancellationToken ct);
}

/// <summary><c>Fe:Vault</c> = <c>KeyVault</c> (Production; <c>Fe:KeyVaultUri</c>, the app's managed identity with "Key Vault Certificates Officer"
/// and "Key Vault Crypto User", see deploy/azure/main.bicep) or <c>Local</c> (development and tests only).</summary>
/// <remarks>R9 #2: the certificate and its <see cref="CryptographyClient"/> are kept per key name, so a signature is one vault call (the sign).
/// A name belongs to one consent and is never reused; a withdrawn consent is refused by <see cref="FeSigner"/> before the key is asked for.</remarks>
internal sealed class KeyVaultFeVault(CertificateClient certificates, TokenCredential credential) : IFeCertificateVault
{
    private readonly ConcurrentDictionary<string, FeKey> _keys = new();

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
        _keys.TryRemove(name, out _);
        try { await certificates.StartDeleteCertificateAsync(name, ct); }
        catch (RequestFailedException e) when (e.Status == 404) { } // already gone
    }

    public async Task<FeKey> KeyAsync(string name, CancellationToken ct)
    {
        if (_keys.TryGetValue(name, out var cached)) return cached;
        var stored = (await certificates.GetCertificateAsync(name, ct)).Value;
        var crypto = new CryptographyClient(stored.KeyId, credential);
        return _keys.GetOrAdd(name, new FeKey(X509CertificateLoader.LoadCertificate(stored.Cer),
            async (hash, token) => (await crypto.SignAsync(SignatureAlgorithm.RS256, hash, token)).Signature));
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

    public Task<FeKey> KeyAsync(string name, CancellationToken ct) =>
        _certificates.TryGetValue(name, out var c)
            ? Task.FromResult(new FeKey(X509CertificateLoader.LoadCertificate(c.RawData), (hash, _) =>
            {
                using var rsa = c.GetRSAPrivateKey()!;
                return Task.FromResult(rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }))
            : throw new InvalidOperationException($"No certificate '{name}' in the local vault.");
}
