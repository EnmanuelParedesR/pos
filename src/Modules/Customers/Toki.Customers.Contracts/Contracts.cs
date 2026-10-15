using Toki.SharedKernel;

namespace Toki.Customers.Contracts;

public enum NivelPrecioCliente { Detalle, Mayorista }

/// <param name="Deuda">Unpaid credit (fiado) right now.</param>
/// <param name="SaldoFavor">Money the store owes the customer (from returns), usable as payment.</param>
public sealed record ClienteParaVenta(Guid Id, string Nombre, string? RncCedula, bool CreditoFiscal, NivelPrecioCliente Nivel,
    decimal LimiteCredito, decimal Deuda, decimal SaldoFavor, bool Activo);

/// <summary>Customer account operations called inside a sale's transaction.</summary>
public interface ICuentasClientes
{
    Task<ClienteParaVenta?> ObtenerAsync(Guid clienteId, CancellationToken ct);

    /// <summary>Charges a credit sale. Locks the customer so two registers cannot both pass the limit.
    /// Fails with Clientes.LimiteExcedido unless <paramref name="permitirExceso"/>.</summary>
    Task<Result> CargarFiadoAsync(Guid clienteId, Guid ventaId, string documento, decimal monto, bool permitirExceso, CancellationToken ct);

    /// <summary>Pays part of a sale with the customer's balance in favor.</summary>
    Task<Result> UsarSaldoFavorAsync(Guid clienteId, Guid ventaId, decimal monto, CancellationToken ct);

    /// <summary>A voided sale: its credit charge is cancelled (anything already paid on it becomes balance in favor) and used balance comes back.</summary>
    Task AnularVentaAsync(Guid ventaId, CancellationToken ct);

    /// <summary>A return credited to the customer: first it pays down that sale's unpaid charge, the rest becomes balance in favor.</summary>
    Task AcreditarDevolucionAsync(Guid clienteId, Guid ventaId, Guid devolucionId, decimal monto, CancellationToken ct);
}

public sealed record CargoFiadoRegistradoV1(Guid ClienteId, Guid VentaId, decimal Monto) : IntegrationEvent(ClienteId);

public sealed record AbonoFiadoRegistradoV1(Guid ClienteId, Guid AbonoId, decimal Monto) : IntegrationEvent(ClienteId);
