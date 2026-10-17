using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Identity;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Lite;

/// <summary>TK-320 (ADR-0013): a business of the Lite host. Its id is the cloud tenant's, so moving up to Esencial keeps the panel's history.</summary>
internal sealed class Tenant : ISharedAcrossTenants
{
    public Guid Id { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>TK-320: which business a device belongs to, by the hash of its secret: the one table read across businesses, before the request
/// knows its own. It holds no business data. The column is not called TenantId on purpose: that name puts a table under Row-Level Security.</summary>
internal sealed class TenantDevice : ISharedAcrossTenants
{
    public byte[] SecretHash { get; set; } = [];
    public Guid BusinessId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime CreatedUtc { get; set; }

    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
}

internal sealed class LiteDbContext(DbContextOptions<LiteDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "lite";
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantDevice> Devices => Set<TenantDevice>();
}

internal sealed class LiteConfiguration : IEntityTypeConfiguration<Tenant>, IEntityTypeConfiguration<TenantDevice>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("Tenants");
        b.Property(t => t.Id).ValueGeneratedNever();
    }

    public void Configure(EntityTypeBuilder<TenantDevice> b)
    {
        b.ToTable("TenantDevices");
        b.HasKey(d => d.SecretHash);
        b.Property(d => d.SecretHash).HasMaxLength(32);
        b.HasIndex(d => d.BusinessId);
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of the Lite host's own tables.</summary>
internal sealed class LiteDesignTimeFactory : DesignTimeFactory<LiteDbContext>;

/// <summary>Every business, for the workers (outbox, upload, heartbeat and license).</summary>
internal sealed class LiteTenants(LiteDbContext db) : ITenantDirectory
{
    public async Task<IReadOnlyList<Guid>> AllAsync(CancellationToken ct) => await db.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(ct);
}

/// <summary>TK-320: the request's business comes from its device, never from a header that names it. The device's secret is looked up in
/// <see cref="TenantDevice"/> (a scope of its own, so the request's connection is opened only once the tenant is set); then Identity
/// authenticates the device and the session as always, inside that business (a revoked device or a session of another business fails there).
/// Without a known device no <c>/api</c> route answers: what a store does anonymously before pairing (setup, pairing codes) is not for Lite.</summary>
/// <remarks>Why a lookup table and not <c>IgnoreQueryFilters</c> on <c>identity.Devices</c>: with Row-Level Security a connection that has not said
/// its business sees no row at all, and a bypass in the predicate would open every table. TK-321 replaces the secret with a signed request.</remarks>
internal static class DeviceTenant
{
    public static readonly Error Unknown = Error.Unauthorized("Lite.UnknownDevice", "Este equipo no está unido a ningún negocio.");

    public static async Task ResolveAsync(HttpContext http, RequestDelegate next)
    {
        var tenant = http.RequestServices.GetRequiredService<ScopedTenant>();
        var secret = http.Request.Headers[IdentityModule.DeviceHeader].ToString();
        if (secret.Length > 0)
        {
            var hash = TenantDevice.Hash(secret);
            await using var scope = http.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<LiteDbContext>().Devices.AsNoTracking().Where(d => d.SecretHash == hash)
                    .Select(d => (Guid?)d.BusinessId).FirstOrDefaultAsync(http.RequestAborted) is { } business)
                tenant.TenantId = business;
        }
        if (tenant.TenantId == Guid.Empty && http.Request.Path.StartsWithSegments("/api"))
        {
            await EndpointExtensions.Problem(Unknown).ExecuteAsync(http);
            return;
        }
        await next(http);
    }
}
