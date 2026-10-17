using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Host.Cloud.Quotas;

namespace Abastalia.Host.Cloud.Billing;

/// <summary>Signs the SHA-256 of a license (ES256, answer r‖s). TK-249: the cloud's own license key, a second one the store trusts besides the
/// owner's offline key (docs/licencias.md).</summary>
public interface ILicenseSigner
{
    Task<byte[]> SignSha256Async(byte[] digest, CancellationToken ct);
}

/// <summary><c>Billing:License:KeyId</c>: an EC P-256 key in Key Vault, created non-exportable; the private half never leaves the vault and the API's
/// managed identity may only sign with it ("Key Vault Crypto User", deploy/azure/main.bicep).</summary>
internal sealed class KeyVaultLicenseSigner(CryptographyClient key) : ILicenseSigner
{
    public async Task<byte[]> SignSha256Async(byte[] digest, CancellationToken ct) => (await key.SignAsync(SignatureAlgorithm.ES256, digest, ct)).Signature;
}

/// <summary>Development and tests only: <c>Billing:License:PrivateKey</c> (PEM). Never in Production (<see cref="CloudLicenses.AddCloudLicenses"/>).</summary>
internal sealed class LocalLicenseSigner(ECDsa key) : ILicenseSigner
{
    public Task<byte[]> SignSha256Async(byte[] digest, CancellationToken ct) => Task.FromResult(key.SignHash(digest));
}

/// <summary>The store's license from its subscription: plan, add-ons and paid-through date, signed by the cloud key. A store connected to the
/// cloud pulls it (POST /api/nodes/license) and installs it when it is newer (TK-249). Offline issuance keeps working beside it.</summary>
/// <remarks>Expiry = paid until (Paddle or the staff override) + <c>Billing:License:MarginDays</c> (3: the renewal webhook and the store's next
/// pull have time to arrive), then the store's own 15 days of grace, then Restricted, then Blocked <c>Billing:License:BlockAfterDays</c> (30, signed
/// into the license) after it expired (TK-279).</remarks>
internal sealed class CloudLicenses(CloudDbContext db, IConfiguration config, BillingCatalog catalog, TimeProvider clock, ILicenseSigner? signer = null)
{
    public bool IsConfigured => signer is not null;

    public static void AddCloudLicenses(IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        services.AddScoped<CloudLicenses>();
        if (config["Billing:License:KeyId"] is { Length: > 0 } keyId)
            services.AddSingleton<ILicenseSigner>(new KeyVaultLicenseSigner(new CryptographyClient(new Uri(keyId), new DefaultAzureCredential())));
        else if (!environment.IsProduction() && config["Billing:License:PrivateKey"] is { Length: > 0 } pem)
        {
            var key = ECDsa.Create();
            key.ImportFromPem(pem);
            services.AddSingleton<ILicenseSigner>(new LocalLicenseSigner(key));
        }
    }

    /// <summary>The signed license for the node's store, or null when the business has no paid time left or no key is set up.
    /// TK-279: a business Abastalia blocked gets, for every store, a license that says so (it replaces any older one).</summary>
    public async Task<string?> IssueAsync(Guid tenantId, Guid storeId, CancellationToken ct)
    {
        if (signer is null) return null;
        var subscription = await db.TenantSubscriptions.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (subscription is null) return null;
        var blocked = subscription.BlockedUtc is { } since ? DateTime.SpecifyKind(since, DateTimeKind.Utc) : (DateTime?)null;
        var paidUntil = subscription.PaidUntilUtc is { } paid ? DateTime.SpecifyKind(paid, DateTimeKind.Utc) : (DateTime?)null;
        var expires = blocked ?? paidUntil?.AddDays(config.GetValue("Billing:License:MarginDays", 3));
        // Nothing paid ahead: an expired license would only replace a valid offline one.
        if (expires is null || (blocked is null && expires <= now)) return null;

        var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId, ct);
        var (plan, addOns) = (string.IsNullOrEmpty(tenant.Plan) ? Quota.DefaultPlan : tenant.Plan, Quota.SplitAddOns(tenant.AddOns));
        // TK-258: the plan's stores plus one per "Sucursal adicional" get a license, the oldest first; the rest keep their own (offline or trial).
        var licensed = await db.Stores.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.CreatedUtc).ThenBy(s => s.Id)
            .Take(Count(config, catalog, plan, addOns, nameof(AbastaliaProduct.Stores), t => t.Stores) ?? 1).Select(s => s.Id).ToListAsync(ct);
        if (blocked is null && !licensed.Contains(storeId)) return null;
        var store = await db.Stores.AsNoTracking().Where(s => s.Id == storeId).Select(s => new { s.Name, s.Rnc, s.PreviousRnc }).SingleOrDefaultAsync(ct);
        // TK-308: the store refuses a license for another RNC; one that has not sent its RNC yet gets none (accepted until it expires).
        var data = Data(config, catalog, store?.Name ?? tenant.Name, plan, addOns, Truncate(subscription.UpdatedUtc), expires.Value) with
        {
            TenantId = tenantId, Blocked = blocked is not null, BlockAfterDays = config.GetValue<int?>("Billing:License:BlockAfterDays"), Rnc = store?.Rnc,
            ReplacesRnc = store?.PreviousRnc, // TK-368: support moved the RNC; the store with the old one takes the new one
        };
        return await SignAsync(data, Jws.LicenseType, ct);
    }

    /// <summary>R12 #7 (TK-308): the signed <see cref="TrialGrant"/> of a referred business (the store ignores a bare number); null without a key.</summary>
    public async Task<string?> TrialAsync(Guid tenantId, int days, CancellationToken ct) =>
        signer is null ? null : await SignAsync(new TrialGrant(tenantId, days, clock.GetUtcNow().UtcDateTime), Jws.TrialType, ct);

    public const int MaxNonce = 64;

    /// <summary>R12 #3 (TK-308): the cloud's signed time for the store's <paramref name="nonce"/>; null without a key or a nonce.</summary>
    public async Task<string?> TimeAsync(Guid tenantId, string? nonce, CancellationToken ct) =>
        signer is null || nonce is not { Length: > 0 and <= MaxNonce } ? null
            : await SignAsync(new ServerTime(tenantId, nonce, clock.GetUtcNow().UtcDateTime), Jws.TimeType, ct);

    private async Task<string> SignAsync<T>(T data, string type, CancellationToken ct)
    {
        var input = Jws.SigningInput(data, type);
        return Jws.Compose(input, await signer!.SignSha256Async(SHA256.HashData(Encoding.ASCII.GetBytes(input)), ct));
    }

    /// <summary>TK-308: an RNC as the cloud keeps it: its digits when there are 9 (company) or 11 (cédula); anything else is none.</summary>
    public static string? Rnc(string? rnc) => rnc is null ? null : new string([.. rnc.Where(char.IsAsciiDigit)]) is { Length: 9 or 11 } digits ? digits : null;

    /// <summary>What the license says of a plan: the Paddle product's <c>custom_data.abastalia</c> (<c>registers</c>, <c>features</c>) &gt;
    /// <c>Billing:Plans:&lt;Plan&gt;</c> &gt; Default's (Paddle, then appsettings). An add-on named like a <see cref="Feature"/>
    /// (SupplierInvoiceReading) switches that function on.</summary>
    public static LicenseData Data(IConfiguration config, BillingCatalog catalog, string store, string plan, IEnumerable<string> addOns, DateTime issuedUtc, DateTime expiresUtc)
    {
        var features = Features(config, catalog, plan, addOns);
        var limits = new Dictionary<string, int>();
        if (Count(config, catalog, plan, addOns, nameof(Limit.Registers), t => t.Registers) is { } registers) limits[nameof(Limit.Registers)] = registers;
        // TK-277: the plan's cashiers plus each add-on's (ExtraCashier 1, ExtraRegister 2, ExtraStore 4); none = the store does not limit them.
        if (Count(config, catalog, plan, addOns, nameof(Limit.Cashiers), t => t.Cashiers) is { } cashiers) limits[nameof(Limit.Cashiers)] = cashiers;
        return new LicenseData(store, config[$"Billing:Plans:{plan}:Name"] ?? catalog.Of(plan)?.Name ?? plan, features, limits, issuedUtc, expiresUtc);
    }

    /// <summary>The license's functions for a plan and its add-ons (see <see cref="Data"/>). TK-338: the cloud reads AssistantActions here too.</summary>
    public static List<string> Features(IConfiguration config, BillingCatalog catalog, string plan, IEnumerable<string> addOns)
    {
        string[]? Config(string p) => config.GetSection($"Billing:Plans:{p}:Features").Get<string[]>();
        return [.. (catalog.Of(plan)?.Abastalia.Features?.ToArray() ?? Config(plan) ?? catalog.Of(Quota.DefaultPlan)?.Abastalia.Features?.ToArray()
                ?? Config(Quota.DefaultPlan) ?? [])
            .Concat(addOns.Where(a => Enum.TryParse<Feature>(a, out _))).Distinct()];
    }

    /// <summary>Registers or stores: the plan's (Paddle &gt; <c>Billing:Plans:&lt;Plan&gt;</c> &gt; Default's), plus each add-on unit's (Paddle &gt;
    /// <c>Billing:AddOns:&lt;AddOn&gt;</c>), TK-258. Null when no plan says.</summary>
    private static int? Count(IConfiguration config, BillingCatalog catalog, string plan, IEnumerable<string> addOns, string key, Func<AbastaliaProduct, int?> of)
    {
        int? Own(string section, string code) => (catalog.Of(code)?.Abastalia is { } t ? of(t) : null) ?? config.GetValue<int?>($"Billing:{section}:{code}:{key}");
        return (Own("Plans", plan) ?? Own("Plans", Quota.DefaultPlan)) is { } n ? n + addOns.Sum(a => Own(Quota.AddOnsSection, a) ?? 0) : null;
    }

    private static DateTime Truncate(DateTime utc) => DateTime.SpecifyKind(new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}
