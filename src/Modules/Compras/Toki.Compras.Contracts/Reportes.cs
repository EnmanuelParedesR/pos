namespace Toki.Compras.Contracts;

/// <summary>One line of the DGII 606 (purchases): an applied purchase or an expense with its NCF. Voided ones are left out.</summary>
/// <param name="TipoId">1 = RNC, 2 = cédula.</param>
/// <param name="TipoBienesServicios">DGII code: 9 (cost of sales) for merchandise, the expense type otherwise.</param>
public sealed record Documento606(string? RncCedula, int TipoId, int TipoBienesServicios, string Ncf, DateOnly Fecha, decimal Monto, decimal Itbis, decimal Total, string Condicion);

/// <summary>Accounts payable of a supplier by days overdue.</summary>
public sealed record CuentaPorPagarSuplidor(Guid SuplidorId, string Suplidor, decimal PorVencer, decimal Vencido1a30, decimal Vencido31a60, decimal Vencido61a90,
    decimal VencidoMas90, decimal Total);

public interface IComprasReportes
{
    Task<IReadOnlyList<Documento606>> Documentos606Async(DateOnly desde, DateOnly hasta, CancellationToken ct);

    /// <summary>Open payables by supplier, largest first.</summary>
    Task<IReadOnlyList<CuentaPorPagarSuplidor>> PorPagarAsync(CancellationToken ct);
}
