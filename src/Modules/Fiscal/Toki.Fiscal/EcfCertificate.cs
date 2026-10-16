using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Toki.Fiscal.Domain;
using Toki.SharedKernel;

namespace Toki.Fiscal;

/// <summary>The store's e-CF certificate (TK-142, import and expiry TK-176). Where it comes from, first match wins:
/// <list type="number">
/// <item>The one imported from Configuración → Comprobantes: <c>ecf-certificate.json</c> in the keys folder (<c>Toki:KeysFolder</c>) sets
/// <c>Fiscal:Certificate:Thumbprint</c> and <c>Fiscal:Certificate:Store</c> (<c>LocalMachine</c>, <c>CurrentUser</c> or <c>KeysFolder</c>).</item>
/// <item>The application settings: <c>Fiscal:Certificate:Thumbprint</c> (+ <c>Store</c>, LocalMachine by default) or, for development and tests,
/// <c>Fiscal:Certificate:Path</c> + <c>Password</c> (a .p12/.pfx file).</item>
/// </list>
/// The password is never stored. On the store PC (Production on Windows, or <c>Fiscal:Certificate:ImportTo</c> = <c>Store</c>) the key is imported
/// non-exportable into <c>LocalMachine\My</c>, or into the service account's <c>CurrentUser\My</c> when it may not write the machine store.
/// Anywhere else (<c>ImportTo</c> = <c>KeysFolder</c>: development, tests) it goes to the keys folder, encrypted with Data Protection.</summary>
/// <remarks>shortcut: the machine key is not restricted to the service account (docs/ecf-propio.md §10.3); the installer runs the service as
/// NT SERVICE\Toki, which normally cannot write LocalMachine\My, so a store PC ends up with CurrentUser\My until TK-160 grants it.</remarks>
internal sealed class EcfCertificate
{
    public const string FileName = "ecf-certificate.json";
    public const string ProtectedFileName = "ecf-certificate.pfx.protected";

    private readonly IConfiguration _config;
    private readonly IDataProtector _protector;
    private readonly string _folder;
    private readonly bool _toStore;
    private readonly Lock _lock = new();
    private (X509Certificate2? Certificate, string? Source)? _current;
    private readonly TimeProvider _clock;
    private DateTimeOffset _failedAt;

    /// <summary>R8 #20: a load that failed (thumbprint not in the store yet, file locked) is tried again after this long, not only after a restart.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);

    public EcfCertificate(IConfiguration config, IDataProtectionProvider protection, IHostEnvironment env, TimeProvider clock)
    {
        (_config, _clock) = (config, clock);
        _protector = protection.CreateProtector("Toki.Fiscal.EcfCertificate");
        // The same default as the Settings module's key ring.
        _folder = config["Toki:KeysFolder"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki", "keys");
        _toStore = config["Fiscal:Certificate:ImportTo"] is { } to ? to == "Store" : OperatingSystem.IsWindows() && env.IsProduction();
    }

    public X509Certificate2? Current => Loaded().Certificate;

    /// <summary>Why there is no certificate when one is configured (e.g. a thumbprint that is not in its store).</summary>
    public string? Problem { get; private set; }

    /// <summary>What the owner sees: holder, RNC or cédula, expiry and where it lives. Null without a certificate.</summary>
    public CertificateInfo? Describe() => Loaded() is { Certificate: { } c, Source: var source } ? CertificateInfo.Of(c, source!) : null;

    private (X509Certificate2? Certificate, string? Source) Loaded()
    {
        lock (_lock)
        {
            if (_current is { } current && (current.Certificate is not null || Problem is null || _clock.GetUtcNow() - _failedAt < RetryAfter)) return current;
            try { (_current, Problem) = (Load(), null); }
            // A missing or unreadable certificate must not break the screens that show it: they say there is none, and signing says why.
            catch (Exception e) when (e is InvalidOperationException or CryptographicException or IOException or FormatException)
            {
                (_current, Problem, _failedAt) = ((null, null), e.Message, _clock.GetUtcNow());
            }
            return _current.Value;
        }
    }

    private (X509Certificate2?, string?) Load()
    {
        var imported = new ConfigurationBuilder().AddJsonFile(Path.Combine(_folder, FileName), optional: true).Build();
        if (imported["Fiscal:Certificate:Store"] == "KeysFolder")
            return (X509CertificateLoader.LoadPkcs12(_protector.Unprotect(File.ReadAllBytes(Path.Combine(_folder, ProtectedFileName))), null,
                X509KeyStorageFlags.EphemeralKeySet), "KeysFolder");
        var source = imported["Fiscal:Certificate:Thumbprint"] is not null ? imported : _config;
        return (LoadConfigured(source), source["Fiscal:Certificate:Path"] is { Length: > 0 } ? "File" : source["Fiscal:Certificate:Store"] ?? "LocalMachine");
    }

    /// <summary>The certificate the settings name, or null when they name none. A thumbprint that is not in its store throws.</summary>
    public static X509Certificate2? LoadConfigured(IConfiguration config)
    {
        if (config["Fiscal:Certificate:Thumbprint"] is { Length: > 0 } thumbprint)
        {
            var location = Enum.Parse<StoreLocation>(config["Fiscal:Certificate:Store"] ?? nameof(StoreLocation.LocalMachine));
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint.Replace(" ", "", StringComparison.Ordinal), validOnly: false);
            return found.Count == 1 ? found[0] : throw new InvalidOperationException($"e-CF certificate {thumbprint} not found in {location}\\My.");
        }
        if (config["Fiscal:Certificate:Path"] is { Length: > 0 } path)
            return X509CertificateLoader.LoadPkcs12FromFile(path, config["Fiscal:Certificate:Password"], X509KeyStorageFlags.EphemeralKeySet);
        return null;
    }

    /// <summary>Opens the .p12/.pfx with its password, checks it can sign and has not expired, keeps it (see the class) and uses it from now on.</summary>
    /// <param name="businessRnc">R8 #4: a certificate that names another RNC (or another cédula, for a business that is a person) is refused: the
    /// DGII would reject everything signed with it. A cédula on the certificate of a company (9-digit RNC) is the delegated signer's and is accepted
    /// (docs/ecf-propio.md §11 #28).</param>
    public Result<CertificateInfo> Import(byte[] pfx, string? password, DateTime nowUtc, string? businessRnc)
    {
        X509Certificate2 opened;
        try { opened = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable); }
        catch (CryptographicException) { return FiscalErrors.CertificateUnreadable; }
        using (opened)
        {
            using (var key = opened.GetRSAPrivateKey())
                if (key is null) return FiscalErrors.CertificateWithoutKey;
            if (opened.NotAfter.ToUniversalTime() <= nowUtc) return FiscalErrors.CertificateExpired;
            if (opened.NotBefore.ToUniversalTime() > nowUtc) return FiscalErrors.CertificateNotYetValid;
            if (CertificateInfo.Of(opened, "").TaxId is { } taxId && businessRnc is { } rnc && taxId.Length == rnc.Length && taxId != rnc)
                return FiscalErrors.CertificateWrongRnc(taxId, rnc);

            Directory.CreateDirectory(_folder);
            var (certificate, source) = _toStore ? ToStore(pfx, password) : ToKeysFolder(opened);
            File.WriteAllText(Path.Combine(_folder, FileName), JsonSerializer.Serialize(new
            {
                Fiscal = new { Certificate = new { certificate.Thumbprint, Store = source } },
            }));
            lock (_lock) (_current, Problem) = ((certificate, source), null);
            return CertificateInfo.Of(certificate, source);
        }
    }

    private static (X509Certificate2, string) ToStore(byte[] pfx, string? password)
    {
        try { return (AddTo(StoreLocation.LocalMachine), nameof(StoreLocation.LocalMachine)); }
        // The service account may not write the machine store: its own store still survives restarts and needs no admin rights.
        catch (CryptographicException) { return (AddTo(StoreLocation.CurrentUser), nameof(StoreLocation.CurrentUser)); }
        catch (UnauthorizedAccessException) { return (AddTo(StoreLocation.CurrentUser), nameof(StoreLocation.CurrentUser)); }

        X509Certificate2 AddTo(StoreLocation location)
        {
            // Non-exportable: the key can sign but never leave this PC.
            var flags = X509KeyStorageFlags.PersistKeySet | (location == StoreLocation.LocalMachine ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.UserKeySet);
            var certificate = X509CertificateLoader.LoadPkcs12(pfx, password, flags);
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
            return certificate;
        }
    }

    private (X509Certificate2, string) ToKeysFolder(X509Certificate2 opened)
    {
        var bytes = opened.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(Path.Combine(_folder, ProtectedFileName), _protector.Protect(bytes));
        return (X509CertificateLoader.LoadPkcs12(bytes, null, X509KeyStorageFlags.EphemeralKeySet), "KeysFolder");
    }
}

/// <param name="TaxId">RNC or cédula of the holder, from the subject's SERIALNUMBER or organizationIdentifier, when it has one.</param>
/// <param name="Store">Where it lives: LocalMachine, CurrentUser, KeysFolder or File.</param>
internal sealed record CertificateInfo(string Subject, string? TaxId, string Issuer, DateTime NotBeforeUtc, DateTime NotAfterUtc, string Thumbprint, string Store)
{
    /// <summary>The owner is warned this many days before it expires: renewing with the certification authority takes days.</summary>
    public const int WarningDays = 30;

    public static CertificateInfo Of(X509Certificate2 c, string store) => new(c.GetNameInfo(X509NameType.SimpleName, false), TaxIdOf(c.SubjectName),
        c.GetNameInfo(X509NameType.SimpleName, true), c.NotBefore.ToUniversalTime(), c.NotAfter.ToUniversalTime(), c.Thumbprint, store);

    private static string? TaxIdOf(X500DistinguishedName name) => name.EnumerateRelativeDistinguishedNames()
        .Where(n => n.GetSingleElementType().Value is "2.5.4.5" or "2.5.4.97")
        .Select(n => new string([.. (n.GetSingleElementValue() ?? "").Where(char.IsAsciiDigit)]))
        .FirstOrDefault(d => d.Length is 9 or 11);
}
