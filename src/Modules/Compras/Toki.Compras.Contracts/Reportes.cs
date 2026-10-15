namespace Toki.Compras.Contracts;

/// <summary>One line of the DGII 606 (purchases), columns in the order of DGII's format: an applied purchase or an expense with its NCF.
/// Voided ones are left out. Amounts without ITBIS, split into services and goods.</summary>
/// <param name="TipoId">1 = RNC, 2 = cédula.</param>
/// <param name="TipoBienesServicios">DGII code: 9 (cost of sales) for merchandise, the expense type otherwise.</param>
/// <param name="FormaPago">DGII code: 1 cash, 2 check/transfer/deposit, 3 card, 4 on credit, 7 mixed.</param>
public sealed record Documento606(
    string? RncCedula, int TipoId, int TipoBienesServicios, string Ncf, string? NcfModificado, DateOnly FechaComprobante, DateOnly? FechaPago,
    decimal MontoServicios, decimal MontoBienes, decimal TotalMonto, decimal ItbisFacturado, decimal ItbisRetenido, decimal ItbisProporcionalidad,
    decimal ItbisAlCosto, decimal ItbisPorAdelantar, decimal ItbisPercibido, int? TipoRetencionIsr, decimal RetencionRenta, decimal IsrPercibido,
    decimal ImpuestoSelectivo, decimal OtrosImpuestos, decimal PropinaLegal, int FormaPago);

/// <summary>Accounts payable of a supplier by days overdue.</summary>
public sealed record CuentaPorPagarSuplidor(Guid SuplidorId, string Suplidor, decimal PorVencer, decimal Vencido1a30, decimal Vencido31a60, decimal Vencido61a90,
    decimal VencidoMas90, decimal Total);

public interface IComprasReportes
{
    Task<IReadOnlyList<Documento606>> Documentos606Async(DateOnly desde, DateOnly hasta, CancellationToken ct);

    /// <summary>Open payables by supplier, largest first.</summary>
    Task<IReadOnlyList<CuentaPorPagarSuplidor>> PorPagarAsync(CancellationToken ct);
}
