using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Compras;

public sealed class ComprasTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Cliente(await app.AdminAsync());

    private static string NcfNuevo() => "B01" + Random.Shared.Next(10_000_000, 99_999_999);

    private static async Task<Guid> SuplidorAsync(HttpClient c, int dias = 30) =>
        (await (await c.PostAsJsonAsync("/api/compras/suplidores", new { Nombre = "Granja " + Guid.NewGuid().ToString()[..4], Rnc = "101234567", DiasCredito = dias })).Json())
        .GetProperty("id").GetGuid();

    private static async Task<Guid> CompraAsync(HttpClient c, Guid suplidor, string condicion, DateOnly fecha, params object[] lineas)
    {
        var r = await c.PostAsJsonAsync("/api/compras", new { SuplidorId = suplidor, Ncf = NcfNuevo(), FechaFactura = fecha, Condicion = condicion, Lineas = lineas });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await r.Json()).GetProperty("id").GetGuid();
        (await c.PostAsync($"/api/compras/{id}/aplicar", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return id;
    }

    [Fact]
    public async Task Aplicar_una_compra_entra_existencia_y_recalcula_el_costo()
    {
        var c = await Admin();
        var producto = await c.ProductoAsync(await c.CategoriaAsync(), "Pierna de cerdo", 150m, unidad: "Libra", costo: 80m, existencia: 20);
        var suplidor = await SuplidorAsync(c);

        var borrador = await (await c.PostAsJsonAsync("/api/compras", new
        {
            SuplidorId = suplidor, Ncf = NcfNuevo(), FechaFactura = "2026-10-15", Condicion = "Contado",
            Lineas = new[] { new { ProductoId = producto, Cantidad = 60m, CostoUnitario = 100m, TasaItbis = "Exento" } },
        })).Json();
        borrador.GetProperty("total").GetDecimal().ShouldBe(6000m);
        var id = borrador.GetProperty("id").GetGuid();
        (await c.ExistenciaAsync(producto)).ShouldBe(20m); // a draft moves nothing

        (await c.PostAsync($"/api/compras/{id}/aplicar", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await c.PostAsync($"/api/compras/{id}/aplicar", null)).CodigoError()).ShouldBe("Compras.NoEsBorrador");

        (await c.ExistenciaAsync(producto)).ShouldBe(80m);
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/{producto}")).GetProperty("costo").GetDecimal().ShouldBe(95m); // (20×80 + 60×100)/80
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Comprar_por_caja_entra_unidades_y_el_costo_por_unidad()
    {
        var c = await Admin();
        var producto = await c.ProductoAsync(await c.CategoriaAsync("Bebidas"), "Agua 500ml", 25m, costo: 0m);
        var caja = (await (await c.PostAsJsonAsync($"/api/inventario/productos/{producto}/presentaciones", new { Nombre = "Fardo 24", Factor = 24m, Precio = 500m })).Json()).GetProperty("id").GetGuid();
        await CompraAsync(c, await SuplidorAsync(c), "Contado", new DateOnly(2026, 10, 15),
            new { ProductoId = producto, PresentacionId = caja, Cantidad = 2m, CostoUnitario = 312m });

        (await c.ExistenciaAsync(producto)).ShouldBe(48m);
        (await c.GetFromJsonAsync<JsonElement>($"/api/inventario/productos/{producto}")).GetProperty("costo").GetDecimal().ShouldBe(13m);
    }

    [Fact]
    public async Task El_mismo_NCF_del_mismo_suplidor_no_entra_dos_veces()
    {
        var c = await Admin();
        var producto = await c.ProductoAsync(await c.CategoriaAsync(), "Sal", 20m);
        var suplidor = await SuplidorAsync(c);
        var ncf = NcfNuevo();
        object Cuerpo() => new { SuplidorId = suplidor, Ncf = ncf, FechaFactura = "2026-10-15", Condicion = "Contado", Lineas = new[] { new { ProductoId = producto, Cantidad = 1m, CostoUnitario = 10m } } };
        (await c.PostAsJsonAsync("/api/compras", Cuerpo())).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await c.PostAsJsonAsync("/api/compras", Cuerpo())).CodigoError()).ShouldBe("Compras.NcfRepetido");
    }

    [Fact]
    public async Task Los_pagos_van_a_las_facturas_mas_viejas_y_no_pasan_de_la_deuda()
    {
        var c = await Admin();
        var producto = await c.ProductoAsync(await c.CategoriaAsync(), "Pollo entero", 95m, unidad: "Libra");
        var suplidor = await SuplidorAsync(c, dias: 15);
        var vieja = await CompraAsync(c, suplidor, "Credito", new DateOnly(2026, 9, 1), new { ProductoId = producto, Cantidad = 100m, CostoUnitario = 60m, TasaItbis = "Exento" });
        await CompraAsync(c, suplidor, "Credito", new DateOnly(2026, 10, 1), new { ProductoId = producto, Cantidad = 50m, CostoUnitario = 60m, TasaItbis = "Exento" });

        var demasiado = await c.PostAsJsonAsync("/api/compras/pagos", new { SuplidorId = suplidor, Monto = 9001m, Medio = "Transferencia", Fecha = "2026-10-15" });
        (await demasiado.CodigoError()).ShouldBe("Compras.PagoMayorQueDeuda");

        var pago = await (await c.PostAsJsonAsync("/api/compras/pagos", new { SuplidorId = suplidor, Monto = 7000m, Medio = "Transferencia", Fecha = "2026-10-15", Referencia = "TRF-1" })).Json();
        var aplicaciones = pago.GetProperty("aplicaciones");
        aplicaciones[0].GetProperty("monto").GetDecimal().ShouldBe(6000m); // the September invoice first, in full
        aplicaciones[1].GetProperty("monto").GetDecimal().ShouldBe(1000m);

        var estado = await c.GetFromJsonAsync<JsonElement>($"/api/compras/suplidores/{suplidor}/estado-cuenta");
        estado.GetProperty("saldo").GetDecimal().ShouldBe(2000m);
        var antiguedad = await c.GetFromJsonAsync<JsonElement>("/api/compras/por-pagar");
        antiguedad.EnumerateArray().Single(i => i.GetProperty("suplidorId").GetGuid() == suplidor).GetProperty("total").GetDecimal().ShouldBe(2000m);

        // A purchase with payments cannot be voided.
        (await (await c.PostAsJsonAsync($"/api/compras/{vieja}/anular", new { Motivo = "error" })).CodigoError()).ShouldBe("Compras.ConPagos");
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Anular_una_compra_saca_la_existencia_pero_no_por_debajo_de_cero()
    {
        var c = await Admin();
        var producto = await c.ProductoAsync(await c.CategoriaAsync(), "Longaniza", 200m, unidad: "Libra", venderSinExistencia: true);
        var suplidor = await SuplidorAsync(c);
        var compra = await CompraAsync(c, suplidor, "Contado", new DateOnly(2026, 10, 15), new { ProductoId = producto, Cantidad = 10m, CostoUnitario = 120m });
        (await app.ComandoAsync(new MoverInventario(producto, 4m, Entrada: false))).IsSuccess.ShouldBeTrue();

        (await (await c.PostAsJsonAsync($"/api/compras/{compra}/anular", new { Motivo = "llegó mal" })).CodigoError()).ShouldBe("Inventario.SinExistencia");
        (await c.ExistenciaAsync(producto)).ShouldBe(6m);

        var otra = await CompraAsync(c, suplidor, "Contado", new DateOnly(2026, 10, 15), new { ProductoId = producto, Cantidad = 5m, CostoUnitario = 120m });
        (await c.PostAsJsonAsync($"/api/compras/{otra}/anular", new { Motivo = "duplicada" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await c.ExistenciaAsync(producto)).ShouldBe(6m);
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Un_gasto_a_credito_es_una_cuenta_por_pagar()
    {
        var c = await Admin();
        var suplidor = await SuplidorAsync(c);
        var r = await c.PostAsJsonAsync("/api/compras/gastos", new
        {
            SuplidorId = suplidor, Ncf = NcfNuevo(), Fecha = "2026-10-10", Concepto = "Energía eléctrica", Tipo = "TrabajosSuministrosServicios",
            Subtotal = 4000m, Itbis = 720m, Condicion = "Credito",
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await c.GetFromJsonAsync<JsonElement>($"/api/compras/suplidores/{suplidor}/estado-cuenta")).GetProperty("saldo").GetDecimal().ShouldBe(4720m);

        var malo = await c.PostAsJsonAsync("/api/compras/gastos", new
        {
            SuplidorId = suplidor, Ncf = NcfNuevo(), Fecha = "2026-10-10", Concepto = "x", Tipo = "GastosFinancieros", Subtotal = 100m, Itbis = 50m, Condicion = "Contado",
        });
        malo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Un_cajero_no_puede_registrar_compras()
    {
        var cajero = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        (await cajero.GetAsync("/api/compras")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
