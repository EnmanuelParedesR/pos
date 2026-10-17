namespace Abastalia.Client.UI.Api;

// Inventory back office (TK-184): stock, kardex, adjustments, counts and transfers.

public enum StockMovementType { Sale, SaleVoid, Return, Purchase, PurchaseVoid, Adjustment, Count, TransferOut, TransferIn }
public enum AdjustmentReason { Shrinkage, Damage, InternalUse, Gift, Correction }
public enum StockCountStatus { Open, Applied }

public sealed record Warehouse(Guid Id, string Name, bool IsActive, bool IsMain);
/// <param name="Cost">Cost and value come null without costs.view (TK-201), like every cost and profit below.</param>
public sealed record StockLevel(Guid ProductId, string Name, string Category, UnitOfMeasure Unit, decimal Quantity, decimal? Cost, decimal? Value,
    bool BelowMinimum, bool NeedsCount);
/// <param name="Quantity">Signed: negative went out.</param>
public sealed record StockMovement(long Sequence, DateTime DateUtc, StockMovementType Type, Guid WarehouseId, decimal Quantity, decimal? UnitCost,
    decimal ResultingStock, Guid? ReferenceId, string? Reason);

/// <param name="Quantity">Positive adds, negative takes out.</param>
/// <param name="AdjustmentId">TK-304: the same id on a retry: the server applies it once.</param>
public sealed record AdjustStock(Guid ProductId, Guid? WarehouseId, decimal Quantity, AdjustmentReason Reason, string? Note, Guid AdjustmentId);
public sealed record AdjustmentRecorded(Guid AdjustmentId);

public sealed record TransferLine(Guid ProductId, decimal Quantity);
/// <param name="TransferId">TK-304: the same id on a retry: the server moves the stock once.</param>
public sealed record TransferStock(Guid Source, Guid Destination, IReadOnlyList<TransferLine> Lines, Guid TransferId);
public sealed record TransferRecorded(Guid TransferId);

public sealed record StartStockCount(Guid? WarehouseId, Guid? CategoryId);
public sealed record StockCountStarted(Guid StockCountId, int ProductCount);
public sealed record StockCountSummary(Guid Id, StockCountStatus Status, Guid WarehouseId, Guid? CategoryId, DateTime CreatedUtc, DateTime? AppliedUtc,
    int LineCount, int CountedCount);
/// <summary>A blind count line: no system quantity.</summary>
public sealed record StockCountLine(Guid ProductId, string Product, UnitOfMeasure Unit, decimal? Counted);
public sealed record StockCountSheet(Guid Id, StockCountStatus Status, Guid WarehouseId, StockCountLine[] Lines);
public sealed record CountedLine(Guid ProductId, decimal Counted);
public sealed record RecordCount(IReadOnlyList<CountedLine> Lines);
public sealed record ApplyStockCount(Guid? SupervisorApprovalId);
public sealed record CountDifference(Guid ProductId, string Product, decimal SystemStock, decimal Counted, decimal Difference, decimal? Value);
public sealed record StockCountApplied(CountDifference[] Differences, decimal? DifferenceValue);
