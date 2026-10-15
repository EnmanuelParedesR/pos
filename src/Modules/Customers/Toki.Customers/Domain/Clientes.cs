using Toki.CashRegister.Contracts;
using Toki.Customers.Contracts;
using Toki.SharedKernel;

namespace Toki.Customers.Domain;

internal sealed class Cliente
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? RncCedula { get; set; }
    public string? Telefono { get; set; }
    public string? Direccion { get; set; }
    /// <summary>Asks for a credit fiscal receipt (31) by default.</summary>
    public bool CreditoFiscal { get; set; }
    public NivelPrecioCliente Nivel { get; set; }
    /// <summary>Most the customer may owe. Zero means no credit.</summary>
    public decimal LimiteCredito { get; set; }
    public bool Activo { get; set; } = true;
    public DateTime CreadoUtc { get; set; }
    /// <summary>Bumped to lock the row: limit checks, payments and balance use run one at a time per customer.</summary>
    public int Version { get; set; }
}

/// <summary>A credit sale. Its balance is Monto minus the live payments applied to it. Aging uses its date.</summary>
internal sealed class CargoFiado
{
    public Guid Id { get; set; }
    public Guid ClienteId { get; set; }
    public Guid VentaId { get; set; }
    public string Documento { get; set; } = "";
    public DateTime FechaUtc { get; set; }
    public decimal Monto { get; set; }
    public bool Anulado { get; set; }
    public DateTime? AnuladoUtc { get; set; }
    /// <summary>What had been paid on it when the sale was voided: that money went to other charges or to balance in favor.</summary>
    public decimal PagadoAlAnular { get; set; }
}

/// <summary>Caja: paid at the register. Devolucion: a return credited to the account. AnulacionVenta: what was paid on a voided sale, moved to other charges.</summary>
internal enum OrigenAbono { Caja, Devolucion, AnulacionVenta }

internal sealed class AbonoFiado
{
    public Guid Id { get; set; }
    public Guid ClienteId { get; set; }
    public DateTime FechaUtc { get; set; }
    /// <summary>What goes to the debt (cash centavos kept as rounding are not part of it).</summary>
    public decimal Monto { get; set; }
    public OrigenAbono Origen { get; set; }
    public MedioPago? Medio { get; set; }
    public Guid? TurnoId { get; set; }
    public Guid? ReferenciaId { get; set; }
    public string? Referencia { get; set; }
    public Guid? UsuarioId { get; set; }
    /// <summary>A voided payment stays for history; its applications no longer count.</summary>
    public bool Anulado { get; set; }
    public DateTime? AnuladoUtc { get; set; }
    public string? MotivoAnulacion { get; set; }
    public List<AplicacionAbono> Aplicaciones { get; set; } = [];
}

internal sealed class AplicacionAbono
{
    public Guid AbonoId { get; set; }
    public Guid CargoId { get; set; }
    public decimal Monto { get; set; }
}

internal enum TipoMovimientoSaldo { Devolucion, UsoEnVenta, AnulacionUso, CargoAnuladoPagado }

/// <summary>Balance in favor ledger: positive adds, negative uses. The balance is always the sum.</summary>
internal sealed class MovimientoSaldo
{
    public long Secuencia { get; set; }
    public Guid Id { get; set; }
    public Guid ClienteId { get; set; }
    public TipoMovimientoSaldo Tipo { get; set; }
    public decimal Monto { get; set; }
    public Guid ReferenciaId { get; set; }
    public DateTime FechaUtc { get; set; }
}

internal static class ClientesErrors
{
    public static readonly Error ClienteNoEncontrado = Error.NotFound("Clientes.ClienteNoEncontrado", "El cliente no existe o está desactivado.");
    public static readonly Error SinSaldoFavor = Error.Conflict("Clientes.SinSaldoFavor", "El cliente no tiene ese saldo a favor.");
    public static readonly Error AbonoMayorQueDeuda = Error.Validation("Clientes.AbonoMayorQueDeuda", "El abono es mayor que la deuda del cliente.");
    public static readonly Error RncDuplicado = Error.Conflict("Clientes.RncDuplicado", "Ya hay un cliente con ese RNC o cédula.");
    public static readonly Error SinPermisoCredito = Error.Forbidden("Clientes.SinPermisoCredito",
        "Dar crédito, precio mayorista o reactivar un cliente necesita el permiso de fiado.");
    public static readonly Error AbonoNoEncontrado = Error.NotFound("Clientes.AbonoNoEncontrado", "El abono no existe o no se cobró en caja.");
    public static readonly Error AbonoYaAnulado = Error.Conflict("Clientes.AbonoYaAnulado", "El abono ya está anulado.");
    public static readonly Error AbonoConFacturaAnulada = Error.Conflict("Clientes.AbonoConFacturaAnulada",
        "Ese abono pagó una venta que ya se anuló; su dinero ya se movió. Corrija con un nuevo movimiento.");

    public static Error LimiteExcedido(decimal disponible) =>
        Error.Forbidden("Clientes.LimiteExcedido", $"El fiado pasa del límite del cliente (disponible RD$ {disponible:N2}). Necesita un supervisor.");
}
