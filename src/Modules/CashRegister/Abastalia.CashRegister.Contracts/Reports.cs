namespace Abastalia.CashRegister.Contracts;

/// <summary>A shift for the reconciliation report. Expected and counted cash only exist once the shift is closed (the count is blind).</summary>
public sealed record ShiftSummary(Guid ShiftId, string Register, string Cashier, DateTime OpenedUtc, DateTime? ClosedUtc, int? ZNumber,
    decimal OpeningFloat, decimal? ExpectedCash, decimal? CountedCash, decimal? Difference);

public interface ICashRegisterReports
{
    /// <summary>Shifts opened in the business-day range, oldest first.</summary>
    Task<IReadOnlyList<ShiftSummary>> ShiftsAsync(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Names of the registers with an open shift, whenever it was opened.</summary>
    Task<IReadOnlyList<string>> OpenRegistersAsync(CancellationToken ct);

    /// <summary>R11 A2: whether the shift is still open (its count is still blind for its cashier).</summary>
    Task<bool> IsOpenAsync(Guid shiftId, CancellationToken ct);
}
