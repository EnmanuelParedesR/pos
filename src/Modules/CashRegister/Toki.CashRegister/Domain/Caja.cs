using Toki.CashRegister.Contracts;
using Toki.SharedKernel;

namespace Toki.CashRegister.Domain;

internal sealed class CajaFisica
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = "";
    public bool Activa { get; set; } = true;
    /// <summary>The paired device this register belongs to (set on its first shift). Its shifts are only operated from there.</summary>
    public Guid? DispositivoId { get; set; }
}

internal enum EstadoTurno { Abierto, Cerrado }

/// <summary>A shift: one physical register + one cashier, from the opening float to the blind count.</summary>
internal sealed class Turno
{
    public Guid Id { get; set; }
    public Guid CajaId { get; set; }
    public Guid CajeroId { get; set; }
    public string CajeroNombre { get; set; } = "";
    public DateTime AbiertoUtc { get; set; }
    public decimal FondoInicial { get; set; }
    public EstadoTurno Estado { get; set; }
    public DateTime? CerradoUtc { get; set; }
    public decimal? EfectivoEsperado { get; set; }
    public decimal? EfectivoContado { get; set; }
    public decimal? Diferencia { get; set; }
    public Guid? SupervisorCierreId { get; set; }
    /// <summary>JSON of the denomination count.</summary>
    public string? Conteo { get; set; }
    public int? NumeroZ { get; set; }
    /// <summary>Head of the audit chain at the closing, as printed on the Z.</summary>
    public string? CadenaAuditoria { get; set; }
    public int Version { get; set; }
}

/// <summary>Redondeo: centavos the store keeps because change is given in whole pesos (there are no coins below RD$ 1).</summary>
internal enum TipoMovimientoCaja { Cobro, AnulacionCobro, Reembolso, Entrada, Retiro, Gasto, AbonoCliente, Redondeo, AnulacionAbono }

internal enum MotivoMovimiento { CambioAdicional, Recogida, DepositoBanco, PagoSuplidor, GastoMenor, Otro }

/// <summary>Cash-book row. Never edited: a mistake is fixed with another movement.</summary>
internal sealed class MovimientoCaja
{
    public long Secuencia { get; set; }
    public Guid Id { get; set; }
    public Guid TurnoId { get; set; }
    public TipoMovimientoCaja Tipo { get; set; }
    public MedioPago Medio { get; set; }
    /// <summary>Always positive; the type says whether money comes in or goes out.</summary>
    public decimal Monto { get; set; }
    public MotivoMovimiento? Motivo { get; set; }
    public string? Nota { get; set; }
    public Guid? ReferenciaId { get; set; }
    public string? Referencia { get; set; }
    public Guid? UsuarioId { get; set; }
    public Guid? SupervisorId { get; set; }
    public DateTime FechaUtc { get; set; }

    public bool Entra => Tipo is TipoMovimientoCaja.Cobro or TipoMovimientoCaja.Entrada or TipoMovimientoCaja.AbonoCliente or TipoMovimientoCaja.Redondeo;
}

/// <summary>Dominican peso bills and coins for the closing count.</summary>
internal static class Denominaciones
{
    public static readonly IReadOnlyList<decimal> Dop = [2000, 1000, 500, 200, 100, 50, 25, 10, 5, 1];
}

internal sealed record ConteoDenominacion(decimal Denominacion, int Cantidad);

/// <summary>Totals of a shift: the expected cash formula and the Z report lines.</summary>
internal static class CuadreTurno
{
    /// <summary>Expected cash = float + cash sales − cash refunds − voided cash + cash in − withdrawals − petty expenses.</summary>
    public static decimal EfectivoEsperado(decimal fondo, IEnumerable<MovimientoCaja> movimientos) =>
        fondo + movimientos.Where(m => m.Medio == MedioPago.Efectivo).Sum(m => m.Entra ? m.Monto : -m.Monto);

    public static decimal Contado(IEnumerable<ConteoDenominacion> conteo) => conteo.Sum(c => c.Denominacion * c.Cantidad);
}

internal static class CajaErrors
{
    public static readonly Error CajaNoEncontrada = Error.NotFound("Caja.CajaNoEncontrada", "La caja no existe o está desactivada.");
    public static readonly Error TurnoNoEncontrado = Error.NotFound("Caja.TurnoNoEncontrado", "El turno no existe.");
    public static readonly Error CajaOcupada = Error.Conflict("Caja.CajaOcupada", "Esta caja ya tiene un turno abierto.");
    public static readonly Error CajeroConTurno = Error.Conflict("Caja.CajeroConTurno", "Este cajero ya tiene un turno abierto en otra caja.");
    public static readonly Error TurnoCerrado = Error.Conflict("Caja.TurnoCerrado", "El turno ya está cerrado.");
    public static readonly Error TurnoAjeno = Error.Forbidden("Caja.TurnoAjeno", "Este turno es de otro cajero.");
    public static readonly Error OtroDispositivo = Error.Forbidden("Caja.OtroDispositivo", "Esta caja está asignada a otro equipo.");
    public static readonly Error DenominacionInvalida = Error.Validation("Caja.DenominacionInvalida", "Esa denominación no existe.");
}
