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
    public List<LineaCompra> Lineas { get; set; } = [];

    public void Recalcular()
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
    public DateOnly? Vence { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Itbis { get; set; }
    public decimal Total { get; set; }
    public bool Anulado { get; set; }
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
    public DateTime CreadoUtc { get; set; }
    public Guid UsuarioId { get; set; }
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
}
