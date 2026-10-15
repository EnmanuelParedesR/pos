namespace Toki.Customers.Contracts;

/// <summary>Unpaid credit of a customer by the age of each charge (business days).</summary>
public sealed record CuentaPorCobrarCliente(Guid ClienteId, string Cliente, decimal De0a30, decimal De31a60, decimal De61a90, decimal MasDe90,
    decimal Total, decimal LimiteCredito);

public interface IClientesReportes
{
    /// <summary>Open credit by customer, largest first.</summary>
    Task<IReadOnlyList<CuentaPorCobrarCliente>> PorCobrarAsync(CancellationToken ct);
}
