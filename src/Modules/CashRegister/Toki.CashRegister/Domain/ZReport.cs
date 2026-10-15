using Toki.CashRegister.Contracts;

namespace Toki.CashRegister.Domain;

/// <param name="CustomerPayments">Customer payments on credit, by method (they are in the card terminal's batch too).</param>
internal sealed record MethodTotal(PaymentMethod Method, decimal SalePayments, decimal Voids, decimal Refunds, decimal CustomerPayments, decimal Net);

/// <summary>Z report (closed shift) or X report (open shift, for a supervisor).</summary>
internal sealed record ZReport(
    Guid ShiftId, int? ZNumber, string Register, string Cashier, DateTime OpenedUtc, DateTime? ClosedUtc, decimal OpeningFloat,
    IReadOnlyList<MethodTotal> ByMethod, int SaleCount, int VoidedCount, decimal CustomerPayments, decimal Rounding, decimal CashIn, decimal Withdrawals, decimal Expenses,
    decimal ExpectedCash, decimal? CountedCash, decimal? Difference, IReadOnlyList<DenominationCount>? CashCount, Guid? ClosingSupervisorId,
    string? AuditChainHead)
{
    /// <summary>Marks the reversal of a sale's rounding (so it is not counted as a voided payment).</summary>
    public const string RoundingReference = "rounding";

    /// <param name="auditChainHead">Head of the audit chain ("#seq hash"), printed so a rewound chain shows on paper. A closed shift keeps the one from its closing.</param>
    public static ZReport Build(Shift s, string register, IReadOnlyList<CashMovement> movements, IReadOnlyList<DenominationCount>? cashCount, string? auditChainHead)
    {
        static bool VoidsRounding(CashMovement m) => m.Type == CashMovementType.SalePaymentVoid && m.Reference == RoundingReference;
        decimal Sum(CashMovementType type, PaymentMethod? method = null) =>
            movements.Where(m => m.Type == type && !VoidsRounding(m) && (method is null || m.Method == method)).Sum(m => m.Amount);

        var byMethod = movements.Where(m => m.Type is CashMovementType.SalePayment or CashMovementType.SalePaymentVoid or CashMovementType.Refund
                or CashMovementType.CustomerPayment or CashMovementType.CustomerPaymentVoid)
            .Select(m => m.Method).Distinct().Order()
            .Select(method =>
            {
                var salePayments = Sum(CashMovementType.SalePayment, method);
                var voids = Sum(CashMovementType.SalePaymentVoid, method);
                var refunds = Sum(CashMovementType.Refund, method);
                var customerPayments = Sum(CashMovementType.CustomerPayment, method) - Sum(CashMovementType.CustomerPaymentVoid, method);
                return new MethodTotal(method, salePayments, voids, refunds, customerPayments, salePayments - voids - refunds + customerPayments);
            }).ToList();

        var voided = movements.Where(m => m.Type == CashMovementType.SalePaymentVoid && !VoidsRounding(m)).Select(m => m.ReferenceId).ToHashSet();
        var sales = movements.Where(m => m.Type == CashMovementType.SalePayment).Select(m => m.ReferenceId).Distinct().Count();
        var rounding = Sum(CashMovementType.Rounding) - movements.Where(VoidsRounding).Sum(m => m.Amount);

        return new ZReport(s.Id, s.ZNumber, register, s.CashierName, s.OpenedUtc, s.ClosedUtc, s.OpeningFloat, byMethod,
            sales - voided.Count, voided.Count, Sum(CashMovementType.CustomerPayment) - Sum(CashMovementType.CustomerPaymentVoid), rounding,
            Sum(CashMovementType.CashIn), Sum(CashMovementType.Withdrawal), Sum(CashMovementType.Expense),
            s.ExpectedCash ?? ShiftBalance.ExpectedCash(s.OpeningFloat, movements), s.CountedCash, s.Difference, cashCount, s.ClosingSupervisorId, s.AuditChainHead ?? auditChainHead);
    }
}
