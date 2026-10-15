using Toki.SharedKernel;

namespace Toki.CashRegister.Contracts;

/// <summary>How a customer pays. Only cash gives change; OnAccount and CreditBalance need a customer.</summary>
public enum PaymentMethod { Cash, Card, Transfer, OnAccount, CreditBalance }

public sealed record OpenShift(Guid ShiftId, Guid RegisterId, string Register, Guid CashierId);

public sealed record PaymentByMethod(PaymentMethod Method, decimal Amount, string? Reference);

/// <summary>Cash-drawer operations called inside a sale's transaction.</summary>
public interface ICashRegisterOperations
{
    /// <summary>The shift if it is open and the caller may sell on it (its cashier, or someone who can approve closings).</summary>
    Task<Result<OpenShift>> GetOpenShiftAsync(Guid shiftId, CancellationToken ct);

    /// <param name="rounding">Centavos kept because cash change is given in whole pesos.</param>
    Task RecordSalePaymentAsync(Guid shiftId, Guid saleId, IReadOnlyList<PaymentByMethod> payments, decimal rounding, CancellationToken ct);

    /// <summary>Money given back to a customer (a return).</summary>
    Task<Result> RecordRefundAsync(Guid shiftId, Guid referenceId, PaymentMethod method, decimal amount, CancellationToken ct);

    /// <summary>A customer pays their credit (on account) at the register.</summary>
    /// <param name="rounding">Cash centavos kept because the customer paid in whole pesos.</param>
    Task<Result> RecordCustomerPaymentAsync(Guid shiftId, Guid customerPaymentId, PaymentMethod method, decimal amount, decimal rounding, CancellationToken ct);

    /// <summary>A supplier is paid in cash from the drawer (a withdrawal with reason SupplierPayment).</summary>
    Task<Result> RecordSupplierPaymentAsync(Guid shiftId, Guid supplierPaymentId, decimal amount, CancellationToken ct);

    /// <summary>Reverses every movement of a sale, a customer payment or a drawer supplier payment (a void). Only while its shift is open.</summary>
    Task<Result> VoidMovementsAsync(Guid referenceId, CancellationToken ct);
}

public sealed record ShiftOpenedV1(Guid ShiftId, Guid RegisterId, Guid CashierId, decimal OpeningFloat) : IntegrationEvent(ShiftId);

public sealed record CashMovementRecordedV1(Guid ShiftId, string Type, PaymentMethod Method, decimal Amount, Guid? ReferenceId) : IntegrationEvent(ShiftId);

public sealed record ShiftClosedV1(Guid ShiftId, Guid RegisterId, Guid CashierId, int ZNumber, decimal ExpectedCash, decimal CountedCash, decimal Difference)
    : IntegrationEvent(ShiftId);
