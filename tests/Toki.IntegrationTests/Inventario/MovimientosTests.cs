using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Inventario;

/// <summary>Stock movements through the IMovimientosInventario contract (what sales and purchases call) and the catalog rules from review R2.</summary>
public sealed class MovimientosTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Cliente(await app.AdminAsync());

    [Fact]
    public async Task Una_compra_recalcula_el_costo_promedio_ponderado()
    {
        var c = await Admin();
        var id = await c.ProductoAsync(await c.CategoriaAsync(), "Costilla", 180m, unidad: "Libra", costo: 100m, existencia: 10);

        (await app.ComandoAsync(new MoverInventario(id, 30m, Entrada: true, Costo: 140m))).IsSuccess.ShouldBeTrue();

        var p = await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/{id}");
        p.GetProperty("costo").GetDecimal().ShouldBe(130m); // (10×100 + 30×140) / 40
        (await c.ExistenciaAsync(id)).ShouldBe(40m);
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Vender_sin_existencia_deja_negativo_y_marca_para_contar_pero_un_ajuste_no()
    {
        var c = await Admin();
        var id = await c.ProductoAsync(await c.CategoriaAsync(), "Pollo", 95m, unidad: "Libra", venderSinExistencia: true);

        (await app.ComandoAsync(new MoverInventario(id, 2.5m, Entrada: false))).IsSuccess.ShouldBeTrue();
        var p = await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/{id}");
        var tienda = p.GetProperty("existencias").EnumerateArray().Single(e => e.GetProperty("almacen").GetString() == "Tienda");
        tienda.GetProperty("cantidad").GetDecimal().ShouldBe(-2.5m);
        tienda.GetProperty("pendienteContar").GetBoolean().ShouldBeTrue();

        var merma = await c.PostAsJsonAsync("/api/inventario/ajustes", new { ProductoId = id, Cantidad = -1m, Motivo = "Merma" });
        (await merma.CodigoError()).ShouldBe("Inventario.SinExistencia");
    }

    [Fact]
    public async Task Dos_cajas_vendiendo_la_ultima_libra_solo_una_gana()
    {
        var c = await Admin();
        var id = await c.ProductoAsync(await c.CategoriaAsync(), "Lomo", 300m, unidad: "Libra", existencia: 1);

        var resultados = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => app.ComandoAsync(new MoverInventario(id, 1m, Entrada: false))));

        resultados.Count(r => r.IsSuccess).ShouldBe(1);
        (await c.ExistenciaAsync(id)).ShouldBe(0m);
    }

    [Fact]
    public async Task Un_producto_por_unidad_no_acepta_fracciones()
    {
        var c = await Admin();
        var id = await c.ProductoAsync(await c.CategoriaAsync(), "Cerveza", 150m, existencia: 24);
        var r = await c.PostAsJsonAsync("/api/inventario/ajustes", new { ProductoId = id, Cantidad = -0.5m, Motivo = "Danio" });
        (await r.CodigoError()).ShouldBe("Inventario.CantidadInvalida");
    }

    [Fact]
    public async Task El_motivo_del_ajuste_queda_en_el_kardex()
    {
        var c = await Admin();
        var id = await c.ProductoAsync(await c.CategoriaAsync(), "Chorizo", 220m, unidad: "Libra", existencia: 5);
        (await c.PostAsJsonAsync("/api/inventario/ajustes", new { ProductoId = id, Cantidad = -0.75m, Motivo = "Merma", Nota = "se dañó en la nevera" })).EnsureSuccessStatusCode();
        var kardex = await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/{id}/movimientos");
        kardex[0].GetProperty("motivo").GetString().ShouldBe("Merma: se dañó en la nevera");
    }

    [Fact]
    public async Task El_PLU_se_guarda_sin_ceros_y_lo_encuentran_etiquetas_de_4_y_5_digitos()
    {
        var c = await Admin();
        var id = await c.ProductoAsync(await c.CategoriaAsync(), "Pechuga", 140m, unidad: "Libra", plu: "0731");
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/{id}")).GetProperty("plu").GetString().ShouldBe("731");

        string Etiqueta(string doce) => doce + Toki.Inventory.Domain.EtiquetaBalanza.DigitoVerificador(doce);
        var peso = await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/por-codigo/{Etiqueta("200073101500")}"); // 5-digit PLU, 1.500 lb
        peso.GetProperty("productoId").GetGuid().ShouldBe(id);
        peso.GetProperty("cantidadEtiqueta").GetDecimal().ShouldBe(1.5m);

        var precio = await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/por-codigo/{Etiqueta("220731021000")}"); // 4-digit PLU, RD$ 210.00
        precio.GetProperty("productoId").GetGuid().ShouldBe(id);
        precio.GetProperty("montoEtiqueta").GetDecimal().ShouldBe(210m);

        (await c.GetFromJsonAsync<JsonElement>("/api/inventario/productos/por-codigo/0731")).GetProperty("productoId").GetGuid().ShouldBe(id);
    }

    [Fact]
    public async Task El_codigo_de_una_presentacion_trae_su_precio_y_factor()
    {
        var c = await Admin();
        var id = await c.ProductoAsync(await c.CategoriaAsync("Bebidas"), "Malta", 60m);
        var pr = await (await c.PostAsJsonAsync($"/api/inventario/productos/{id}/presentaciones", new { Nombre = "Caja 24", Factor = 24m, Precio = 1300m })).Json();
        var codigo = "CAJA-" + Guid.NewGuid().ToString()[..6];
        (await c.PostAsJsonAsync($"/api/inventario/productos/{id}/codigos", new { Codigo = codigo, PresentacionId = pr.GetProperty("id").GetGuid() })).EnsureSuccessStatusCode();

        var r = await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/por-codigo/{codigo}");
        r.GetProperty("precio").GetDecimal().ShouldBe(1300m);
        r.GetProperty("factor").GetDecimal().ShouldBe(24m);
        r.GetProperty("nombre").GetString().ShouldBe("Malta (Caja 24)");
    }

    [Fact]
    public async Task Una_etiqueta_de_peso_no_aplica_a_un_producto_por_unidad()
    {
        var c = await Admin();
        await c.ProductoAsync(await c.CategoriaAsync(), "Huevos", 10m, plu: "555");
        var etiqueta = "200055501000";
        etiqueta += Toki.Inventory.Domain.EtiquetaBalanza.DigitoVerificador(etiqueta);
        (await (await c.GetAsync($"/api/inventario/productos/por-codigo/{etiqueta}")).CodigoError()).ShouldBe("Inventario.EtiquetaNoAplica");
    }

    [Fact]
    public async Task Crear_un_producto_necesita_permiso_de_precios()
    {
        var admin = await Admin();
        var cat = await admin.CategoriaAsync();
        var rol = await (await admin.PostAsJsonAsync("/api/identidad/roles", new { Nombre = "Catálogo", Permisos = new[] { "productos.gestionar" } })).Json();
        var catalogo = app.Cliente(await app.UsuarioAsync(rol.GetProperty("id").GetGuid()));
        var r = await catalogo.PostAsJsonAsync("/api/inventario/productos", new { Nombre = "Chuleta barata", CategoriaId = cat, Unidad = "Libra", TasaItbis = "General18", Precio = 1m, Costo = 0m });
        (await r.CodigoError()).ShouldBe("Inventario.SinPermisoPrecios");
    }

    [Fact]
    public async Task Un_formato_de_etiqueta_propio_se_puede_configurar()
    {
        var c = await Admin();
        (await c.PutAsJsonAsync("/api/inventario/formatos-etiqueta", new
        {
            Formatos = new[] { new { Prefijo = "20", LongitudPlu = 5, EsPrecio = false, Decimales = 3 }, new { Prefijo = "29", LongitudPlu = 4, EsPrecio = true, Decimales = 2 } },
        })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var id = await c.ProductoAsync(await c.CategoriaAsync(), "Queso de hoja", 260m, unidad: "Libra", plu: "88");
        var etiqueta = "290088031250";
        etiqueta += Toki.Inventory.Domain.EtiquetaBalanza.DigitoVerificador(etiqueta);
        var r = await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/por-codigo/{etiqueta}");
        r.GetProperty("productoId").GetGuid().ShouldBe(id);
        r.GetProperty("montoEtiqueta").GetDecimal().ShouldBe(312.50m);
    }
}

public sealed class ConfiguracionTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task El_administrador_actualiza_los_datos_del_negocio_y_el_cajero_no()
    {
        var admin = app.Cliente(await app.AdminAsync());
        var datos = new { Nombre = "Carnicería La Esquina", Rnc = "131-24567-8", Direccion = "Calle 1", Telefono = "809-555-0101", PieRecibo = "Gracias", TopeDescuentoPct = 5m };
        (await admin.PutAsJsonAsync("/api/configuracion/negocio", datos)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var negocio = await admin.GetFromJsonAsync<JsonElement>("/api/configuracion/negocio");
        negocio.GetProperty("rnc").GetString().ShouldBe("131245678");
        negocio.GetProperty("topeDescuentoPct").GetDecimal().ShouldBe(5m);

        (await admin.PutAsJsonAsync("/api/configuracion/negocio", datos with { Rnc = "123" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var cajero = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        (await cajero.PutAsJsonAsync("/api/configuracion/negocio", datos)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
    }
}
