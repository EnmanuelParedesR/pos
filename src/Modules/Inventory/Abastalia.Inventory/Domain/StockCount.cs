namespace Abastalia.Inventory.Domain;

internal enum StockCountStatus { Open, Applied }

/// <summary>Blind physical count: counters never see the expected quantity; differences appear only when it is applied.</summary>
internal sealed class StockCount
{
    public Guid Id { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid? CategoryId { get; set; }
    public StockCountStatus Status { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? AppliedUtc { get; set; }
    public List<StockCountLine> Lines { get; set; } = [];
}

internal sealed class StockCountLine
{
    public Guid StockCountId { get; set; }
    public Guid ProductId { get; set; }
    public decimal? Counted { get; set; }
    /// <summary>System stock at the moment the line was counted. Never returned by any endpoint (the count is blind).
    /// Applying the count adds Counted − SystemAtCount, so sales made after counting are not erased.</summary>
    public decimal? SystemAtCount { get; set; }
}
