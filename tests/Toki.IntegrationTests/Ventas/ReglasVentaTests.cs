using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Tienda;

namespace Toki.IntegrationTests.Ventas;

/// <summary>Price, discount, unit and shift rules a sale must enforce on the server (review R3).</summary>
public sealed class ReglasVentaTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Cliente(await app.AdminAsync());

    private async Task<Guid> ProductoAsync(string nombre, decimal precio, decimal costo = 0, string unidad = "Unidad", string tasa = "General18",
        decimal existencia = 100, decimal? mayorista = null)
    {
        var admin = await Admin();
        return await admin.ProductoAsync(await admin.CategoriaAsync(), $"{nombre} {Guid.NewGuid().ToString()[..4]}", precio, unidad: unidad, tasa: tasa,
            costo: costo, existencia: existencia, mayorista: mayorista);
    }

    private static object Cuerpo(Guid ventaId, Guid turnoId, decimal total, decimal efectivo, object[] lineas, string nivel = "Detalle", Guid[]? autorizaciones = null) => new
    {
        VentaId = ventaId, TurnoId = turnoId, Lineas = lineas, Pagos = new[] { new { Medio = "Efectivo", Monto = efectivo } }, TotalEsperado = total,
        Nivel = nivel, Autorizaciones = autorizaciones ?? [],
    };

    private static async Task<string?> PermisoPedido(HttpResponseMessage r)
    {
        r.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var detalle = (await r.Json()).GetProperty("detail").GetString()!;
        return detalle[(detalle.LastIndexOf('(') + 1)..^2];
    }

    [Fact]
    public async Task Un_descuento_grande_en_una_sola_linea_pide_supervisor_aunque_la_venta_sea_grande()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var chuleta = await ProductoAsync("Chuleta", 165m, costo: 110m, unidad: "Libra", tasa: "Exento");
        var refresco = await ProductoAsync("Refresco", 59m, costo: 35m);

        // 20 % off the refrescos is 1.3 % of the whole sale, but 20 % of that line (cap: 10 %).
        var lineas = new object[] { Linea(chuleta, 10m), new { ProductoId = refresco, Cantidad = 2m, Descuento = new { EsPorcentaje = true, Valor = 20m } } };
        var ventaId = Guid.CreateVersion7();
        (await PermisoPedido(await cajero.VenderAsync(Cuerpo(ventaId, cajero.TurnoId, 1744.40m, 1745m, lineas)))).ShouldBe("ventas.descuento-mayor");

        var ok = await cajero.VenderAsync(Cuerpo(ventaId, cajero.TurnoId, 1744.40m, 1745m, lineas, autorizaciones: [await cajero.Cliente.AutorizarAsync("ventas.descuento-mayor", ventaId)]));
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Un_descuento_pequeno_que_deja_la_linea_bajo_costo_pide_supervisor()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var agua = await ProductoAsync("Agua", 118m, costo: 95m); // 100 before ITBIS

        // 10 % (inside the cap) leaves 90 before ITBIS, under the cost of 95.
        var lineas = new object[] { new { ProductoId = agua, Cantidad = 1m, Descuento = new { EsPorcentaje = true, Valor = 10m } } };
        var ventaId = Guid.CreateVersion7();
        (await PermisoPedido(await cajero.VenderAsync(Cuerpo(ventaId, cajero.TurnoId, 106.20m, 107m, lineas)))).ShouldBe("ventas.cambiar-precio");

        var ok = await cajero.VenderAsync(Cuerpo(ventaId, cajero.TurnoId, 106.20m, 107m, lineas, autorizaciones: [await cajero.Cliente.AutorizarAsync("ventas.cambiar-precio", ventaId)]));
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Al_por_mayor_usa_el_precio_mayorista_y_sin_cliente_mayorista_pide_supervisor()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var arroz = await ProductoAsync("Arroz", 50m, costo: 30m, mayorista: 45m);
        var ventaId = Guid.CreateVersion7();
        var lineas = new object[] { Linea(arroz, 10m) };

        (await PermisoPedido(await cajero.VenderAsync(Cuerpo(ventaId, cajero.TurnoId, 450m, 450m, lineas, "Mayorista")))).ShouldBe("ventas.cambiar-precio");
        var ok = await cajero.VenderAsync(Cuerpo(ventaId, cajero.TurnoId, 450m, 450m, lineas, "Mayorista",
            [await cajero.Cliente.AutorizarAsync("ventas.cambiar-precio", ventaId)]));
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ok.Json()).GetProperty("total").GetDecimal().ShouldBe(450m);
    }

    [Fact]
    public async Task Una_presentacion_mueve_su_factor_imprime_su_nombre_y_se_devuelve_proporcional()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var admin = await Admin();
        var cerveza = await ProductoAsync("Cerveza", 120m, costo: 80m, existencia: 48);
        var caja = (await (await admin.PostAsJsonAsync($"/api/inventario/productos/{cerveza}/presentaciones", new { Nombre = "Caja 12", Factor = 12m, Precio = 1300m }))
            .Json()).GetProperty("id").GetGuid();

        var venta = await (await cajero.VenderAsync(Venta(cajero.TurnoId, 2600m, 2600m, new { ProductoId = cerveza, PresentacionId = caja, Cantidad = 2m }))).Json();
        (await admin.ExistenciaAsync(cerveza)).ShouldBe(24m);
        var ventaId = venta.GetProperty("ventaId").GetGuid();
        var linea = (await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/{ventaId}")).GetProperty("lineas")[0];
        linea.GetProperty("descripcion").GetString()!.ShouldEndWith("(Caja 12)");

        var dev = await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/devoluciones", new
        {
            DevolucionId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Medio = "Efectivo", Motivo = "Sobró",
            Lineas = new[] { new { LineaVentaId = linea.GetProperty("id").GetGuid(), Cantidad = 1m } },
            AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.devolver", ventaId),
        });
        (await dev.Json()).GetProperty("total").GetDecimal().ShouldBe(1300m);
        (await admin.ExistenciaAsync(cerveza)).ShouldBe(36m);
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Lo_que_se_vende_por_unidad_no_acepta_fracciones()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var refresco = await ProductoAsync("Refresco", 59m);
        (await (await cajero.VenderAsync(Venta(cajero.TurnoId, 88.50m, 89m, Linea(refresco, 1.5m)))).CodigoError()).ShouldBe("Ventas.CantidadInvalida");

        var ventaId = (await (await cajero.VenderAsync(Venta(cajero.TurnoId, 118m, 118m, Linea(refresco, 2m)))).Json()).GetProperty("ventaId").GetGuid();
        var linea = (await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/{ventaId}")).GetProperty("lineas")[0].GetProperty("id").GetGuid();
        var dev = await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/devoluciones", new
        {
            DevolucionId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Medio = "Efectivo", Motivo = "x",
            Lineas = new[] { new { LineaVentaId = linea, Cantidad = 0.5m } }, AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.devolver", ventaId),
        });
        (await dev.CodigoError()).ShouldBe("Ventas.CantidadInvalida");
    }

    [Fact]
    public async Task Un_consumo_desde_250_mil_necesita_RNC()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var nevera = await ProductoAsync("Nevera", 125_000m, existencia: 4);
        (await (await cajero.VenderAsync(Venta(cajero.TurnoId, 250_000m, 250_000m, Linea(nevera, 2m)))).CodigoError()).ShouldBe("Ventas.RncRequerido");

        var conRnc = await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Lineas = new[] { Linea(nevera, 2m) }, TotalEsperado = 250_000m,
            Pagos = new[] { new { Medio = "Transferencia", Monto = 250_000m } }, RncCliente = "001-1234567-8", NombreCliente = "Juan Pérez",
        });
        conRnc.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task El_mismo_VentaId_con_otro_carrito_no_devuelve_la_venta_vieja()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var refresco = await ProductoAsync("Refresco", 59m);
        var ventaId = Guid.CreateVersion7();
        (await cajero.VenderAsync(Cuerpo(ventaId, cajero.TurnoId, 59m, 59m, [Linea(refresco, 1m)]))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var otra = await cajero.VenderAsync(Cuerpo(ventaId, cajero.TurnoId, 118m, 118m, [Linea(refresco, 2m)]));
        otra.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await otra.CodigoError()).ShouldBe("Ventas.IdRepetido");
    }

    [Fact]
    public async Task No_se_vende_en_un_turno_cerrado_ni_en_el_de_otro_cajero()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 0m);
        var otro = await app.CajeroConTurnoAsync();
        var refresco = await ProductoAsync("Refresco", 59m);

        (await (await otro.VenderAsync(Venta(cajero.TurnoId, 59m, 59m, Linea(refresco, 1m)))).CodigoError()).ShouldBe("Caja.TurnoAjeno");

        (await cajero.Cliente.PostAsJsonAsync($"/api/caja/turnos/{cajero.TurnoId}/cerrar", new
        {
            Conteo = Array.Empty<object>(), AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("caja.aprobar-cierre", cajero.TurnoId),
        })).EnsureSuccessStatusCode();
        (await (await cajero.VenderAsync(Venta(cajero.TurnoId, 59m, 59m, Linea(refresco, 1m)))).CodigoError()).ShouldBe("Caja.TurnoCerrado");
    }
}
