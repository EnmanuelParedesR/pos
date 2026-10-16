using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Inventory.Contracts;
using Toki.Inventory.Domain;
using Toki.Inventory.Persistence;
using Toki.SharedKernel;

namespace Toki.Inventory;

public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<InventoryDbContext>();
        services.AddScoped<ICatalogQueries, CatalogQueries>();
        services.AddScoped<IInventoryReports, InventoryReports>();
        services.AddScoped<StockMovements>();
        services.AddScoped<IStockMovements>(sp => sp.GetRequiredService<StockMovements>());
        return services;
    }
}

internal sealed class CatalogQueries(InventoryDbContext db) : ICatalogQueries
{
    public async Task<IReadOnlyList<ProductToReorder>> BelowMinimumAsync(CancellationToken ct)
    {
        var stock = db.Stock.GroupBy(e => e.ProductId).Select(g => new { ProductId = g.Key, Quantity = g.Sum(e => e.Quantity) });
        return await (from p in db.Products
                      join e in stock on p.Id equals e.ProductId
                      where p.IsActive && p.MinimumStock > 0 && e.Quantity <= p.MinimumStock
                      orderby p.Name
                      select new ProductToReorder(p.Id, p.Name, p.Unit, p.ItbisRate, e.Quantity, p.MinimumStock, p.MaximumStock))
            .AsNoTracking().ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ProductForSale>> GetForSaleAsync(IReadOnlyCollection<(Guid ProductId, Guid? PresentationId)> items, CancellationToken ct)
    {
        var productIds = items.Select(i => i.ProductId).Distinct().ToList();
        var presentationIds = items.Where(i => i.PresentationId is not null).Select(i => i.PresentationId!.Value).Distinct().ToList();

        var products = await (from p in db.Products
                              join c in db.Categories on p.CategoryId equals c.Id
                              where productIds.Contains(p.Id)
                              select new { p, Category = c.Name }).AsNoTracking().ToDictionaryAsync(x => x.p.Id, ct);
        var presentations = await db.Presentations.Where(x => presentationIds.Contains(x.Id)).AsNoTracking().ToDictionaryAsync(x => x.Id, ct);

        var result = new List<ProductForSale>();
        foreach (var (productId, presentationId) in items.Distinct())
        {
            if (!products.TryGetValue(productId, out var x)) continue;
            var p = x.p;
            if (presentationId is { } pid)
            {
                if (!presentations.TryGetValue(pid, out var pr) || pr.ProductId != productId) continue;
                result.Add(new ProductForSale(p.Id, pr.Id, $"{p.Name} ({pr.Name})", p.CategoryId, x.Category, p.Unit, p.ItbisRate,
                    pr.Price, pr.WholesalePrice, pr.Factor, p.Cost, p.SellWithoutStock, p.IsActive && pr.IsActive, pr.Name));
            }
            else
            {
                result.Add(new ProductForSale(p.Id, null, p.Name, p.CategoryId, x.Category, p.Unit, p.ItbisRate,
                    p.Price, p.WholesalePrice, 1, p.Cost, p.SellWithoutStock, p.IsActive));
            }
        }
        return result;
    }

    public async Task<Result<ScaleLabelReading>> ReadScaleLabelAsync(string code, CancellationToken ct)
    {
        var parsed = ScaleLabel.Read(code.Trim(), await db.LabelFormats.AsNoTracking().ToListAsync(ct));
        if (parsed is null) return Error.Validation("Inventory.NotAScaleLabel", "Ese código no es una etiqueta de balanza.");
        if (parsed.IsFailure) return parsed.Error!;
        var plu = Product.NormalizePlu(parsed.Value.Plu);
        var productId = await db.Products.AsNoTracking().Where(p => p.Plu == plu).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (productId is null) return InventoryErrors.CodeNotFound;
        return new ScaleLabelReading(productId.Value, parsed.Value.Weight, parsed.Value.Price);
    }
}

/// <summary>Every stock change goes through here: atomic relative updates on the stock row plus a kardex row.
/// Rows are always touched in ProductId order so concurrent commands lock them in the same order (no deadlocks).</summary>
internal sealed class StockMovements(InventoryDbContext db, IOutbox outbox, IUserContext user, TimeProvider clock) : IStockMovements
{
    public Task<Result> RecordOutboundAsync(StockMovementType type, Guid referenceId, IReadOnlyList<StockMovementLine> lines, CancellationToken ct, Guid? warehouseId = null,
        bool onlyWithStock = false, bool reverseCost = false) =>
        OutboundAsync(type, referenceId, lines, allowNegativeIfProductAllows: !onlyWithStock, reason: null, ct, warehouseId, reverseCost);

    /// <param name="allowNegativeIfProductAllows">Only sales honour "sell without stock"; adjustments and transfers never go below zero.</param>
    public async Task<Result> OutboundAsync(StockMovementType type, Guid referenceId, IReadOnlyList<StockMovementLine> lines,
        bool allowNegativeIfProductAllows, string? reason, CancellationToken ct, Guid? warehouseId = null, bool reverseCost = false)
    {
        var warehouse = warehouseId ?? Warehouse.MainId;
        foreach (var line in Group(lines))
        {
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == line.ProductId, ct);
            if (product is null) return InventoryErrors.ProductNotFound;
            if (ValidateQuantity(product, line.Quantity) is { } error) return error;

            var q = line.Quantity;
            var allow = allowNegativeIfProductAllows && product.SellWithoutStock;
            // Atomic conditional decrement: two registers selling the last pound cannot both succeed.
            var rows = await db.Stock
                .Where(e => e.ProductId == line.ProductId && e.WarehouseId == warehouse && (allow || e.Quantity >= q))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Quantity, e => e.Quantity - q)
                    .SetProperty(e => e.NeedsCount, e => e.NeedsCount || e.Quantity - q < 0), ct);
            var resulting = await QuantityAsync(line.ProductId, warehouse, ct);
            if (rows == 0) return InventoryErrors.OutOfStock(product.Name, resulting);

            var cost = product.Cost;
            if (reverseCost && line.UnitCost is { } inboundCost)
            {
                // Same order as an entry: the stock row is already locked, so the cost read here cannot be lost by a concurrent purchase.
                var currentCost = await db.Products.Where(p => p.Id == line.ProductId).Select(p => p.Cost).SingleAsync(ct);
                var total = await db.Stock.Where(e => e.ProductId == line.ProductId).SumAsync(e => e.Quantity, ct);
                if (AverageCost.Reverse(total, currentCost, q, inboundCost) is { } reversed)
                    await db.Products.Where(p => p.Id == line.ProductId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Cost, reversed), ct);
                cost = inboundCost;
            }
            Record(type, referenceId, line.ProductId, warehouse, -q, cost, resulting, reason);
        }
        return Result.Success();
    }

    public Task<Result> RecordInboundAsync(StockMovementType type, Guid referenceId, IReadOnlyList<StockMovementLine> lines, bool updateCost, CancellationToken ct, Guid? warehouseId = null) =>
        InboundAsync(type, referenceId, lines, updateCost, reason: null, ct, warehouseId);

    public async Task<Result> InboundAsync(StockMovementType type, Guid referenceId, IReadOnlyList<StockMovementLine> lines, bool updateCost,
        string? reason, CancellationToken ct, Guid? warehouseId = null)
    {
        var warehouse = warehouseId ?? Warehouse.MainId;
        foreach (var line in lines.OrderBy(l => l.ProductId))
        {
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == line.ProductId, ct);
            if (product is null) return InventoryErrors.ProductNotFound;
            if (ValidateQuantity(product, line.Quantity) is { } error) return error;

            // Update the stock row first: it takes the row lock before the cost is read, so two purchases cannot lose a cost update.
            var q = line.Quantity;
            var rows = await db.Stock.Where(e => e.ProductId == line.ProductId && e.WarehouseId == warehouse)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Quantity, e => e.Quantity + q), ct);
            if (rows == 0) return InventoryErrors.WarehouseNotFound;

            var cost = product.Cost;
            if (updateCost && line.UnitCost is { } inboundCost)
            {
                var currentCost = await db.Products.Where(p => p.Id == line.ProductId).Select(p => p.Cost).SingleAsync(ct);
                var totalAfter = await db.Stock.Where(e => e.ProductId == line.ProductId).SumAsync(e => e.Quantity, ct);
                cost = AverageCost.Calculate(totalAfter - q, currentCost, q, inboundCost);
                await db.Products.Where(p => p.Id == line.ProductId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Cost, cost), ct);
            }

            var resulting = await QuantityAsync(line.ProductId, warehouse, ct);
            Record(type, referenceId, line.ProductId, warehouse, q, line.UnitCost ?? cost, resulting, reason);
        }
        return Result.Success();
    }

    /// <summary>Applies a counted difference as a relative change (sales after the count stay counted) and clears the count flag.</summary>
    public async Task<decimal> ApplyDifferenceAsync(StockMovementType type, Guid referenceId, Guid productId, Guid warehouse, decimal difference,
        decimal cost, string? reason, CancellationToken ct)
    {
        await db.Stock.Where(e => e.ProductId == productId && e.WarehouseId == warehouse)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Quantity, e => e.Quantity + difference).SetProperty(e => e.NeedsCount, false), ct);
        var resulting = await QuantityAsync(productId, warehouse, ct);
        if (difference != 0) Record(type, referenceId, productId, warehouse, difference, cost, resulting, reason);
        return resulting;
    }

    private void Record(StockMovementType type, Guid referenceId, Guid productId, Guid warehouse, decimal quantity, decimal cost, decimal resulting, string? reason)
    {
        var m = new StockMovement
        {
            Id = Guid.CreateVersion7(), ProductId = productId, WarehouseId = warehouse, Type = type, Quantity = quantity,
            UnitCost = cost, ResultingStock = resulting, ReferenceId = referenceId, Reason = reason,
            UserId = user.UserId == Guid.Empty ? null : user.UserId, DateUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.StockMovements.Add(m);
        outbox.Add(new StockMovementRecordedV1(m.Id, productId, warehouse, type, quantity, cost, referenceId));
    }

    private Task<decimal> QuantityAsync(Guid productId, Guid warehouse, CancellationToken ct) =>
        db.Stock.Where(e => e.ProductId == productId && e.WarehouseId == warehouse).Select(e => e.Quantity).SingleOrDefaultAsync(ct);

    private static Error? ValidateQuantity(Product p, decimal quantity) =>
        quantity <= 0 || quantity != Math.Round(quantity, 3) || (p.Unit == UnitOfMeasure.Unit && quantity != Math.Truncate(quantity))
            ? Error.Validation("Inventory.InvalidQuantity", $"La cantidad de {p.Name} no es válida (se vende por {(p.Unit == UnitOfMeasure.Pound ? "libra" : "unidad")}).")
            : null;

    private static IEnumerable<StockMovementLine> Group(IReadOnlyList<StockMovementLine> lines) =>
        lines.GroupBy(l => l.ProductId).OrderBy(g => g.Key).Select(g =>
        {
            var quantity = g.Sum(l => l.Quantity);
            // The group keeps the weighted cost of its lines (a voided purchase may list one product twice at different costs).
            decimal? cost = g.All(l => l.UnitCost is not null) ? Math.Round(g.Sum(l => l.Quantity * l.UnitCost!.Value) / quantity, 4) : null;
            return new StockMovementLine(g.Key, quantity, cost);
        });
}
