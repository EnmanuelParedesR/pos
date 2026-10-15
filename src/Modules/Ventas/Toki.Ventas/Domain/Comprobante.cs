using Toki.Caja.Contracts;
using Toki.Configuracion.Contracts;
using Toki.Fiscal.Contracts;
using Toki.Inventario.Contracts;
using Toki.SharedKernel;

namespace Toki.Ventas.Domain;

public sealed record LineaImpresion(string Descripcion, decimal Cantidad, UnidadMedida Unidad, decimal Precio, decimal Descuento, decimal Total, string? Nota);
public sealed record ItbisImpresion(string Tasa, decimal Base, decimal Itbis);
public sealed record PagoImpresion(MedioPago Medio, decimal Monto, decimal? Recibido);

/// <summary>Print model of a receipt. The register renders it to ESC/POS for its own printer (58 or 80 mm).</summary>
public sealed record ComprobanteImpresion(
    string Negocio, string? Rnc, string? Direccion, string? Telefono, string TipoComprobante, string ENcf, long Numero, DateTime FechaLocal,
    string Caja, string Cajero, string? Cliente, string? RncCliente, IReadOnlyList<LineaImpresion> Lineas, decimal Subtotal, decimal Descuento,
    IReadOnlyList<ItbisImpresion> Itbis, decimal Total, IReadOnlyList<PagoImpresion> Pagos, decimal Cambio, string CodigoSeguridad, string UrlQr,
    string? Pie, bool Anulada)
{
    internal static ComprobanteImpresion De(Venta v, DatosNegocio n)
    {
        var itbis = v.Lineas.GroupBy(l => l.TasaItbis).OrderBy(g => g.Key)
            .Select(g => new ItbisImpresion(NombreTasa(g.Key), g.Sum(l => l.Total - l.Itbis), g.Sum(l => l.Itbis))).ToList();
        return new ComprobanteImpresion(n.Nombre, n.Rnc, n.Direccion, n.Telefono, NombreTipo(v.TipoComprobante), v.ENcf, v.Numero,
            v.FechaUtc + DiaNegocio.Desfase, v.Caja, v.Cajero, v.ClienteNombre, v.RncCliente,
            v.Lineas.OrderBy(l => l.Orden).Select(l => new LineaImpresion(l.Descripcion, l.Cantidad, l.Unidad, l.PrecioUnitario, l.Descuento, l.Total, l.Nota)).ToList(),
            v.Subtotal, v.Descuento, itbis, v.Total, v.Pagos.Select(p => new PagoImpresion(p.Medio, p.Monto, p.Recibido)).ToList(), v.Cambio,
            v.CodigoSeguridad, v.UrlQr, n.PieRecibo, v.Estado == EstadoVenta.Anulada);
    }

    private static string NombreTipo(TipoEcf t) => t == TipoEcf.CreditoFiscal31 ? "Factura de Crédito Fiscal Electrónica" : "Factura de Consumo Electrónica";

    private static string NombreTasa(TasaItbis t) => t switch
    {
        TasaItbis.General18 => "ITBIS 18%",
        TasaItbis.Reducida16 => "ITBIS 16%",
        TasaItbis.Cero => "ITBIS 0%",
        _ => "Exento",
    };
}
