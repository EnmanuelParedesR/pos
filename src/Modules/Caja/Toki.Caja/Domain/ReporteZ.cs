using Toki.Caja.Contracts;

namespace Toki.Caja.Domain;

internal sealed record TotalMedio(MedioPago Medio, decimal Cobros, decimal Anulaciones, decimal Reembolsos, decimal Neto);

/// <summary>Z report (closed shift) or X report (open shift, for a supervisor).</summary>
internal sealed record ReporteZ(
    Guid TurnoId, int? NumeroZ, string Caja, string Cajero, DateTime AbiertoUtc, DateTime? CerradoUtc, decimal FondoInicial,
    IReadOnlyList<TotalMedio> PorMedio, int Ventas, int VentasAnuladas, decimal Entradas, decimal Retiros, decimal Gastos,
    decimal EfectivoEsperado, decimal? EfectivoContado, decimal? Diferencia, IReadOnlyList<ConteoDenominacion>? Conteo, Guid? SupervisorCierreId)
{
    public static ReporteZ Construir(Turno t, string caja, IReadOnlyList<MovimientoCaja> movimientos, IReadOnlyList<ConteoDenominacion>? conteo)
    {
        decimal Suma(TipoMovimientoCaja tipo, MedioPago? medio = null) =>
            movimientos.Where(m => m.Tipo == tipo && (medio is null || m.Medio == medio)).Sum(m => m.Monto);

        var porMedio = movimientos.Where(m => m.Tipo is TipoMovimientoCaja.Cobro or TipoMovimientoCaja.AnulacionCobro or TipoMovimientoCaja.Reembolso)
            .Select(m => m.Medio).Distinct().Order()
            .Select(medio =>
            {
                var cobros = Suma(TipoMovimientoCaja.Cobro, medio);
                var anulaciones = Suma(TipoMovimientoCaja.AnulacionCobro, medio);
                var reembolsos = Suma(TipoMovimientoCaja.Reembolso, medio);
                return new TotalMedio(medio, cobros, anulaciones, reembolsos, cobros - anulaciones - reembolsos);
            }).ToList();

        var anuladas = movimientos.Where(m => m.Tipo == TipoMovimientoCaja.AnulacionCobro).Select(m => m.ReferenciaId).ToHashSet();
        var ventas = movimientos.Where(m => m.Tipo == TipoMovimientoCaja.Cobro).Select(m => m.ReferenciaId).Distinct().Count();

        return new ReporteZ(t.Id, t.NumeroZ, caja, t.CajeroNombre, t.AbiertoUtc, t.CerradoUtc, t.FondoInicial, porMedio,
            ventas - anuladas.Count, anuladas.Count, Suma(TipoMovimientoCaja.Entrada), Suma(TipoMovimientoCaja.Retiro), Suma(TipoMovimientoCaja.Gasto),
            t.EfectivoEsperado ?? CuadreTurno.EfectivoEsperado(t.FondoInicial, movimientos), t.EfectivoContado, t.Diferencia, conteo, t.SupervisorCierreId);
    }
}
