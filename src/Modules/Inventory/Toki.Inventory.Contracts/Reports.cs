namespace Toki.Inventory.Contracts;

/// <summary>Stock of a product in all warehouses, valued at its average cost (without ITBIS) and at its shelf price (with ITBIS).</summary>
public sealed record InventoryValue(Guid ProductId, string Product, Guid CategoryId, string Category, UnitOfMeasure Unit, bool IsActive,
    decimal Stock, decimal Cost, decimal CostValue, decimal Price, decimal SaleValue);

public interface IInventoryReports
{
    /// <summary>Active products, and inactive ones that still have stock (so the value matches the kardex), highest cost value first.
    /// Negative stock is kept as is: it shows what must be counted.</summary>
    Task<IReadOnlyList<InventoryValue>> ValueAsync(CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, string>> CategoriesAsync(CancellationToken ct);
}
