namespace Toki.Ventas.Contracts;

/// <summary>Net sales of a day: voided sales excluded, returns subtracted. Cost is the historical cost saved on each line.</summary>
public sealed record VentaDia(DateOnly Dia, int Ventas, decimal Bruto, decimal Descuento, decimal Itbis, decimal Devoluciones, decimal Neto, decimal Costo, decimal Utilidad);

public sealed record VentaProducto(Guid ProductoId, Guid CategoriaId, string Producto, decimal Cantidad, decimal Neto, decimal Costo, decimal Utilidad);

public sealed record VentaCajero(Guid CajeroId, string Cajero, int Ventas, int Anuladas, decimal Neto, decimal Descuento);

/// <summary>One line of the DGII 607 (sales) report: an issued sale or credit note.</summary>
public sealed record Documento607(
    string? RncCedula, string ENcf, string? ENcfModificado, DateOnly Fecha, decimal Monto, decimal Itbis,
    decimal Efectivo, decimal TarjetaOTransferencia, decimal Credito, decimal Otros);

public interface IVentasReportes
{
    Task<IReadOnlyList<VentaDia>> PorDiaAsync(DateOnly desde, DateOnly hasta, CancellationToken ct);
    Task<IReadOnlyList<VentaProducto>> PorProductoAsync(DateOnly desde, DateOnly hasta, CancellationToken ct);
    Task<IReadOnlyList<VentaCajero>> PorCajeroAsync(DateOnly desde, DateOnly hasta, CancellationToken ct);
    Task<IReadOnlyList<Documento607>> Documentos607Async(DateOnly desde, DateOnly hasta, CancellationToken ct);
}
