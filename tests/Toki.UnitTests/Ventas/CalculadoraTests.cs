using Shouldly;
using Toki.Caja.Contracts;
using Toki.SharedKernel;
using Toki.Ventas.Domain;

namespace Toki.UnitTests.Ventas;

public sealed class CalculadoraTests
{
    private static Totales Calcular(Descuento? global, params LineaEntrada[] lineas) => Calculadora.Calcular(lineas, global).Value;

    [Fact]
    public void El_itbis_se_extrae_del_precio_que_ya_lo_incluye()
    {
        var t = Calcular(null, new LineaEntrada(118m, 2, TasaItbis.General18, null));
        t.Total.ShouldBe(236m);
        t.Itbis.ShouldBe(36m);
        t.Subtotal.ShouldBe(200m);
    }

    [Fact]
    public void Cada_linea_usa_su_tasa()
    {
        var t = Calcular(null,
            new LineaEntrada(118m, 1, TasaItbis.General18, null),
            new LineaEntrada(116m, 1, TasaItbis.Reducida16, null),
            new LineaEntrada(50m, 1, TasaItbis.Exento, null));
        t.Itbis.ShouldBe(34m);
        t.Total.ShouldBe(284m);
    }

    [Fact]
    public void La_cantidad_en_libras_se_redondea_por_linea()
    {
        // 2.355 lb × 165.00 = 388.575 → 388.58
        var t = Calcular(null, new LineaEntrada(165m, 2.355m, TasaItbis.Exento, null));
        t.Total.ShouldBe(388.58m);
    }

    [Fact]
    public void El_descuento_global_se_reparte_y_el_resto_del_redondeo_va_a_la_linea_mayor()
    {
        var t = Calcular(new Descuento(false, 10m),
            new LineaEntrada(100m, 1, TasaItbis.General18, null),
            new LineaEntrada(100m, 1, TasaItbis.General18, null),
            new LineaEntrada(100m, 1, TasaItbis.General18, null));
        t.Descuento.ShouldBe(10m);
        t.Lineas.Sum(l => l.DescuentoGlobal).ShouldBe(10m);
        t.Total.ShouldBe(290m);
    }

    [Fact]
    public void Descuento_por_linea_en_porcentaje()
    {
        var t = Calcular(null, new LineaEntrada(200m, 1, TasaItbis.General18, new Descuento(true, 15m)));
        t.Descuento.ShouldBe(30m);
        t.Total.ShouldBe(170m);
        t.PorcentajeDescuento.ShouldBe(15m);
    }

    [Fact]
    public void Un_descuento_mayor_que_el_importe_es_un_error()
    {
        var r = Calculadora.Calcular([new LineaEntrada(50m, 1, TasaItbis.General18, new Descuento(false, 60m))], null);
        r.Error!.Code.ShouldBe("Ventas.DescuentoInvalido");
    }

    [Fact]
    public void Una_etiqueta_de_precio_fija_el_importe_de_la_linea()
    {
        var t = Calcular(null, new LineaEntrada(165m, 2.35m, TasaItbis.Exento, null, MontoFijo: 387.75m));
        t.Total.ShouldBe(387.75m);
    }
}

public sealed class PagosTests
{
    [Fact]
    public void El_efectivo_da_la_devuelta()
    {
        var r = Pagos.Aplicar(376.75m, [new PagoEntrada(MedioPago.Efectivo, 500m, null)]).Value;
        r.Cambio.ShouldBe(123.25m);
        r.Pagos.Single().Monto.ShouldBe(376.75m);
        r.Pagos.Single().Recibido.ShouldBe(500m);
    }

    [Fact]
    public void Pago_mixto_tarjeta_y_efectivo()
    {
        var r = Pagos.Aplicar(1000m, [new PagoEntrada(MedioPago.Tarjeta, 600m, "AUT123"), new PagoEntrada(MedioPago.Efectivo, 500m, null)]).Value;
        r.Cambio.ShouldBe(100m);
        r.Pagos.Single(p => p.Medio == MedioPago.Efectivo).Monto.ShouldBe(400m);
        r.Pagos.Single(p => p.Medio == MedioPago.Tarjeta).Monto.ShouldBe(600m);
    }

    [Fact]
    public void La_tarjeta_no_puede_pasar_del_total() =>
        Pagos.Aplicar(100m, [new PagoEntrada(MedioPago.Tarjeta, 150m, null)]).Error!.Code.ShouldBe("Ventas.PagoExcedido");

    [Fact]
    public void Si_falta_dinero_es_un_error() =>
        Pagos.Aplicar(100m, [new PagoEntrada(MedioPago.Efectivo, 99.99m, null)]).Error!.Code.ShouldBe("Ventas.PagoIncompleto");
}
