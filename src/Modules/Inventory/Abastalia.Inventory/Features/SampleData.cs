using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Inventory.Contracts;
using Abastalia.Inventory.Domain;
using Abastalia.Inventory.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Inventory.Features;

/// <summary>TK-196: "datos de ejemplo" so a new store can try the register in its first minutes, and remove them with one click.
/// Only catalog (categories and products marked <c>IsSample</c>, named "(ejemplo)"): no sales, purchases or receipts, so nothing of it
/// can reach the 606, 607 or 608. No barcodes or PLUs either, so they never collide with the store's real ones.</summary>
/// <remarks>A sample product the store did sell or buy is a real operation (its e-CF went out and must be reported): removing the
/// sample data deactivates that product instead of deleting it, so its history stays whole.</remarks>
internal static class SampleData
{
    public sealed record Status(int Products, int Categories);

    private static readonly (string Category, string Color, (string Name, UnitOfMeasure Unit, ItbisRate Itbis, decimal Price, decimal Cost)[] Products)[] Catalog =
    [
        ("Carnes (ejemplo)", "#B3261E", [
            ("Pechuga de pollo (ejemplo)", UnitOfMeasure.Pound, ItbisRate.Exempt, 135m, 98m),
            ("Carne de res molida (ejemplo)", UnitOfMeasure.Pound, ItbisRate.Exempt, 260m, 190m),
            ("Chuleta ahumada (ejemplo)", UnitOfMeasure.Pound, ItbisRate.General18, 225m, 160m),
        ]),
        ("Colmado (ejemplo)", "#0B5CAD", [
            ("Arroz 5 lb (ejemplo)", UnitOfMeasure.Unit, ItbisRate.Exempt, 210m, 175m),
            ("Aceite 1 L (ejemplo)", UnitOfMeasure.Unit, ItbisRate.General18, 245m, 190m),
            ("Salami 1 lb (ejemplo)", UnitOfMeasure.Unit, ItbisRate.General18, 175m, 128m),
        ]),
        ("Bebidas (ejemplo)", "#2E7D32", [
            ("Agua 20 oz (ejemplo)", UnitOfMeasure.Unit, ItbisRate.General18, 25m, 14m),
            ("Refresco 2 L (ejemplo)", UnitOfMeasure.Unit, ItbisRate.General18, 110m, 80m),
            ("Jugo de naranja 1 L (ejemplo)", UnitOfMeasure.Unit, ItbisRate.General18, 135m, 95m),
        ]),
    ];

    public sealed record Load : ICommand<Status>;

    public sealed record Remove : ICommand<Status>;

    internal sealed class LoadHandler(InventoryDbContext db, IAuditLog audit) : ICommandHandler<Load, Status>
    {
        public async Task<Result<Status>> Handle(Load c, CancellationToken ct)
        {
            // Once is enough: loading again while some are there does nothing.
            if (await db.Products.AnyAsync(p => p.IsSample && p.IsActive, ct)) return await CountAsync(db, ct);
            var warehouses = await db.Warehouses.Select(w => w.Id).ToListAsync(ct);
            foreach (var (name, color, products) in Catalog)
            {
                var category = new Category { Id = Guid.CreateVersion7(), Name = name, Color = color, IsSample = true };
                db.Categories.Add(category);
                foreach (var (product, unit, itbis, price, cost) in products)
                {
                    // Sold without stock: no opening movement, so a product never sold leaves no trace when removed.
                    var p = new Product
                    {
                        Id = Guid.CreateVersion7(), Name = product, CategoryId = category.Id, Unit = unit, ItbisRate = itbis, Price = price, Cost = cost,
                        SellWithoutStock = true, IsSample = true,
                    };
                    db.Products.Add(p);
                    db.Stock.AddRange(warehouses.Select(w => new Stock { ProductId = p.Id, WarehouseId = w }));
                }
            }
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync("inventory.sample-data-load", "Product", Guid.Empty, new { Products = Catalog.Sum(x => x.Products.Length) }, ct);
            return await CountAsync(db, ct);
        }
    }

    internal sealed class RemoveHandler(InventoryDbContext db, IAuditLog audit) : ICommandHandler<Remove, Status>
    {
        public async Task<Result<Status>> Handle(Remove c, CancellationToken ct)
        {
            var sample = db.Products.Where(p => p.IsSample);
            var used = sample.Where(p => db.StockMovements.Any(m => m.ProductId == p.Id)).Select(p => p.Id);
            var unused = sample.Where(p => !db.StockMovements.Any(m => m.ProductId == p.Id)).Select(p => p.Id);
            var kept = await used.CountAsync(ct);
            await db.Products.Where(p => used.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), ct);
            await db.Barcodes.Where(b => unused.Contains(b.ProductId)).ExecuteDeleteAsync(ct);
            await db.Presentations.Where(x => unused.Contains(x.ProductId)).ExecuteDeleteAsync(ct);
            await db.Stock.Where(x => unused.Contains(x.ProductId)).ExecuteDeleteAsync(ct);
            await db.Set<StockCountLine>().Where(l => unused.Contains(l.ProductId)).ExecuteDeleteAsync(ct);
            var deleted = await db.Products.Where(p => p.IsSample && !db.StockMovements.Any(m => m.ProductId == p.Id)).ExecuteDeleteAsync(ct);
            // A sample category the owner filled with real products stays (deactivated only if nothing active is left in it).
            var empty = db.Categories.Where(x => x.IsSample && !db.Products.Any(p => p.CategoryId == x.Id));
            await empty.ExecuteDeleteAsync(ct);
            await db.Categories.Where(x => x.IsSample && !db.Products.Any(p => p.CategoryId == x.Id && p.IsActive))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);
            await audit.RecordAsync("inventory.sample-data-remove", "Product", Guid.Empty, new { Deleted = deleted, Deactivated = kept }, ct);
            return await CountAsync(db, ct);
        }
    }

    /// <summary>Sample data still showing (active).</summary>
    private static async Task<Status> CountAsync(InventoryDbContext db, CancellationToken ct) =>
        new(await db.Products.CountAsync(p => p.IsSample && p.IsActive, ct), await db.Categories.CountAsync(x => x.IsSample && x.IsActive, ct));

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/inventory/sample-data", async (InventoryDbContext db, CancellationToken ct) => Results.Ok(await CountAsync(db, ct)))
                .RequireAuthorization().WithTags("Inventory");
            app.MapPost("/api/inventory/sample-data", async (ICommandHandler<Load, Status> h, CancellationToken ct) => (await h.Handle(new Load(), ct)).ToHttp())
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
            app.MapDelete("/api/inventory/sample-data", async (ICommandHandler<Remove, Status> h, CancellationToken ct) => (await h.Handle(new Remove(), ct)).ToHttp())
                .RequirePermission(Permissions.ProductsManage).WithTags("Inventory");
        }
    }
}
