namespace Abastalia.Customers.Contracts;

/// <summary>Unpaid credit of a customer by the age of each charge (business days).</summary>
public sealed record CustomerReceivable(Guid CustomerId, string Customer, decimal Days0To30, decimal Days31To60, decimal Days61To90, decimal Over90,
    decimal Total, decimal CreditLimit);

public interface ICustomerReports
{
    /// <summary>Open credit by customer, largest first.</summary>
    Task<IReadOnlyList<CustomerReceivable>> ReceivablesAsync(CancellationToken ct);
}
