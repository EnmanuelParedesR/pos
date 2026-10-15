using Toki.SharedKernel;

namespace Toki.Inventory.Contracts;

public enum UnitOfMeasure { Unit, Pound }

public enum StockMovementType
{
    Sale, SaleVoid, Return,
    Purchase, PurchaseVoid,
    Adjustment, Count,
    TransferOut, TransferIn,
}

/// <summary>What a sale needs to price and record a line. <see cref="Factor"/> is base units per sold unit (1 without a presentation).</summary>
public sealed record ProductForSale(
    Guid ProductId, Guid? PresentationId, string Name, Guid CategoryId, string Category, UnitOfMeasure Unit,
    ItbisRate ItbisRate, decimal Price, decimal? WholesalePrice, decimal Factor, decimal BaseCost,
    bool SellWithoutStock, bool IsActive);

public interface ICatalogQueries
{
    /// <summary>Products (or presentations) by id. Missing ids are simply absent from the result.</summary>
    Task<IReadOnlyList<ProductForSale>> GetForSaleAsync(IReadOnlyCollection<(Guid ProductId, Guid? PresentationId)> items, CancellationToken ct);

    /// <summary>Reads a scale label (weight or price) and finds its product by PLU. A sale trusts this, never an amount sent by the register.</summary>
    Task<Result<ScaleLabelReading>> ReadScaleLabelAsync(string code, CancellationToken ct);

    /// <summary>TK-191: active products with a minimum whose stock (all warehouses together) is at or under it, by name.</summary>
    Task<IReadOnlyList<ProductToReorder>> BelowMinimumAsync(CancellationToken ct);
}

/// <param name="MaximumStock">0 when the owner did not set one.</param>
public sealed record ProductToReorder(Guid ProductId, string Name, UnitOfMeasure Unit, ItbisRate ItbisRate, decimal Stock, decimal MinimumStock, decimal MaximumStock);

/// <summary>Exactly one of <see cref="Weight"/> (pounds) or <see cref="Amount"/> (line total) is set.</summary>
public sealed record ScaleLabelReading(Guid ProductId, decimal? Weight, decimal? Amount);

/// <param name="Quantity">Base units (units or pounds), always positive.</param>
/// <param name="UnitCost">Cost per base unit for inbound movements (purchases); ignored on outbound ones.</param>
public sealed record StockMovementLine(Guid ProductId, decimal Quantity, decimal? UnitCost = null);

/// <summary>Stock changes called inside another module's transaction (a sale, a purchase).</summary>
public interface IStockMovements
{
    /// <summary>Takes stock out. Fails with Inventory.OutOfStock unless the product may be sold without stock (then it is flagged for counting).</summary>
    /// <param name="onlyWithStock">True for non-sale outbound movements (voiding a purchase): never below zero, even for products sold without stock.</param>
    /// <param name="reverseCost">Voiding a purchase: each line's <see cref="StockMovementLine.UnitCost"/> is taken back out of the average cost.</param>
    Task<Result> RecordOutboundAsync(StockMovementType type, Guid referenceId, IReadOnlyList<StockMovementLine> lines, CancellationToken ct, Guid? warehouseId = null,
        bool onlyWithStock = false, bool reverseCost = false);

    /// <summary>Puts stock in. When <paramref name="updateCost"/> is true (purchases) the weighted average cost is recomputed.</summary>
    Task<Result> RecordInboundAsync(StockMovementType type, Guid referenceId, IReadOnlyList<StockMovementLine> lines, bool updateCost, CancellationToken ct, Guid? warehouseId = null);
}

public sealed record PriceChangedV1(Guid ProductId, decimal OldPrice, decimal NewPrice, decimal? OldWholesalePrice, decimal? NewWholesalePrice)
    : IntegrationEvent(ProductId);

public sealed record StockMovementRecordedV1(Guid MovementId, Guid ProductId, Guid WarehouseId, StockMovementType Type, decimal Quantity, decimal UnitCost, Guid? ReferenceId)
    : IntegrationEvent(ProductId);
