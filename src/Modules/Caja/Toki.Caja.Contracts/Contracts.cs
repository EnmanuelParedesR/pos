using Toki.SharedKernel;

namespace Toki.Caja.Contracts;

/// <summary>How a customer pays. Only cash gives change; Fiado and SaldoFavor need a customer.</summary>
public enum MedioPago { Efectivo, Tarjeta, Transferencia, Fiado, SaldoFavor }

public sealed record TurnoAbierto(Guid TurnoId, Guid CajaId, string Caja, Guid CajeroId);

public sealed record CobroMedio(MedioPago Medio, decimal Monto, string? Referencia);

/// <summary>Cash-drawer operations called inside a sale's transaction.</summary>
public interface ICajaOperaciones
{
    /// <summary>The shift if it is open and the caller may sell on it (its cashier, or someone who can approve closings).</summary>
    Task<Result<TurnoAbierto>> ObtenerTurnoAbiertoAsync(Guid turnoId, CancellationToken ct);

    Task RegistrarCobroAsync(Guid turnoId, Guid ventaId, IReadOnlyList<CobroMedio> pagos, CancellationToken ct);

    /// <summary>Money given back to a customer (a return).</summary>
    Task<Result> RegistrarReembolsoAsync(Guid turnoId, Guid referenciaId, MedioPago medio, decimal monto, CancellationToken ct);

    /// <summary>Reverses every payment of a sale in its shift (a void).</summary>
    Task<Result> AnularCobrosAsync(Guid ventaId, CancellationToken ct);
}

public sealed record TurnoAbiertoV1(Guid TurnoId, Guid CajaId, Guid CajeroId, decimal FondoInicial) : IntegrationEvent(TurnoId);

public sealed record MovimientoCajaRegistradoV1(Guid TurnoId, string Tipo, MedioPago Medio, decimal Monto, Guid? ReferenciaId) : IntegrationEvent(TurnoId);

public sealed record TurnoCerradoV1(Guid TurnoId, Guid CajaId, Guid CajeroId, int NumeroZ, decimal EfectivoEsperado, decimal EfectivoContado, decimal Diferencia)
    : IntegrationEvent(TurnoId);
