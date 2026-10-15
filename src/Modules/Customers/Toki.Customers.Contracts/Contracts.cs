using Toki.SharedKernel;

namespace Toki.Customers.Contracts;

public enum PriceLevel { Retail, Wholesale }

/// <param name="Debt">Unpaid credit (on account) right now.</param>
/// <param name="CreditBalance">Money the store owes the customer (from returns), usable as payment.</param>
public sealed record CustomerForSale(Guid Id, string Name, string? TaxId, bool FiscalCredit, PriceLevel PriceLevel,
    decimal CreditLimit, decimal Debt, decimal CreditBalance, bool IsActive);

/// <summary>Customer account operations called inside a sale's transaction.</summary>
public interface ICustomerAccounts
{
    Task<CustomerForSale?> GetAsync(Guid customerId, CancellationToken ct);

    /// <summary>Charges a credit sale. Locks the customer so two registers cannot both pass the limit.
    /// Fails with Customers.CreditLimitExceeded unless <paramref name="allowOverLimit"/>.</summary>
    Task<Result> ChargeOnAccountAsync(Guid customerId, Guid saleId, string document, decimal amount, bool allowOverLimit, CancellationToken ct);

    /// <summary>Pays part of a sale with the customer's credit balance.</summary>
    Task<Result> UseCreditBalanceAsync(Guid customerId, Guid saleId, decimal amount, CancellationToken ct);

    /// <summary>A voided sale: its on-account charge is cancelled (anything already paid on it becomes credit balance) and used balance comes back.</summary>
    Task VoidSaleAsync(Guid saleId, CancellationToken ct);

    /// <summary>A return credited to the customer: first it pays down that sale's unpaid charge, the rest becomes credit balance.</summary>
    Task CreditReturnAsync(Guid customerId, Guid saleId, Guid returnId, decimal amount, CancellationToken ct);
}

public sealed record OnAccountChargeRecordedV1(Guid CustomerId, Guid SaleId, decimal Amount) : IntegrationEvent(CustomerId);

public sealed record CustomerPaymentRecordedV1(Guid CustomerId, Guid CustomerPaymentId, decimal Amount) : IntegrationEvent(CustomerId);
