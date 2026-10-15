namespace Toki.Ventas.Contracts;

/// <summary>Sales of a business day. Voided sales are left out; returns are subtracted on the day they happen (in every report).</summary>
/// <param name="VentasConItbis">What the day's sales charged, ITBIS included (before returns).</param>
/// <param name="Devoluciones">Returned the same day (ITBIS included), whatever the day of the sale.</param>
/// <param name="NetoSinItbis">Sales minus returns, without ITBIS: the revenue profit is measured on.</param>
/// <param name="Costo">Historical cost of what was sold, minus the cost of returned goods put back on the shelf.</param>
public sealed record VentaDia(DateOnly Dia, int Ventas, decimal VentasConItbis, decimal Descuento, decimal Devoluciones, decimal Itbis, decimal NetoSinItbis,
    decimal Costo, decimal Utilidad);

/// <param name="Cantidad">Base units (pounds or units) sold minus returned in the period.</param>
public sealed record VentaProducto(Guid ProductoId, Guid CategoriaId, string Producto, decimal Cantidad, decimal NetoSinItbis, decimal Costo, decimal Utilidad);

/// <param name="VendidoConItbis">What the cashier's completed sales charged, ITBIS included (returns are not per cashier).</param>
public sealed record VentaCajero(Guid CajeroId, string Cajero, int Ventas, int Anuladas, decimal VendidoConItbis, decimal Descuento);

/// <summary>One line of the DGII 607 (sales), columns in the order of DGII's format. Amounts without ITBIS in <see cref="MontoFacturado"/>.</summary>
/// <param name="TipoId">1 = RNC, 2 = cédula; empty for a consumer receipt without one.</param>
/// <param name="TipoIngreso">1 = operating income.</param>
public sealed record Documento607(
    string? RncCedula, int? TipoId, string Ncf, string? NcfModificado, int TipoIngreso, DateOnly FechaComprobante, DateOnly? FechaRetencion,
    decimal MontoFacturado, decimal ItbisFacturado, decimal ItbisRetenidoTerceros, decimal ItbisPercibido, decimal RetencionRentaTerceros, decimal IsrPercibido,
    decimal ImpuestoSelectivo, decimal OtrosImpuestos, decimal PropinaLegal,
    decimal Efectivo, decimal ChequeTransferencia, decimal Tarjeta, decimal VentaCredito, decimal BonosRegalo, decimal Permuta, decimal OtrasFormas);

public interface IVentasReportes
{
    Task<IReadOnlyList<VentaDia>> PorDiaAsync(DateOnly desde, DateOnly hasta, CancellationToken ct);
    Task<IReadOnlyList<VentaProducto>> PorProductoAsync(DateOnly desde, DateOnly hasta, CancellationToken ct);
    Task<IReadOnlyList<VentaCajero>> PorCajeroAsync(DateOnly desde, DateOnly hasta, CancellationToken ct);
    Task<IReadOnlyList<Documento607>> Documentos607Async(DateOnly desde, DateOnly hasta, CancellationToken ct);
}
