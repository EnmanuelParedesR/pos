using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using QRCoder;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Inventory.Contracts;
using Toki.SharedKernel;
using Toki.Sales.Contracts;
using Toki.Sales.Domain;
using Toki.Sync.Contracts;

namespace Toki.Sales.Features;

/// <summary>The public catalog (TK-195): which products the owner shows and the WhatsApp number orders go to.
/// The cloud serves the page from the events written here; a price change reaches it through Inventory's PriceChangedV1.</summary>
internal static class PublicCatalog
{
    /// <param name="PanelUrl">The owner's panel (Cloud:PanelUrl); the public page is {PanelUrl}/c/{storeId}.</param>
    public sealed record Settings(string? WhatsApp, IReadOnlyList<Guid> ProductIds, string? PanelUrl);

    public sealed record Command(string? WhatsApp, IReadOnlyList<Guid> ProductIds) : ICommand<Ok>;

    /// <summary>Digits only, with the country code: a Dominican 10-digit number gets the 1 in front (wa.me needs it).</summary>
    public static string? NormalizeWhatsApp(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length switch
        {
            0 => null,
            10 when digits[..3] is "809" or "829" or "849" => "1" + digits,
            _ => digits,
        };
    }

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => NormalizeWhatsApp(c.WhatsApp)).Must(w => w is null || w.Length is >= 10 and <= 15).WithName("WhatsApp")
                .WithMessage("El número de WhatsApp no es válido. Escríbalo con el código de área, por ejemplo 809 555 0101.");
            RuleFor(c => c.ProductIds).Must(p => p.Count <= 1000).WithMessage("El catálogo tiene como máximo 1,000 productos.");
        }
    }

    /// <summary>Sends every product that is or was on the catalog, so a name changed in Productos also reaches the page when the owner saves.</summary>
    internal sealed class Handler(SalesDbContext db, ICatalogQueries catalog, IOutbox outbox, IAuditLog audit) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var shown = c.ProductIds.ToHashSet();
            var before = await db.CatalogItems.ToListAsync(ct);
            var products = await catalog.GetForSaleAsync(before.Select(i => i.ProductId).Union(shown).Select(id => (id, (Guid?)null)).ToList(), ct);
            if (shown.Except(products.Select(p => p.ProductId)).Any()) return SalesErrors.ProductUnavailable;

            db.CatalogItems.RemoveRange(before.Where(i => !shown.Contains(i.ProductId)));
            db.CatalogItems.AddRange(shown.Except(before.Select(i => i.ProductId)).Select(id => new CatalogItem { ProductId = id }));
            var settings = await db.CatalogSettings.FirstOrDefaultAsync(ct);
            if (settings is null) db.CatalogSettings.Add(settings = new CatalogSettings());
            settings.WhatsApp = NormalizeWhatsApp(c.WhatsApp);

            foreach (var p in products) outbox.Add(ToEvent(p, shown.Contains(p.ProductId)));
            outbox.Add(new CatalogSettingsChangedV1(settings.WhatsApp));
            await audit.RecordAsync("catalog.update", nameof(CatalogSettings), Guid.Empty, new { settings.WhatsApp, Products = shown.Count }, ct);
            return Ok.Value;
        }
    }

    /// <summary>Only what a customer may see: no cost, no stock. An inactive product is taken off the page.</summary>
    internal static CatalogItemChangedV1 ToEvent(ProductForSale p, bool shown) =>
        new(p.ProductId, p.Name, p.Category, p.Unit.ToString(), p.Price, shown && p.IsActive);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/catalog", async (SalesDbContext db, IConfiguration config, CancellationToken ct) =>
                {
                    var settings = await db.CatalogSettings.AsNoTracking().FirstOrDefaultAsync(ct);
                    var ids = await db.CatalogItems.AsNoTracking().Select(i => i.ProductId).ToListAsync(ct);
                    var panel = config["Cloud:PanelUrl"]?.Trim().TrimEnd('/');
                    return Results.Ok(new Settings(settings?.WhatsApp, ids, string.IsNullOrEmpty(panel) ? null : panel));
                })
                .RequirePermission(Permissions.ProductsManage).WithTags("Catalog");
            app.MapPut("/api/catalog", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.ProductsManage).WithTags("Catalog");
            // The QR to print: an SVG of the page's address, drawn here so the back office needs no QR library.
            app.MapGet("/api/catalog/qr", (string url) =>
                    Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" && url.Length <= 300
                        ? Results.Ok(new { Svg = Qr(url) })
                        : EndpointExtensions.Problem(Error.Validation("Catalog.InvalidUrl", "La dirección del catálogo no es válida.")))
                .RequirePermission(Permissions.ProductsManage).WithTags("Catalog");
        }

        private static string Qr(string url)
        {
            using var data = QRCodeGenerator.GenerateQrCode(url, QRCodeGenerator.ECCLevel.M);
            return new SvgQRCode(data).GetGraphic(8);
        }
    }
}

/// <summary>On connecting to the cloud: the catalog as it is, so a store that set it up before connecting has its page.</summary>
internal sealed class CatalogSnapshot(SalesDbContext db, ICatalogQueries catalog, IOutbox outbox) : ICloudSnapshot
{
    public async Task EnqueueAsync(CancellationToken ct)
    {
        var settings = await db.CatalogSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var ids = await db.CatalogItems.AsNoTracking().Select(i => i.ProductId).ToListAsync(ct);
        if (settings is null && ids.Count == 0) return;
        foreach (var p in await catalog.GetForSaleAsync(ids.Select(id => (id, (Guid?)null)).ToList(), ct)) outbox.Add(PublicCatalog.ToEvent(p, shown: true));
        outbox.Add(new CatalogSettingsChangedV1(settings?.WhatsApp));
    }
}
