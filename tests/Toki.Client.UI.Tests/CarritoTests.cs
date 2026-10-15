using Shouldly;
using Toki.Client.UI.Api;
using Toki.Client.UI.Estado;
using Toki.Escuadra;
using Toki.Escuadra.Componentes;

namespace Toki.Client.UI.Tests;

public sealed class CarritoTests
{
    private static ProductoPorCodigo Producto(string unidad, decimal precio, decimal? cantidadEtiqueta = null, decimal? montoEtiqueta = null) =>
        new(Guid.NewGuid(), null, "Prueba", unidad, "Exento", precio, null, 1m, cantidadEtiqueta, montoEtiqueta, [], null);

    [Fact]
    public void El_mismo_producto_por_unidad_se_suma_en_su_linea_y_el_pesado_va_aparte()
    {
        var c = new Carrito();
        var salami = Producto("Unidad", 210m);
        c.Agregar(salami, null);
        c.Agregar(salami, null);
        c.Lineas.Count.ShouldBe(1);
        c.Lineas[0].Cantidad.ShouldBe(2m);

        var pechuga = Producto("Libra", 165m);
        c.Agregar(pechuga, null, 2.35m);
        c.Agregar(pechuga, null, 1m);
        c.Lineas.Count.ShouldBe(3);
        c.Total.ShouldBe(420m + 387.75m + 165m);
    }

    [Fact]
    public void Una_etiqueta_de_precio_cobra_su_monto_y_se_detecta_repetida()
    {
        var c = new Carrito();
        var linea = c.Agregar(Producto("Libra", 165m, montoEtiqueta: 412.50m), "2201020041250");
        linea.Total.ShouldBe(412.50m);
        linea.Cantidad.ShouldBe(2.5m);
        linea.ParaVenta().CodigoEtiqueta.ShouldBe("2201020041250");
        c.TieneEtiqueta("2201020041250").ShouldBeTrue();
    }

    [Fact]
    public void Descuentos_por_porcentaje_y_monto_y_un_nuevo_id_tras_vaciar()
    {
        var c = new Carrito();
        var l = c.Agregar(Producto("Unidad", 210m), null);
        c.Cambiar(l with { Descuento = new Descuento(true, 20m) });
        c.Total.ShouldBe(168m);
        c.Cambiar(c.Lineas[0] with { Descuento = new Descuento(false, 500m) });
        c.Total.ShouldBe(0m); // a fixed discount never goes below zero
        var id = c.VentaId;
        c.Vaciar();
        c.VentaId.ShouldNotBe(id);
        c.Vacio.ShouldBeTrue();
    }

    [Fact]
    public void Una_venta_en_espera_vuelve_igual()
    {
        var c = new Carrito();
        c.Agregar(Producto("Libra", 165m), null, 1.5m);
        c.Comprobante = TipoEcf.CreditoFiscal31;
        c.RncManual = "131245678";
        var json = System.Text.Json.JsonSerializer.SerializeToElement(c.Guardar(), TokiApi.Json);

        var otro = new Carrito();
        otro.Restaurar(System.Text.Json.JsonSerializer.Deserialize<CarritoGuardado>(json, TokiApi.Json)!);
        otro.Total.ShouldBe(247.50m);
        otro.Comprobante.ShouldBe(TipoEcf.CreditoFiscal31);
        otro.RncManual.ShouldBe("131245678");
    }

    [Theory]
    [InlineData("12", "3", "123")]
    [InlineData("12", "borrar", "1")]
    [InlineData("1.5", ".", "1.5")]
    [InlineData("12", "limpiar", "")]
    public void El_teclado_numerico(string actual, string tecla, string esperado) => TecladoNumerico.Aplicar(actual, tecla).ShouldBe(esperado);

    [Fact]
    public void Formato_de_dinero_y_lectura_de_numeros()
    {
        Formato.Pesos(1234.5m).ShouldBe("1,234.50");
        Formato.LeerNumero("1,250.5").ShouldBe(1250.5m);
        Formato.LeerNumero("abc").ShouldBeNull();
        Formato.Cantidad(2.35m, "Libra").ShouldBe("2.350 lb");
    }

    [Fact]
    public void El_permiso_que_pide_la_api_se_lee_del_detalle()
    {
        new Problema(403, "Autorizacion.Requerida", "Necesita la autorización de un supervisor (ventas.anular).").PermisoRequerido.ShouldBe("ventas.anular");
        new Problema(403, "Otro", "x (y)").PermisoRequerido.ShouldBeNull();
    }
}
