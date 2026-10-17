using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Identity;
using Abastalia.Identity.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Lite;

/// <summary>TK-320 (ADR-0013): a business of the Lite host. Its id is the cloud tenant's, so moving up to Esencial keeps the panel's history.</summary>
internal sealed class Tenant : ISharedAcrossTenants
{
    public Guid Id { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>TK-320/TK-321: a paired selling device: which business it belongs to and the public key its requests are signed with. Read across
/// businesses, before the request knows its own; it holds no business data. The column is not called TenantId on purpose: that name puts a table
/// under Row-Level Security. Kept in step with Identity's own device row: both are written in one transaction on pairing and on revoking.</summary>
internal sealed class TenantDevice : ISharedAcrossTenants
{
    public Guid DeviceId { get; set; }
    public Guid BusinessId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>ECDSA P-256 SubjectPublicKeyInfo. The private key never leaves the browser (non-extractable WebCrypto key).</summary>
    public byte[] PublicKey { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
}

/// <summary>TK-321: a code the owner got in the panel to pair a selling device: 8 digits, 10 minutes, single use, one at a time per business.
/// Only its SHA-256 is kept.</summary>
internal sealed class DeviceCode : ISharedAcrossTenants
{
    public byte[] CodeHash { get; set; } = [];
    public Guid BusinessId { get; set; }
    public DateTime ExpiresUtc { get; set; }
}

/// <summary>TK-321: a device nonce used inside the signature window (replay protection), as the cloud's <c>NodeNonces</c>.</summary>
internal sealed class DeviceNonce : ISharedAcrossTenants
{
    /// <summary><c>deviceId.nonce</c>.</summary>
    public string Key { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
}

internal sealed class LiteDbContext(DbContextOptions<LiteDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "lite";
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantDevice> Devices => Set<TenantDevice>();
    public DbSet<DeviceCode> DeviceCodes => Set<DeviceCode>();
    public DbSet<DeviceNonce> DeviceNonces => Set<DeviceNonce>();
}

internal sealed class LiteConfiguration : IEntityTypeConfiguration<Tenant>, IEntityTypeConfiguration<TenantDevice>, IEntityTypeConfiguration<DeviceCode>,
    IEntityTypeConfiguration<DeviceNonce>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("Tenants");
        b.Property(t => t.Id).ValueGeneratedNever();
    }

    public void Configure(EntityTypeBuilder<TenantDevice> b)
    {
        b.ToTable("TenantDevices");
        b.HasKey(d => d.DeviceId);
        b.Property(d => d.DeviceId).ValueGeneratedNever();
        b.Property(d => d.Name).HasMaxLength(100);
        b.Property(d => d.PublicKey).HasMaxLength(200);
        b.HasIndex(d => d.BusinessId);
    }

    public void Configure(EntityTypeBuilder<DeviceCode> b)
    {
        b.ToTable("DeviceCodes");
        b.HasKey(c => c.CodeHash);
        b.Property(c => c.CodeHash).HasMaxLength(32);
        b.HasIndex(c => c.BusinessId);
    }

    public void Configure(EntityTypeBuilder<DeviceNonce> b)
    {
        b.ToTable("DeviceNonces");
        b.HasKey(n => n.Key);
        b.Property(n => n.Key).HasMaxLength(110);
        b.HasIndex(n => n.ExpiresUtc);
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of the Lite host's own tables.</summary>
internal sealed class LiteDesignTimeFactory : DesignTimeFactory<LiteDbContext>;

/// <summary>Every business, for the workers (outbox, upload, heartbeat and license).</summary>
internal sealed class LiteTenants(LiteDbContext db) : ITenantDirectory
{
    public async Task<IReadOnlyList<Guid>> AllAsync(CancellationToken ct) => await db.Tenants.AsNoTracking().OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(ct);
}

/// <summary>TK-320: the request's business comes from its device, never from a header that names it. TK-321: the device proves itself with a
/// signature (ADR-0008's node format, <see cref="NodeSignature"/>) in <c>X-Device-Signature: deviceId.unixSeconds.nonce.signature</c> over
/// <c>deviceId.unixSeconds.nonce.METHOD /path?query bodyHash</c>, bodyHash in <c>X-Body-Sha256</c> (required): checked against the key in
/// <see cref="TenantDevice"/> (a scope of its own, so the request's connection is opened only once the tenant is set), within 15 minutes of this
/// server's clock, each nonce once, and the body hashed afterwards must match. Then Identity authenticates the device (<see cref="VerifiedDevice"/>)
/// and the session as always, inside that business. Without a proven device no <c>/api</c> route answers.</summary>
/// <remarks>Why a lookup table and not <c>IgnoreQueryFilters</c> on <c>identity.Devices</c>: with Row-Level Security a connection that has not said
/// its business sees no row at all, and a bypass in the predicate would open every table.</remarks>
internal static class DeviceTenant
{
    public const string SignatureHeader = "X-Device-Signature";
    public static readonly Error Unknown = Error.Unauthorized("Lite.UnknownDevice", "Este equipo no está unido a ningún negocio.");
    private static long _lastSweepTicks;

    public static async Task ResolveAsync(HttpContext http, RequestDelegate next)
    {
        http.Request.Headers.Remove(IdentityModule.DeviceHeader); // a store's device secret proves nothing here
        var tenant = http.RequestServices.GetRequiredService<ScopedTenant>();
        var header = http.Request.Headers[SignatureHeader].ToString();
        if (header.Length > 0)
        {
            await using var scope = http.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            if (await VerifyAsync(http, header, scope.ServiceProvider.GetRequiredService<LiteDbContext>(), http.RequestServices.GetRequiredService<TimeProvider>())
                is { } device)
            {
                tenant.TenantId = device.BusinessId;
                http.Features.Set(new VerifiedDevice(device.DeviceId));
            }
        }
        if (tenant.TenantId == Guid.Empty && http.Request.Path.StartsWithSegments("/api"))
        {
            await EndpointExtensions.Problem(Unknown).ExecuteAsync(http);
            return;
        }
        await next(http);
    }

    private static async Task<TenantDevice?> VerifyAsync(HttpContext http, string header, LiteDbContext db, TimeProvider clock)
    {
        var parts = header.Split('.');
        var claimed = http.Request.Headers[NodeSignature.BodyHashHeader].ToString();
        if (parts.Length != 4 || !Guid.TryParse(parts[0], out var id) || !long.TryParse(parts[1], out var seconds) || parts[2].Length is < 16 or > 64
            || claimed.Length is 0 or > 64)
            return null;
        var now = clock.GetUtcNow();
        if ((now - DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(seconds, 0, 253_402_300_799))).Duration() > NodeSignature.Window) return null;
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == id, http.RequestAborted);
        var request = http.Request;
        // The signature first, on the hash the device claims, so an unknown or forged request never makes the server read its body.
        if (device is null || !NodeSignature.Verify(device.PublicKey, NodeSignature.Payload(id, seconds, parts[2], request.Method, $"{request.Path}{request.QueryString}", claimed), parts[3])
            || !await UseNonceAsync(db, $"{id}.{parts[2]}", now.UtcDateTime, http.RequestAborted))
            return null;
        request.EnableBuffering(); // the endpoint still reads it
        var hash = NodeSignature.Base64Url(await SHA256.HashDataAsync(request.Body, http.RequestAborted));
        request.Body.Position = 0;
        return hash == claimed ? device : null;
    }

    /// <summary>The primary key refuses the second use, also from a second instance. Expired ones go at most once a minute.</summary>
    private static async Task<bool> UseNonceAsync(LiteDbContext db, string key, DateTime nowUtc, CancellationToken ct)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (nowUtc.Ticks - last > TimeSpan.TicksPerMinute && Interlocked.CompareExchange(ref _lastSweepTicks, nowUtc.Ticks, last) == last)
            await db.DeviceNonces.Where(n => n.ExpiresUtc < nowUtc).ExecuteDeleteAsync(ct);
        db.DeviceNonces.Add(new DeviceNonce { Key = key, ExpiresUtc = nowUtc + 2 * NodeSignature.Window });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false; // used already
        }
    }
}
