using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Catalog;

/// <summary>A product on a store's public catalog (TK-195), from CatalogItemChangedV1; PriceChangedV1 keeps its price current.
/// Only what a customer may see: there is no cost or stock column.</summary>
public sealed class ReplicaCatalogItem : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid ProductId { get; set; }
    public Guid StoreId { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Price { get; set; }
    public bool Visible { get; set; }
    /// <summary>Newest applied event: an older one arriving late never undoes a newer change.</summary>
    public DateTime ChangedUtc { get; set; }
}

public sealed class ReplicaCatalogSettings : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public string? WhatsApp { get; set; }
    public DateTime ChangedUtc { get; set; }
    /// <summary>TK-280: the store's logo (BusinessLogoChangedV1), a PNG under 48 KB; its own time, so a WhatsApp change never undoes a newer logo.</summary>
    public byte[]? Logo { get; set; }
    public DateTime? LogoChangedUtc { get; set; }
}

public sealed record PublicCatalogItem(Guid ProductId, string Name, string Category, string Unit, decimal Price);

/// <param name="LogoVersion">TK-280: null when the store has no logo; else <c>/api/public/catalog/{storeId}/logo?v={LogoVersion}</c> serves it.</param>
public sealed record PublicCatalogPage(string StoreName, string? WhatsApp, IReadOnlyList<PublicCatalogItem> Items, string? LogoVersion = null);

internal static class PublicCatalogEndpoints
{
    public const string RateLimit = "public-catalog";

    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<ReplicaCatalogItem>(e =>
        {
            e.ToTable("CatalogItems", "replica");
            e.HasKey(i => new { i.TenantId, i.ProductId });
            e.Property(i => i.Name).HasMaxLength(150);
            e.Property(i => i.Category).HasMaxLength(100);
            e.Property(i => i.Unit).HasMaxLength(10);
            e.HasIndex(i => new { i.StoreId, i.Visible });
        });
        builder.Entity<ReplicaCatalogSettings>(e =>
        {
            e.ToTable("CatalogSettings", "replica");
            e.HasKey(s => new { s.TenantId, s.StoreId });
            e.Property(s => s.WhatsApp).HasMaxLength(15);
            e.HasIndex(s => s.StoreId);
        });
    }

    /// <summary>No sign-in: the page a store's customers open from the QR or a shared link. The store id is the fixed address,
    /// so the QR never changes. Cached a minute by browsers and proxies; at most <c>Catalog:RequestsPerMinute</c> (60) per IP address (R8 #17).</summary>
    // shortcut: the address uses the store id (not a readable slug); add a slug the owner picks in the panel if stores ask to share a short link.
    public static void MapPublicCatalog(this IEndpointRouteBuilder app)
    {
        // R8 #17: a business disabled by Abastalia staff (TK-189) has no public catalog either.
        static IQueryable<Store> Open(CloudDbContext db, Guid storeId) =>
            db.Stores.AsNoTracking().Where(s => s.Id == storeId && !db.Tenants.Any(t => t.Id == s.TenantId && t.DisabledUtc != null));
        var notFound = Error.NotFound("Catalog.NotFound", "Este catálogo no existe.");

        app.MapGet("/api/public/catalog/{storeId:guid}", async (Guid storeId, CloudDbContext db, HttpContext http, CancellationToken ct) =>
            {
                var store = await Open(db, storeId).FirstOrDefaultAsync(ct);
                if (store is null) return EndpointExtensions.Problem(notFound);
                var settings = await db.CatalogSettings.AsNoTracking().Where(s => s.StoreId == storeId)
                    .Select(s => new { s.WhatsApp, s.LogoChangedUtc, HasLogo = s.Logo != null }).FirstOrDefaultAsync(ct);
                var items = await db.CatalogItems.AsNoTracking().Where(i => i.StoreId == storeId && i.Visible)
                    .Select(i => new PublicCatalogItem(i.ProductId, i.Name, i.Category, i.Unit, i.Price)).ToListAsync(ct);
                http.Response.Headers.CacheControl = "public, max-age=60";
                return Results.Ok(new PublicCatalogPage(store.Name, settings?.WhatsApp, [.. items.OrderBy(i => i.Category).ThenBy(i => i.Name)],
                    settings is { HasLogo: true, LogoChangedUtc: { } at } ? at.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
            })
            .AllowAnonymous().RequireCors(p => p.AllowAnyOrigin().WithMethods("GET")).RequireRateLimiting(RateLimit).WithTags("Catalog");

        // TK-280: the store's logo, a PNG the store server wrote (and the projector checked). The page asks with ?v=, so it can be kept a day.
        app.MapGet("/api/public/catalog/{storeId:guid}/logo", async (Guid storeId, CloudDbContext db, HttpContext http, CancellationToken ct) =>
            {
                var png = await Open(db, storeId).SelectMany(s => db.CatalogSettings.Where(c => c.StoreId == s.Id && c.TenantId == s.TenantId).Select(c => c.Logo))
                    .FirstOrDefaultAsync(ct);
                if (png is null) return EndpointExtensions.Problem(notFound);
                http.Response.Headers.CacheControl = "public, max-age=86400";
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(png, "image/png");
            })
            .AllowAnonymous().RequireCors(p => p.AllowAnyOrigin().WithMethods("GET")).RequireRateLimiting(RateLimit).WithTags("Catalog");
    }
}
