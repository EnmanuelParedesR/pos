namespace Toki.CashRegister.Contracts;

/// <summary>A shift for the reconciliation report. Expected and counted cash only exist once the shift is closed (the count is blind).</summary>
public sealed record ResumenTurno(Guid TurnoId, string Caja, string Cajero, DateTime AbiertoUtc, DateTime? CerradoUtc, int? NumeroZ,
    decimal FondoInicial, decimal? EfectivoEsperado, decimal? EfectivoContado, decimal? Diferencia);

public interface ICajaReportes
{
    /// <summary>Shifts opened in the business-day range, oldest first.</summary>
    Task<IReadOnlyList<ResumenTurno>> TurnosAsync(DateOnly desde, DateOnly hasta, CancellationToken ct);
}
