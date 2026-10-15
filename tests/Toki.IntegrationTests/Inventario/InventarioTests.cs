using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Inventario;

public sealed class InventarioTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Cliente(await app.AdminAsync());

    [Fact]
    public async Task Crear_un_producto_y_escanearlo_por_codigo_de_barras()
    {
        var c = await Admin();
        var cat = await c.CategoriaAsync();
        var id = await c.ProductoAsync(cat, "Salami Induveca", 185m, codigo: "7460312004518", existencia: 10);

        var r = await c.GetFromJsonAsync<JsonElement>("/api/inventario/productos/por-codigo/7460312004518");
        r.GetProperty("productoId").GetGuid().ShouldBe(id);
        r.GetProperty("precio").GetDecimal().ShouldBe(185m);
        r.GetProperty("existencia").GetDecimal().ShouldBe(10m);
        r.GetProperty("notas").EnumerateArray().Select(n => n.GetString()).ShouldContain("molida");
    }

    [Fact]
    public async Task Una_etiqueta_de_balanza_trae_el_peso()
    {
        var c = await Admin();
        var cat = await c.CategoriaAsync("Cerdo");
        var id = await c.ProductoAsync(cat, "Chuleta", 165m, unidad: "Libra", plu: "321");
        var etiqueta = "200032102350";
        etiqueta += Toki.Inventario.Domain.EtiquetaBalanza.DigitoVerificador(etiqueta);

        var r = await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/por-codigo/{etiqueta}");
        r.GetProperty("productoId").GetGuid().ShouldBe(id);
        r.GetProperty("cantidadEtiqueta").GetDecimal().ShouldBe(2.35m);

        var mala = await c.GetAsync($"/api/inventario/productos/por-codigo/{etiqueta[..12]}{(etiqueta[12] - '0' + 1) % 10}");
        (await mala.CodigoError()).ShouldBe("Inventario.EtiquetaMalLeida");
    }

    [Fact]
    public async Task Un_codigo_desconocido_da_404()
    {
        var r = await (await Admin()).GetAsync("/api/inventario/productos/por-codigo/0000000000000");
        r.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task El_mismo_codigo_no_puede_ir_en_dos_productos()
    {
        var c = await Admin();
        var cat = await c.CategoriaAsync();
        await c.ProductoAsync(cat, "Uno", 10m, codigo: "111");
        var r = await c.PostAsJsonAsync("/api/inventario/productos", new
        {
            Nombre = "Dos", CategoriaId = cat, Unidad = "Unidad", TasaItbis = "General18", Precio = 10m, Costo = 0m, Codigos = new[] { "111" },
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Un_ajuste_no_saca_mas_de_lo_que_hay_y_queda_en_el_kardex()
    {
        var c = await Admin();
        var id = await c.ProductoAsync(await c.CategoriaAsync(), "Queso", 300m, unidad: "Libra", existencia: 5);

        var mucho = await c.PostAsJsonAsync("/api/inventario/ajustes", new { ProductoId = id, Cantidad = -6m, Motivo = "Merma" });
        mucho.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await mucho.CodigoError()).ShouldBe("Inventario.SinExistencia");

        (await c.PostAsJsonAsync("/api/inventario/ajustes", new { ProductoId = id, Cantidad = -1.25m, Motivo = "Merma" })).EnsureSuccessStatusCode();
        (await c.ExistenciaAsync(id)).ShouldBe(3.75m);

        var kardex = await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/{id}/movimientos");
        kardex.GetArrayLength().ShouldBe(2);
        kardex[0].GetProperty("cantidad").GetDecimal().ShouldBe(-1.25m);
        kardex[0].GetProperty("existenciaResultante").GetDecimal().ShouldBe(3.75m);
    }

    [Fact]
    public async Task Cambiar_el_precio_necesita_permiso_y_la_version_detecta_ediciones_simultaneas()
    {
        var admin = await Admin();
        var categoria = await admin.CategoriaAsync();
        var id = await admin.ProductoAsync(categoria, "Arroz", 45m);

        // A role that can edit products but not prices.
        var rol = await (await admin.PostAsJsonAsync("/api/identidad/roles", new { Nombre = "Almacén", Permisos = new[] { "productos.gestionar", "inventario.ver" } })).Json();
        var almacenista = app.Cliente(await app.UsuarioAsync(rol.GetProperty("id").GetGuid()));

        object Cambio(int version, decimal precio) => new
        {
            Version = version, Nombre = "Arroz Selecto", CategoriaId = categoria,
            Unidad = "Unidad", TasaItbis = "General18", Precio = precio, VenderSinExistencia = false, StockMinimo = 0, Activo = true,
        };

        var sinPermiso = await almacenista.PutAsJsonAsync($"/api/inventario/productos/{id}", Cambio(0, 50m));
        (await sinPermiso.CodigoError()).ShouldBe("Inventario.SinPermisoPrecios");
        (await almacenista.PutAsJsonAsync($"/api/inventario/productos/{id}", Cambio(0, 45m))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var vieja = await admin.PutAsJsonAsync($"/api/inventario/productos/{id}", Cambio(0, 50m));
        vieja.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/inventario/productos/{id}", Cambio(1, 50m))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Transferir_mueve_existencia_entre_almacenes()
    {
        var c = await Admin();
        var id = await c.ProductoAsync(await c.CategoriaAsync(), "Pollo entero", 95m, unidad: "Libra", existencia: 20);
        var frio = (await (await c.PostAsJsonAsync("/api/inventario/almacenes", new { Nombre = "Cuarto frío" })).Json()).GetProperty("id").GetGuid();
        var tienda = (await c.GetFromJsonAsync<JsonElement>("/api/inventario/almacenes")).EnumerateArray().First(a => a.GetProperty("principal").GetBoolean()).GetProperty("id").GetGuid();

        (await c.PostAsJsonAsync("/api/inventario/transferencias", new { Origen = tienda, Destino = frio, Lineas = new[] { new { ProductoId = id, Cantidad = 8m } } }))
            .EnsureSuccessStatusCode();
        (await c.ExistenciaAsync(id)).ShouldBe(12m);
        (await c.ExistenciaAsync(id, frio)).ShouldBe(8m);
    }

    [Fact]
    public async Task El_conteo_es_ciego_y_aplicarlo_pide_supervisor_a_quien_solo_cuenta()
    {
        var admin = await Admin();
        var cat = await admin.CategoriaAsync("Embutidos");
        var a = await admin.ProductoAsync(cat, "Jamón", 250m, unidad: "Libra", existencia: 10, costo: 150);
        var b = await admin.ProductoAsync(cat, "Mortadela", 120m, unidad: "Libra", existencia: 4, costo: 80);

        var rol = await (await admin.PostAsJsonAsync("/api/identidad/roles", new { Nombre = "Contador", Permisos = new[] { "inventario.contar" } })).Json();
        var contador = app.Cliente(await app.UsuarioAsync(rol.GetProperty("id").GetGuid()));

        var conteo = await (await contador.PostAsJsonAsync("/api/inventario/conteos", new { CategoriaId = cat })).Json();
        var conteoId = conteo.GetProperty("conteoId").GetGuid();
        conteo.GetProperty("productos").GetInt32().ShouldBe(2);

        var lectura = await contador.GetFromJsonAsync<JsonElement>($"/api/inventario/conteos/{conteoId}");
        lectura.GetRawText().ShouldNotContain("sistema", Case.Insensitive);

        (await contador.PutAsJsonAsync($"/api/inventario/conteos/{conteoId}/lineas", new { Lineas = new[] { new { ProductoId = a, Contado = 9.5m }, new { ProductoId = b, Contado = 4m } } }))
            .EnsureSuccessStatusCode();

        var sinSupervisor = await contador.PostAsJsonAsync($"/api/inventario/conteos/{conteoId}/aplicar", new { });
        sinSupervisor.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await sinSupervisor.CodigoError()).ShouldBe("Autorizacion.Requerida");

        var autorizacion = await (await contador.PostAsJsonAsync("/api/identidad/autorizaciones",
            new { Codigo = "admin", Pin = TokiApp.PinAdmin, Permiso = "inventario.ajustar", RecursoId = conteoId })).Json();
        var aplicado = await contador.PostAsJsonAsync($"/api/inventario/conteos/{conteoId}/aplicar", new { AutorizacionSupervisorId = autorizacion.GetProperty("autorizacionId").GetGuid() });
        aplicado.StatusCode.ShouldBe(HttpStatusCode.OK);
        var resultado = await aplicado.Json();
        resultado.GetProperty("diferencias").GetArrayLength().ShouldBe(1);
        resultado.GetProperty("valorDiferencia").GetDecimal().ShouldBe(-75m);

        (await admin.ExistenciaAsync(a)).ShouldBe(9.5m);

        // The authorization is single-use.
        var otra = await contador.PostAsJsonAsync($"/api/inventario/conteos/{conteoId}/aplicar", new { AutorizacionSupervisorId = autorizacion.GetProperty("autorizacionId").GetGuid() });
        otra.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
    }
}
