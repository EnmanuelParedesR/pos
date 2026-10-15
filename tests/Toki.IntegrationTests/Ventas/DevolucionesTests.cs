using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Fiscal;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Tienda;

namespace Toki.IntegrationTests.Ventas;

/// <summary>Voids and returns: money goes back once and the way it came in (review R3).
/// On SQLite writes run one at a time, so the parallel tests check the final state; on SQL Server (CI) they also race for real.</summary>
public sealed class DevolucionesTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<Guid> ProductoAsync(string nombre, decimal precio, decimal existencia = 100)
    {
        var admin = app.Cliente(await app.AdminAsync());
        return await admin.ProductoAsync(await admin.CategoriaAsync(), $"{nombre} {Guid.NewGuid().ToString()[..4]}", precio, existencia: existencia);
    }

    private static async Task<(Guid VentaId, Guid LineaId)> VenderAsync(Cajero cajero, object venta)
    {
        var r = await cajero.VenderAsync(venta);
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        var ventaId = (await r.Json()).GetProperty("ventaId").GetGuid();
        var detalle = await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/{ventaId}");
        return (ventaId, detalle.GetProperty("lineas")[0].GetProperty("id").GetGuid());
    }

    private static async Task<HttpResponseMessage> DevolverAsync(Cajero cajero, Guid ventaId, Guid lineaId, decimal cantidad, string medio = "Efectivo", Guid? devolucionId = null) =>
        await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/devoluciones", new
        {
            DevolucionId = devolucionId ?? Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Medio = medio, Motivo = "Devolución",
            Lineas = new[] { new { LineaVentaId = lineaId, Cantidad = cantidad } },
            AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.devolver", ventaId),
        });

    private static async Task<HttpResponseMessage> AnularAsync(Cajero cajero, Guid ventaId) =>
        await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/anular",
            new { Motivo = "Error", AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.anular", ventaId) });

    [Fact]
    public async Task Una_venta_con_tarjeta_no_se_devuelve_en_efectivo()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var whisky = await ProductoAsync("Whisky", 5000m);
        var (ventaId, linea) = await VenderAsync(cajero, new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Lineas = new[] { Linea(whisky, 1m) }, TotalEsperado = 5000m,
            Pagos = new[] { new { Medio = "Tarjeta", Monto = 5000m, Referencia = "AUT-1" } },
        });

        (await (await DevolverAsync(cajero, ventaId, linea, 1m, "Efectivo")).CodigoError()).ShouldBe("Ventas.MedioExcedido");
        (await DevolverAsync(cajero, ventaId, linea, 1m, "Tarjeta")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.ReporteXAsync(cajero.TurnoId)).GetProperty("efectivoEsperado").GetDecimal().ShouldBe(2000m);
    }

    [Fact]
    public async Task La_ultima_pieza_cuadra_monto_e_itbis_y_cada_nota_cumple_precio_por_cantidad_menos_descuento()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var queso = await ProductoAsync("Queso", 50m);
        // 3 × 50 = 150 with RD$ 50 off: the line is 100.00, which does not split evenly in three.
        var ventaId = Guid.CreateVersion7();
        var (_, linea) = await VenderAsync(cajero, new
        {
            VentaId = ventaId, TurnoId = cajero.TurnoId, TotalEsperado = 100m, Pagos = new[] { new { Medio = "Efectivo", Monto = 100m } },
            Lineas = new[] { new { ProductoId = queso, Cantidad = 3m, Descuento = new { EsPorcentaje = false, Valor = 50m } } },
            Autorizaciones = new[] { await cajero.Cliente.AutorizarAsync("ventas.descuento-mayor", ventaId) },
        });

        var devoluciones = new List<JsonElement>();
        for (var i = 0; i < 3; i++) devoluciones.Add(await (await DevolverAsync(cajero, ventaId, linea, 1m)).Json());
        devoluciones.Select(d => d.GetProperty("total").GetDecimal()).ShouldBe([33.33m, 33.33m, 33.34m]);
        var detalle = await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/{ventaId}");
        devoluciones.Sum(d => d.GetProperty("itbis").GetDecimal()).ShouldBe(detalle.GetProperty("lineas")[0].GetProperty("itbis").GetDecimal());

        var notas = devoluciones.Select(d => d.GetProperty("eNcfNotaCredito").GetString()!).ToList();
        foreach (var item in await ItemsDeAsync(notas))
        {
            decimal Valor(string nombre) => decimal.Parse(item.Element(nombre)!.Value, CultureInfo.InvariantCulture);
            Math.Abs(Valor("PrecioUnitarioItem") * Valor("CantidadItem") - Valor("DescuentoMonto") - Valor("MontoItem")).ShouldBeLessThanOrEqualTo(0.01m);
        }
    }

    private Task<List<XElement>> ItemsDeAsync(List<string> encfs) => app.EnScopeAsync(async sp =>
    {
        var docs = await sp.GetRequiredService<FiscalDbContext>().Documentos.Where(d => encfs.Contains(d.ENcf)).ToListAsync();
        docs.Count.ShouldBe(encfs.Count);
        return docs.SelectMany(d =>
        {
            using var gzip = new GZipStream(new MemoryStream(d.XmlFirmado), CompressionMode.Decompress);
            return XDocument.Load(gzip).Descendants("Item");
        }).ToList();
    });

    [Fact]
    public async Task Repetir_la_misma_devolucion_devuelve_la_misma_respuesta()
    {
        var cajero = await app.CajeroConTurnoAsync();
        var refresco = await ProductoAsync("Refresco", 59m);
        var (ventaId, linea) = await VenderAsync(cajero, Venta(cajero.TurnoId, 118m, 118m, Linea(refresco, 2m)));
        var id = Guid.CreateVersion7();

        var primera = await (await DevolverAsync(cajero, ventaId, linea, 1m, devolucionId: id)).Json();
        var segunda = await DevolverAsync(cajero, ventaId, linea, 1m, devolucionId: id);
        segunda.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await segunda.Json()).GetProperty("eNcfNotaCredito").GetString().ShouldBe(primera.GetProperty("eNcfNotaCredito").GetString());
        (await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/{ventaId}")).GetProperty("lineas")[0].GetProperty("cantidadDevuelta").GetDecimal().ShouldBe(1m);
    }

    [Fact]
    public async Task Dos_anulaciones_a_la_vez_devuelven_el_dinero_una_sola_vez()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 1000m);
        var refresco = await ProductoAsync("Refresco", 59m, existencia: 10);
        var (ventaId, _) = await VenderAsync(cajero, Venta(cajero.TurnoId, 118m, 118m, Linea(refresco, 2m)));

        var respuestas = await Task.WhenAll(AnularAsync(cajero, ventaId), AnularAsync(cajero, ventaId));
        respuestas.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        (await respuestas.Single(r => r.StatusCode != HttpStatusCode.OK).CodigoError()).ShouldBe("Ventas.YaAnulada");
        (await app.ReporteXAsync(cajero.TurnoId)).GetProperty("efectivoEsperado").GetDecimal().ShouldBe(1000m);
        (await app.Cliente(await app.AdminAsync()).ExistenciaAsync(refresco)).ShouldBe(10m);
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Una_anulacion_y_una_devolucion_a_la_vez_no_pagan_dos_veces()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 1000m);
        var otraCaja = await app.CajeroConTurnoAsync(fondo: 1000m);
        var refresco = await ProductoAsync("Refresco", 59m, existencia: 10);
        var (ventaId, linea) = await VenderAsync(cajero, Venta(cajero.TurnoId, 177m, 177m, Linea(refresco, 3m)));

        var respuestas = await Task.WhenAll(AnularAsync(cajero, ventaId), DevolverAsync(otraCaja, ventaId, linea, 1m));
        respuestas.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var devuelto = 1000m * 2 + 177m - (await app.ReporteXAsync(cajero.TurnoId)).GetProperty("efectivoEsperado").GetDecimal()
                       - (await app.ReporteXAsync(otraCaja.TurnoId)).GetProperty("efectivoEsperado").GetDecimal();
        devuelto.ShouldBeOneOf(177m, 59m);
        (await app.DescuadresKardexAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dos_devoluciones_de_la_misma_linea_a_la_vez_suman_bien()
    {
        var cajero = await app.CajeroConTurnoAsync(fondo: 1000m);
        var otraCaja = await app.CajeroConTurnoAsync(fondo: 1000m);
        var refresco = await ProductoAsync("Refresco", 59m, existencia: 10);
        var (ventaId, linea) = await VenderAsync(cajero, Venta(cajero.TurnoId, 177m, 177m, Linea(refresco, 3m)));

        var respuestas = await Task.WhenAll(DevolverAsync(cajero, ventaId, linea, 1m), DevolverAsync(otraCaja, ventaId, linea, 1m));
        respuestas.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        (await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/{ventaId}")).GetProperty("lineas")[0].GetProperty("cantidadDevuelta").GetDecimal().ShouldBe(2m);
        (await app.Cliente(await app.AdminAsync()).ExistenciaAsync(refresco)).ShouldBe(9m);
        // Only one unit is left: a third return of 2 is rejected.
        (await (await DevolverAsync(cajero, ventaId, linea, 2m)).CodigoError()).ShouldBe("Ventas.CantidadDevolucion");
    }
}
