using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Tienda;

namespace Toki.IntegrationTests.Ventas;

public sealed class VentasTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Cliente(await app.AdminAsync());

    private async Task<(Guid Chuleta, Guid Refresco)> ProductosAsync()
    {
        var admin = await Admin();
        var cat = await admin.CategoriaAsync();
        var chuleta = await admin.ProductoAsync(cat, "Chuleta " + Guid.NewGuid().ToString()[..4], 165m, unidad: "Libra", tasa: "Exento", costo: 110m, existencia: 50);
        var refresco = await admin.ProductoAsync(cat, "Refresco " + Guid.NewGuid().ToString()[..4], 59m, costo: 35m, existencia: 24);
        return (chuleta, refresco);
    }

    private Task<int> ContarAsync<T>(Func<IServiceProvider, IQueryable<T>> q) => app.EnScopeAsync(sp => q(sp).CountAsync());

    [Fact]
    public async Task Una_venta_en_efectivo_mueve_todo_en_una_transaccion()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 1000m);
        var (chuleta, refresco) = await ProductosAsync();

        // 2.35 lb × 165 = 387.75 (exempt) + 2 × 59 = 118 (ITBIS 18 %: 18.00) → 505.75
        var r = await cajero.VenderAsync(Venta(cajero.TurnoId, 505.75m, 1000m, Linea(chuleta, 2.35m), Linea(refresco, 2)));
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        var venta = await r.Json();
        venta.GetProperty("cambio").GetDecimal().ShouldBe(494.25m);
        venta.GetProperty("eNcf").GetString()!.ShouldStartWith("E32");
        var comprobante = venta.GetProperty("comprobante");
        comprobante.GetProperty("itbis").EnumerateArray().Sum(i => i.GetProperty("itbis").GetDecimal()).ShouldBe(18m);
        comprobante.GetProperty("codigoSeguridad").GetString()!.Length.ShouldBe(6);

        var admin = await Admin();
        (await admin.ExistenciaAsync(chuleta)).ShouldBe(47.65m);
        (await admin.ExistenciaAsync(refresco)).ShouldBe(22m);
        (await app.ReporteXAsync(cajero.TurnoId)).GetProperty("efectivoEsperado").GetDecimal().ShouldBe(1505.75m);
        (await ContarAsync(sp => sp.GetRequiredService<InfraDbContext>().Outbox.Where(m => m.Tipo.EndsWith("VentaRegistradaV1")))).ShouldBeGreaterThan(0);
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Repetir_la_misma_venta_devuelve_la_misma_respuesta_sin_duplicar()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var (_, refresco) = await ProductosAsync();
        var venta = Venta(cajero.TurnoId, 59m, 100m, Linea(refresco, 1));

        var primera = await (await cajero.VenderAsync(venta)).Json();
        var segunda = await cajero.VenderAsync(venta);
        segunda.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await segunda.Json()).GetProperty("eNcf").GetString().ShouldBe(primera.GetProperty("eNcf").GetString());
        (await (await Admin()).ExistenciaAsync(refresco)).ShouldBe(23m);
    }

    [Fact]
    public async Task Si_el_total_cambio_no_se_guarda_nada_ni_se_gasta_un_eNCF()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var (_, refresco) = await ProductosAsync();

        var antes = await (await cajero.VenderAsync(Venta(cajero.TurnoId, 59m, 59m, Linea(refresco, 1)))).Json();
        var mal = await cajero.VenderAsync(Venta(cajero.TurnoId, 50m, 100m, Linea(refresco, 1)));
        mal.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await mal.CodigoError()).ShouldBe("Ventas.TotalCambio");
        var despues = await (await cajero.VenderAsync(Venta(cajero.TurnoId, 59m, 59m, Linea(refresco, 1)))).Json();

        var n1 = long.Parse(antes.GetProperty("eNcf").GetString()![3..]);
        var n2 = long.Parse(despues.GetProperty("eNcf").GetString()![3..]);
        n2.ShouldBe(n1 + 1); // no gap: the failed sale gave its number back
    }

    [Fact]
    public async Task Sin_existencia_la_venta_falla_entera()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var (chuleta, refresco) = await ProductosAsync();
        var r = await cajero.VenderAsync(Venta(cajero.TurnoId, 59m * 30, 2000m, Linea(refresco, 30)));
        (await r.CodigoError()).ShouldBe("Inventario.SinExistencia");
        (await (await Admin()).ExistenciaAsync(chuleta)).ShouldBe(50m);
    }

    [Fact]
    public async Task Un_descuento_por_encima_del_tope_pide_supervisor()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var (_, refresco) = await ProductosAsync();
        var ventaId = Guid.CreateVersion7();
        object Cuerpo(params Guid[] autorizaciones) => new
        {
            VentaId = ventaId, TurnoId = cajero.TurnoId, Lineas = new[] { Linea(refresco, 2) }, DescuentoGlobal = new { EsPorcentaje = true, Valor = 20m },
            Pagos = new[] { new { Medio = "Efectivo", Monto = 100m } }, TotalEsperado = 94.40m, Autorizaciones = autorizaciones,
        };

        (await (await cajero.VenderAsync(Cuerpo())).CodigoError()).ShouldBe("Autorizacion.Requerida");
        var ok = await cajero.VenderAsync(Cuerpo(await cajero.Cliente.AutorizarAsync("ventas.descuento-mayor", ventaId)));
        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ok.Json()).GetProperty("total").GetDecimal().ShouldBe(94.40m);
    }

    [Fact]
    public async Task Cambiar_el_precio_o_vender_al_por_mayor_pide_supervisor()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var (_, refresco) = await ProductosAsync();
        var r = await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Lineas = new[] { new { ProductoId = refresco, Cantidad = 1m, PrecioUnitario = 40m } },
            Pagos = new[] { new { Medio = "Efectivo", Monto = 40m } }, TotalEsperado = 40m,
        });
        (await r.CodigoError()).ShouldBe("Autorizacion.Requerida");
    }

    [Fact]
    public async Task Credito_fiscal_necesita_RNC()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var (_, refresco) = await ProductosAsync();
        var sinRnc = await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Lineas = new[] { Linea(refresco, 1) }, Pagos = new[] { new { Medio = "Efectivo", Monto = 59m } },
            TotalEsperado = 59m, TipoComprobante = "CreditoFiscal31",
        });
        sinRnc.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var conRnc = await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Lineas = new[] { Linea(refresco, 1) }, Pagos = new[] { new { Medio = "Efectivo", Monto = 59m } },
            TotalEsperado = 59m, TipoComprobante = "CreditoFiscal31", RncCliente = "131245678", NombreCliente = "Colmado El Sol SRL",
        });
        conRnc.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await conRnc.Json()).GetProperty("eNcf").GetString()!.ShouldStartWith("E31");
    }

    [Fact]
    public async Task Anular_dentro_del_turno_devuelve_inventario_y_dinero_con_nota_de_credito()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 500m);
        var (chuleta, _) = await ProductosAsync();
        var venta = await (await cajero.VenderAsync(Venta(cajero.TurnoId, 330m, 500m, Linea(chuleta, 2m)))).Json();
        var ventaId = venta.GetProperty("ventaId").GetGuid();

        var sinPermiso = await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/anular", new { Motivo = "Cliente se arrepintió" });
        (await sinPermiso.CodigoError()).ShouldBe("Autorizacion.Requerida");

        var anulada = await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/anular",
            new { Motivo = "Cliente se arrepintió", AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.anular", ventaId) });
        anulada.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await anulada.Json()).GetProperty("eNcfNotaCredito").GetString()!.ShouldStartWith("E34");

        (await (await Admin()).ExistenciaAsync(chuleta)).ShouldBe(50m);
        var x = await app.ReporteXAsync(cajero.TurnoId);
        x.GetProperty("efectivoEsperado").GetDecimal().ShouldBe(500m);
        x.GetProperty("ventasAnuladas").GetInt32().ShouldBe(1);

        var otra = await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/anular",
            new { Motivo = "otra vez", AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.anular", ventaId) });
        (await otra.CodigoError()).ShouldBe("Ventas.YaAnulada");
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Devolucion_parcial_repone_lo_que_se_puede_y_devuelve_el_efectivo()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 1000m);
        var (chuleta, refresco) = await ProductosAsync();
        var venta = await (await cajero.VenderAsync(Venta(cajero.TurnoId, 495m + 177m, 1000m, Linea(chuleta, 3m), Linea(refresco, 3)))).Json();
        var ventaId = venta.GetProperty("ventaId").GetGuid();
        var detalle = await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/{ventaId}");
        var lineaChuleta = detalle.GetProperty("lineas").EnumerateArray().First(l => l.GetProperty("descripcion").GetString()!.StartsWith("Chuleta")).GetProperty("id").GetGuid();
        var lineaRefresco = detalle.GetProperty("lineas").EnumerateArray().First(l => l.GetProperty("descripcion").GetString()!.StartsWith("Refresco")).GetProperty("id").GetGuid();

        object Devolucion(Guid id, Guid autorizacion, params object[] lineas) =>
            new { DevolucionId = id, TurnoId = cajero.TurnoId, Lineas = lineas, Medio = "Efectivo", Motivo = "Mal estado", AutorizacionSupervisorId = autorizacion };

        // 1 lb of chuleta goes to waste (not restocked) and 1 refresco back to the shelf.
        var devolucionId = Guid.CreateVersion7();
        var r = await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/devoluciones", Devolucion(devolucionId, await cajero.Cliente.AutorizarAsync("ventas.devolver", ventaId),
            new { LineaVentaId = lineaChuleta, Cantidad = 1m, Reponer = false }, new { LineaVentaId = lineaRefresco, Cantidad = 1m, Reponer = true }));
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var dev = await r.Json();
        dev.GetProperty("total").GetDecimal().ShouldBe(165m + 59m);
        dev.GetProperty("eNcfNotaCredito").GetString()!.ShouldStartWith("E34");

        var admin = await Admin();
        (await admin.ExistenciaAsync(chuleta)).ShouldBe(47m);
        (await admin.ExistenciaAsync(refresco)).ShouldBe(22m);
        (await app.ReporteXAsync(cajero.TurnoId)).GetProperty("efectivoEsperado").GetDecimal().ShouldBe(1000m + 672m - 224m);

        // Cannot return more than what is left of the line.
        var demasiado = await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/devoluciones", Devolucion(Guid.CreateVersion7(),
            await cajero.Cliente.AutorizarAsync("ventas.devolver", ventaId), new { LineaVentaId = lineaRefresco, Cantidad = 3m, Reponer = true }));
        (await demasiado.CodigoError()).ShouldBe("Ventas.CantidadDevolucion");

        // With returns, a void is no longer possible.
        var anular = await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/anular",
            new { Motivo = "x", AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.anular", ventaId) });
        (await anular.CodigoError()).ShouldBe("Ventas.ConDevoluciones");
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Despues_del_cierre_no_se_anula_hay_que_devolver()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 0m);
        var (_, refresco) = await ProductosAsync();
        var ventaId = (await (await cajero.VenderAsync(Venta(cajero.TurnoId, 59m, 59m, Linea(refresco, 1)))).Json()).GetProperty("ventaId").GetGuid();
        var cierre = await cajero.Cliente.PostAsJsonAsync($"/api/caja/turnos/{cajero.TurnoId}/cerrar", new
        {
            Conteo = new[] { new { Denominacion = 50m, Cantidad = 1 }, new { Denominacion = 5m, Cantidad = 1 }, new { Denominacion = 1m, Cantidad = 4 } },
            AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("caja.aprobar-cierre", cajero.TurnoId),
        });
        cierre.EnsureSuccessStatusCode();

        var admin = await Admin();
        var anular = await admin.PostAsJsonAsync($"/api/ventas/{ventaId}/anular", new { Motivo = "tarde" });
        (await anular.CodigoError()).ShouldBe("Ventas.FueraDeTurno");
    }

    [Fact]
    public async Task Una_etiqueta_de_precio_cobra_lo_que_dice_la_etiqueta()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var (chuleta, _) = await ProductosAsync();
        var r = await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Pagos = new[] { new { Medio = "Efectivo", Monto = 400m } }, TotalEsperado = 387.75m,
            Lineas = new[] { new { ProductoId = chuleta, Cantidad = 2.35m, MontoEtiqueta = 387.75m, CodigoEtiqueta = "2200001038775" } },
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await Admin()).ExistenciaAsync(chuleta)).ShouldBe(47.65m);
    }

    [Fact]
    public async Task Pago_mixto_y_ventas_en_espera()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 0m);
        var (_, refresco) = await ProductosAsync();
        var r = await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Lineas = new[] { Linea(refresco, 4) }, TotalEsperado = 236m,
            Pagos = new object[] { new { Medio = "Tarjeta", Monto = 200m, Referencia = "AUT-778" }, new { Medio = "Efectivo", Monto = 50m } },
        });
        (await r.Json()).GetProperty("cambio").GetDecimal().ShouldBe(14m);
        var x = await app.ReporteXAsync(cajero.TurnoId);
        x.GetProperty("efectivoEsperado").GetDecimal().ShouldBe(36m);

        var espera = Guid.CreateVersion7();
        (await cajero.Cliente.PutAsJsonAsync("/api/ventas/en-espera", new { Id = espera, TurnoId = cajero.TurnoId, Etiqueta = "Señora del pollo", Total = 300m, Contenido = new { lineas = 3 } }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var lista = await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/en-espera?turnoId={cajero.TurnoId}");
        lista[0].GetProperty("contenido").GetProperty("lineas").GetInt32().ShouldBe(3);
        (await cajero.Cliente.DeleteAsync($"/api/ventas/en-espera/{espera}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/en-espera?turnoId={cajero.TurnoId}")).GetArrayLength().ShouldBe(0);
    }
}
