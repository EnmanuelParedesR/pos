using Toki.Caja.Contracts;

namespace Toki.Caja.Domain;

/// <param name="Abonos">Customer payments on credit, by method (they are in the card terminal's batch too).</param>
internal sealed record TotalMedio(MedioPago Medio, decimal Cobros, decimal Anulaciones, decimal Reembolsos, decimal Abonos, decimal Neto);

/// <summary>Z report (closed shift) or X report (open shift, for a supervisor).</summary>
internal sealed record ReporteZ(
    Guid TurnoId, int? NumeroZ, string Caja, string Cajero, DateTime AbiertoUtc, DateTime? CerradoUtc, decimal FondoInicial,
    IReadOnlyList<TotalMedio> PorMedio, int Ventas, int VentasAnuladas, decimal AbonosClientes, decimal Redondeo, decimal Entradas, decimal Retiros, decimal Gastos,
    decimal EfectivoEsperado, decimal? EfectivoContado, decimal? Diferencia, IReadOnlyList<ConteoDenominacion>? Conteo, Guid? SupervisorCierreId)
{
    /// <summary>Marks the reversal of a sale's rounding (so it is not counted as a voided payment).</summary>
    public const string ReferenciaRedondeo = "redondeo";

    public static ReporteZ Construir(Turno t, string caja, IReadOnlyList<MovimientoCaja> movimientos, IReadOnlyList<ConteoDenominacion>? conteo)
    {
        static bool AnulaRedondeo(MovimientoCaja m) => m.Tipo == TipoMovimientoCaja.AnulacionCobro && m.Referencia == ReferenciaRedondeo;
        decimal Suma(TipoMovimientoCaja tipo, MedioPago? medio = null) =>
            movimientos.Where(m => m.Tipo == tipo && !AnulaRedondeo(m) && (medio is null || m.Medio == medio)).Sum(m => m.Monto);

        var porMedio = movimientos.Where(m => m.Tipo is TipoMovimientoCaja.Cobro or TipoMovimientoCaja.AnulacionCobro or TipoMovimientoCaja.Reembolso
                or TipoMovimientoCaja.AbonoCliente or TipoMovimientoCaja.AnulacionAbono)
            .Select(m => m.Medio).Distinct().Order()
            .Select(medio =>
            {
                var cobros = Suma(TipoMovimientoCaja.Cobro, medio);
                var anulaciones = Suma(TipoMovimientoCaja.AnulacionCobro, medio);
                var reembolsos = Suma(TipoMovimientoCaja.Reembolso, medio);
                var abonos = Suma(TipoMovimientoCaja.AbonoCliente, medio) - Suma(TipoMovimientoCaja.AnulacionAbono, medio);
                return new TotalMedio(medio, cobros, anulaciones, reembolsos, abonos, cobros - anulaciones - reembolsos + abonos);
            }).ToList();

        var anuladas = movimientos.Where(m => m.Tipo == TipoMovimientoCaja.AnulacionCobro && !AnulaRedondeo(m)).Select(m => m.ReferenciaId).ToHashSet();
        var ventas = movimientos.Where(m => m.Tipo == TipoMovimientoCaja.Cobro).Select(m => m.ReferenciaId).Distinct().Count();
        var redondeo = Suma(TipoMovimientoCaja.Redondeo) - movimientos.Where(AnulaRedondeo).Sum(m => m.Monto);

        return new ReporteZ(t.Id, t.NumeroZ, caja, t.CajeroNombre, t.AbiertoUtc, t.CerradoUtc, t.FondoInicial, porMedio,
            ventas - anuladas.Count, anuladas.Count, Suma(TipoMovimientoCaja.AbonoCliente) - Suma(TipoMovimientoCaja.AnulacionAbono), redondeo,
            Suma(TipoMovimientoCaja.Entrada), Suma(TipoMovimientoCaja.Retiro), Suma(TipoMovimientoCaja.Gasto),
            t.EfectivoEsperado ?? CuadreTurno.EfectivoEsperado(t.FondoInicial, movimientos), t.EfectivoContado, t.Diferencia, conteo, t.SupervisorCierreId);
    }
}
