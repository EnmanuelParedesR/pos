using Toki.SharedKernel;

namespace Toki.Compras.Domain;

internal sealed class Suplidor
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Rnc { get; set; }
    public string? Telefono { get; set; }
    public string? Contacto { get; set; }
    /// <summary>Default credit days for its invoices.</summary>
    public int DiasCredito { get; set; }
    public bool Activo { get; set; } = true;
}

internal enum EstadoCompra { Borrador, Aplicada, Anulada }

public enum Condicion { Contado, Credito }

/// <summary>A supplier invoice for merchandise. Draft → applied (stock in, average cost, payable if on credit) → voided.</summary>
internal sealed class Compra
{
    public long Numero { get; set; }
    public Guid Id { get; set; }
    public Guid SuplidorId { get; set; }
    public string SuplidorNombre { get; set; } = "";
    public string? RncSuplidor { get; set; }
    public string Ncf { get; set; } = "";
    public DateOnly FechaFactura { get; set; }
    public Condicion Condicion { get; set; }
    /// <summary>How a cash (contado) invoice was paid, for the 606 payment form. Unknown stays empty there.</summary>
    public MedioPagoSuplidor? Medio { get; set; }
    public DateOnly? Vence { get; set; }
    public Guid? AlmacenId { get; set; }
    public EstadoCompra Estado { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Itbis { get; set; }
    public decimal Total { get; set; }
    public string? Notas { get; set; }
    public DateTime CreadaUtc { get; set; }
    public DateTime? AplicadaUtc { get; set; }
    public DateTime? AnuladaUtc { get; set; }
    public string? MotivoAnulacion { get; set; }
    public Guid UsuarioId { get; set; }
    /// <summary>Bumped by an atomic update before an edit or a void reads the purchase: it locks the row, so they run one at a time.</summary>
    public int Version { get; set; }
    public List<LineaCompra> Lineas { get; set; } = [];

    /// <summary>The invoice's own totals may differ from Toki's line math by centavos (suppliers round ITBIS on the subtotal).</summary>
    public const decimal ToleranciaFactura = 1m;

    /// <param name="itbisFactura">ITBIS printed on the invoice; when given (within RD$ 1 of the computed one) it is the one paid and reported in the 606.</param>
    /// <param name="totalFactura">Total printed on the invoice, same rule.</param>
    public bool Recalcular(decimal? itbisFactura = null, decimal? totalFactura = null)
    {
        foreach (var l in Lineas)
        {
            l.Subtotal = Dinero.Redondear(l.Cantidad * l.CostoUnitario);
            // Supplier costs come before ITBIS, so it is added on top (unlike shelf prices, which include it).
            l.Itbis = Dinero.Redondear(l.Subtotal * SharedKernel.Itbis.Porcentaje(l.TasaItbis));
        }
        Subtotal = Lineas.Sum(l => l.Subtotal);
        Itbis = Lineas.Sum(l => l.Itbis);
        Total = Subtotal + Itbis;
        if (Math.Abs((itbisFactura ?? Itbis) - Itbis) > ToleranciaFactura || Math.Abs((totalFactura ?? Total) - Total) > ToleranciaFactura) return false;
        Itbis = itbisFactura ?? Itbis;
        Total = totalFactura ?? Total;
        Subtotal = Total - Itbis;
        return true;
    }
}

/// <param name="CostoUnitario">Cost of one purchased unit (a box when <see cref="Factor"/> is 24), before ITBIS.</param>
internal sealed class LineaCompra
{
    public Guid Id { get; set; }
    public Guid CompraId { get; set; }
    public int Orden { get; set; }
    public Guid ProductoId { get; set; }
    public string Descripcion { get; set; } = "";
    public decimal Cantidad { get; set; }
    /// <summary>Base units per purchased unit.</summary>
    public decimal Factor { get; set; } = 1;
    public decimal CostoUnitario { get; set; }
    public TasaItbis TasaItbis { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Itbis { get; set; }
}

/// <summary>DGII 606 classification of an expense.</summary>
public enum TipoGasto
{
    GastosPersonal = 1, TrabajosSuministrosServicios = 2, Arrendamientos = 3, GastosActivoFijo = 4, GastosRepresentacion = 5,
    OtrasDeduccionesAdmitidas = 6, GastosFinancieros = 7, GastosExtraordinarios = 8, CostoDeVenta = 9, AdquisicionesActivos = 10, GastosSeguros = 11,
}

/// <summary>A supplier invoice without merchandise (electricity, rent, transport). Feeds the 606.</summary>
internal sealed class Gasto
{
    public Guid Id { get; set; }
    public Guid SuplidorId { get; set; }
    public string SuplidorNombre { get; set; } = "";
    public string? RncSuplidor { get; set; }
    public string Ncf { get; set; } = "";
    public DateOnly Fecha { get; set; }
    public string Concepto { get; set; } = "";
    public TipoGasto Tipo { get; set; }
    public Condicion Condicion { get; set; }
    /// <summary>How a cash (contado) expense was paid, for the 606 payment form.</summary>
    public MedioPagoSuplidor? Medio { get; set; }
    public DateOnly? Vence { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Itbis { get; set; }
    public decimal Total { get; set; }
    public bool Anulado { get; set; }
    public string? MotivoAnulacion { get; set; }
    public DateTime CreadoUtc { get; set; }
    public Guid UsuarioId { get; set; }
}

internal enum TipoDocumento { Compra, Gasto }

/// <summary>An open payable: created when a credit purchase is applied or a credit expense is recorded. Its balance is
/// always Total minus the sum of applied payments (never a stored, hand-edited number).</summary>
internal sealed class CuentaPorPagar
{
    public Guid Id { get; set; }
    public Guid SuplidorId { get; set; }
    public TipoDocumento Tipo { get; set; }
    public Guid DocumentoId { get; set; }
    public string Ncf { get; set; } = "";
    public DateOnly Fecha { get; set; }
    public DateOnly Vence { get; set; }
    public decimal Total { get; set; }
    public bool Anulada { get; set; }
}

public enum MedioPagoSuplidor { Efectivo, Transferencia, Cheque, Tarjeta }

internal sealed class PagoSuplidor
{
    public Guid Id { get; set; }
    public Guid SuplidorId { get; set; }
    public DateOnly Fecha { get; set; }
    public decimal Monto { get; set; }
    public MedioPagoSuplidor Medio { get; set; }
    public string? Referencia { get; set; }
    /// <summary>Set when the cash came out of a register's drawer during this shift.</summary>
    public Guid? TurnoId { get; set; }
    public DateTime CreadoUtc { get; set; }
    public Guid UsuarioId { get; set; }
    /// <summary>A voided payment stays for history; its applications no longer count (the invoices are open again).</summary>
    public bool Anulado { get; set; }
    public string? MotivoAnulacion { get; set; }
    public List<AplicacionPago> Aplicaciones { get; set; } = [];
}

internal sealed class AplicacionPago
{
    public Guid PagoId { get; set; }
    public Guid CuentaId { get; set; }
    public decimal Monto { get; set; }
}

internal static class Ncf
{
    /// <summary>Old NCF (B01 + 8 digits, 11 chars) or e-NCF (E31 + 10 digits, 13 chars).</summary>
    public static bool EsValido(string? ncf) =>
        ncf is { Length: 11 or 13 } && (ncf[0] is 'B' or 'E') && ncf.Skip(1).All(char.IsAsciiDigit);
}

internal static class ComprasErrors
{
    public static readonly Error SuplidorNoEncontrado = Error.NotFound("Compras.SuplidorNoEncontrado", "El suplidor no existe o está desactivado.");
    public static readonly Error CompraNoEncontrada = Error.NotFound("Compras.CompraNoEncontrada", "La compra no existe.");
    public static readonly Error NoEsBorrador = Error.Conflict("Compras.NoEsBorrador", "Solo se puede cambiar o aplicar una compra en borrador.");
    public static readonly Error NcfRepetido = Error.Conflict("Compras.NcfRepetido", "Ya se registró ese NCF de ese suplidor.");
    public static readonly Error ConPagos = Error.Conflict("Compras.ConPagos", "La compra tiene pagos aplicados; no se puede anular.");
    public static readonly Error PagoMayorQueDeuda = Error.Validation("Compras.PagoMayorQueDeuda", "El pago es mayor que lo que se le debe al suplidor.");
    public static readonly Error ProductoNoEncontrado = Error.NotFound("Compras.ProductoNoEncontrado", "Un producto de la compra no existe.");
    public static readonly Error YaAnulado = Error.Conflict("Compras.YaAnulada", "Ya está anulado.");
    public static readonly Error SuplidorSinRnc = Error.Validation("Compras.SuplidorSinRnc", "El suplidor necesita RNC o cédula para registrar facturas con NCF (606).");
    public static readonly Error TotalFacturaLejos = Error.Validation("Compras.TotalFacturaLejos",
        $"El ITBIS o el total de la factura difiere en más de RD$ {Compra.ToleranciaFactura:N0} de lo calculado. Revise cantidades y costos.");
    public static readonly Error PagoNoEncontrado = Error.NotFound("Compras.PagoNoEncontrado", "El pago no existe.");
    public static readonly Error GastoNoEncontrado = Error.NotFound("Compras.GastoNoEncontrado", "El gasto no existe.");
}

/// <summary>Invoice and payment dates: required, and never later than tomorrow (a typo in the year would break statements and aging).</summary>
internal static class FechaDocumento
{
    public static bool EsValida(DateOnly fecha, TimeProvider reloj) => fecha != default && fecha <= DiaNegocio.De(reloj.GetUtcNow().UtcDateTime).AddDays(1);

    public const string Mensaje = "La fecha es obligatoria y no puede ser futura.";
}
