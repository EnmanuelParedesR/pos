using Toki.Caja.Contracts;
using Toki.Clientes.Contracts;
using Toki.SharedKernel;

namespace Toki.Clientes.Domain;

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
    public int DiasCredito { get; set; } = 30;
    public bool Activo { get; set; } = true;
    public DateTime CreadoUtc { get; set; }
}

/// <summary>A credit sale. Its balance is Monto minus the payments applied to it.</summary>
internal sealed class CargoFiado
{
    public Guid Id { get; set; }
    public Guid ClienteId { get; set; }
    public Guid VentaId { get; set; }
    public string Documento { get; set; } = "";
    public DateTime FechaUtc { get; set; }
    public DateOnly Vence { get; set; }
    public decimal Monto { get; set; }
    public bool Anulado { get; set; }
}

internal enum OrigenAbono { Caja, Devolucion }

internal sealed class AbonoFiado
{
    public Guid Id { get; set; }
    public Guid ClienteId { get; set; }
    public DateTime FechaUtc { get; set; }
    public decimal Monto { get; set; }
    public OrigenAbono Origen { get; set; }
    public MedioPago? Medio { get; set; }
    public Guid? TurnoId { get; set; }
    public Guid? ReferenciaId { get; set; }
    public string? Referencia { get; set; }
    public Guid? UsuarioId { get; set; }
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

    public static Error LimiteExcedido(decimal disponible) =>
        Error.Forbidden("Clientes.LimiteExcedido", $"El fiado pasa del límite del cliente (disponible RD$ {disponible:N2}). Necesita un supervisor.");
}
