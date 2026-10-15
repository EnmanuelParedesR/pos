namespace Toki.CashRegister.Contracts;

/// <summary>A shift for the reconciliation report. Expected and counted cash only exist once the shift is closed (the count is blind).</summary>
public sealed record ShiftSummary(Guid ShiftId, string Register, string Cashier, DateTime OpenedUtc, DateTime? ClosedUtc, int? ZNumber,
    decimal OpeningFloat, decimal? ExpectedCash, decimal? CountedCash, decimal? Difference);

public interface ICashRegisterReports
{
    /// <summary>Shifts opened in the business-day range, oldest first.</summary>
    Task<IReadOnlyList<ShiftSummary>> ShiftsAsync(DateOnly from, DateOnly to, CancellationToken ct);
}
