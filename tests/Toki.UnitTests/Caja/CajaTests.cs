using Shouldly;
using Toki.Caja.Contracts;
using Toki.Caja.Domain;

namespace Toki.UnitTests.Caja;

public sealed class CajaTests
{
    private static MovimientoCaja M(TipoMovimientoCaja tipo, decimal monto, MedioPago medio = MedioPago.Efectivo, Guid? venta = null) =>
        new() { Tipo = tipo, Monto = monto, Medio = medio, ReferenciaId = venta };

    [Fact]
    public void El_efectivo_esperado_sigue_la_formula_del_cuadre()
    {
        var v1 = Guid.NewGuid();
        MovimientoCaja[] movimientos =
        [
            M(TipoMovimientoCaja.Cobro, 1000, venta: v1),
            M(TipoMovimientoCaja.Cobro, 500, MedioPago.Tarjeta, Guid.NewGuid()),
            M(TipoMovimientoCaja.Entrada, 200),
            M(TipoMovimientoCaja.Retiro, 300),
            M(TipoMovimientoCaja.Gasto, 50),
            M(TipoMovimientoCaja.Reembolso, 100),
            M(TipoMovimientoCaja.AnulacionCobro, 1000, venta: v1),
        ];
        // 2000 + 1000 + 200 − 300 − 50 − 100 − 1000; card money never counts as cash.
        CuadreTurno.EfectivoEsperado(2000, movimientos).ShouldBe(1750m);
    }

    [Fact]
    public void El_conteo_multiplica_cada_denominacion()
    {
        ConteoDenominacion[] conteo = [new(2000, 1), new(500, 3), new(25, 4), new(1, 7)];
        CuadreTurno.Contado(conteo).ShouldBe(3607m);
    }

    [Fact]
    public void El_reporte_Z_separa_medios_y_cuenta_ventas_anuladas()
    {
        var anulada = Guid.NewGuid();
        var turno = new Turno { Id = Guid.NewGuid(), CajeroNombre = "Ana", FondoInicial = 1000 };
        MovimientoCaja[] movimientos =
        [
            M(TipoMovimientoCaja.Cobro, 300, venta: anulada),
            M(TipoMovimientoCaja.Cobro, 200, venta: Guid.NewGuid()),
            M(TipoMovimientoCaja.Cobro, 450, MedioPago.Tarjeta, Guid.NewGuid()),
            M(TipoMovimientoCaja.AnulacionCobro, 300, venta: anulada),
        ];
        var z = ReporteZ.Construir(turno, "Caja 1", movimientos, null, "#7 ABCDEF12");

        z.Ventas.ShouldBe(2);
        z.VentasAnuladas.ShouldBe(1);
        z.PorMedio.Single(p => p.Medio == MedioPago.Efectivo).Neto.ShouldBe(200m);
        z.PorMedio.Single(p => p.Medio == MedioPago.Tarjeta).Neto.ShouldBe(450m);
        z.EfectivoEsperado.ShouldBe(1200m);
    }
}
