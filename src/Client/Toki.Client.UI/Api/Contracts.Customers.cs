namespace Toki.Client.UI.Api;

// Customers and credit (TK-186, TK-121). Same rules as Contracts.cs: camelCase JSON, enums by name.

/// <summary>A row of <c>GET /api/customers</c>, with what the edit form needs to send it back whole.</summary>
public sealed record CustomerRow(Guid Id, string Name, string? TaxId, string? Phone, PriceLevel PriceLevel, decimal CreditLimit, bool IsActive,
    string? Address, bool FiscalCredit);
public sealed record SaveCustomer(string Name, string? TaxId, string? Phone, string? Address, bool FiscalCredit, PriceLevel PriceLevel,
    decimal CreditLimit, bool IsActive);

/// <param name="Type">Already in Spanish (Fiado, Abono, Abono anulado…).</param>
/// <param name="PaymentId">Only on a live payment taken at a register: the one that can be voided.</param>
public sealed record StatementLine(DateTime DateUtc, string Type, string? Document, decimal Charge, decimal Payment, decimal Debt, Guid? PaymentId);
public sealed record CustomerStatement(Guid CustomerId, string Name, decimal Debt, decimal CreditBalance, decimal CreditLimit, StatementLine[] Lines);

public sealed record RecordCustomerPayment(Guid CustomerPaymentId, Guid ShiftId, decimal Amount, PaymentMethod Method, string? Reference);
public sealed record CustomerPaymentRecorded(Guid CustomerPaymentId, decimal Applied, decimal RemainingDebt);

/// <summary>A row of <c>GET /api/reports/receivables</c>: what a customer owes by the age of each charge.</summary>
public sealed record ReceivableAging(Guid CustomerId, string Customer, decimal Days0To30, decimal Days31To60, decimal Days61To90, decimal Over90,
    decimal Total, decimal CreditLimit);

public sealed record SaveRole(string Name, string[] Permissions);
