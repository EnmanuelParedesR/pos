using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Shouldly;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Tienda;

namespace Toki.IntegrationTests.Reportes;

/// <summary>A small store day (sales, a void, a return, a closed shift) and the reports that must add up to it. Own app: totals are exact.</summary>
public sealed class ReportesTests(TokiApp app) : IClassFixture<TokiApp>, IAsyncLifetime
{
    // xUnit makes one instance per test but one app per class: the day is sold once.
    private static readonly SemaphoreSlim Candado = new(1, 1);
    private static (Guid Chuleta, Guid Refresco)? _dia;

    private HttpClient _admin = null!;
    private string _periodo = "";
    private Guid _chuleta, _refresco;

    public async ValueTask InitializeAsync()
    {
        _admin = app.Cliente(await app.AdminAsync());
        var hoy = DateOnly.FromDateTime(app.Reloj.GetUtcNow().UtcDateTime.AddHours(-4));
        _periodo = $"desde={hoy:yyyy-MM-dd}&hasta={hoy:yyyy-MM-dd}";
        await Candado.WaitAsync();
        try { _dia ??= await VenderDiaAsync(); }
        finally { Candado.Release(); }
        (_chuleta, _refresco) = _dia.Value;
    }

    private async Task<(Guid, Guid)> VenderDiaAsync()
    {
        var cat = await _admin.CategoriaAsync("Carnes");
        var chuleta = await _admin.ProductoAsync(cat, "Chuleta", 165m, unidad: "Libra", tasa: "Exento", costo: 110m, existencia: 50);
        var refresco = await _admin.ProductoAsync(cat, "=HYPERLINK(\"x\")", 59m, costo: 35m, existencia: 24);
        var cajero = await app.CajeroConTurnoAsync(fondo: 1000m);

        // A: 2 lb chuleta (330, exempt) + 2 refrescos (118, ITBIS 18) = 448.
        (await cajero.VenderAsync(Venta(cajero.TurnoId, 448m, 448m, Linea(chuleta, 2m), Linea(refresco, 2m)))).EnsureSuccessStatusCode();
        // B: voided.
        var b = (await (await cajero.VenderAsync(Venta(cajero.TurnoId, 59m, 59m, Linea(refresco, 1m)))).Json()).GetProperty("ventaId").GetGuid();
        (await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{b}/anular",
            new { Motivo = "Error", AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.anular", b) })).EnsureSuccessStatusCode();
        // C: 3 refrescos (177), one comes back to the shelf.
        var c = (await (await cajero.VenderAsync(Venta(cajero.TurnoId, 177m, 177m, Linea(refresco, 3m)))).Json()).GetProperty("ventaId").GetGuid();
        var linea = (await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/{c}")).GetProperty("lineas")[0].GetProperty("id").GetGuid();
        (await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{c}/devoluciones", new
        {
            DevolucionId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Medio = "Efectivo", Motivo = "Sobró",
            Lineas = new[] { new { LineaVentaId = linea, Cantidad = 1m } }, AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.devolver", c),
        })).EnsureSuccessStatusCode();

        // Expected cash: 1000 + 448 + 177 − 59 = 1566, counted exactly.
        var conteo = new[] { 1000m, 500m, 50m, 10m, 5m, 1m }.Select(d => new { Denominacion = d, Cantidad = 1 });
        (await cajero.Cliente.PostAsJsonAsync($"/api/caja/turnos/{cajero.TurnoId}/cerrar",
            new { Conteo = conteo, AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("caja.aprobar-cierre", cajero.TurnoId) })).EnsureSuccessStatusCode();
        return (chuleta, refresco);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private Task<JsonElement> Get(string ruta) => _admin.GetFromJsonAsync<JsonElement>($"/api/reportes/{ruta}");

    [Fact]
    public async Task Ventas_por_dia_restan_anuladas_y_devoluciones_con_el_costo_historico()
    {
        var dia = (await Get($"ventas/por-dia?{_periodo}")).EnumerateArray().Single();
        dia.GetProperty("ventas").GetInt32().ShouldBe(2);
        dia.GetProperty("devoluciones").GetDecimal().ShouldBe(59m);
        dia.GetProperty("neto").GetDecimal().ShouldBe(566m);
        dia.GetProperty("itbis").GetDecimal().ShouldBe(36m);
        dia.GetProperty("costo").GetDecimal().ShouldBe(360m);
        dia.GetProperty("utilidad").GetDecimal().ShouldBe(170m);
    }

    [Fact]
    public async Task Por_producto_categoria_y_cajero()
    {
        var productos = (await Get($"ventas/por-producto?{_periodo}")).EnumerateArray().ToDictionary(p => p.GetProperty("productoId").GetGuid());
        productos[_chuleta].GetProperty("utilidad").GetDecimal().ShouldBe(110m);
        productos[_refresco].GetProperty("cantidad").GetDecimal().ShouldBe(4m);
        productos[_refresco].GetProperty("neto").GetDecimal().ShouldBe(200m);
        productos[_refresco].GetProperty("utilidad").GetDecimal().ShouldBe(60m);

        var categoria = (await Get($"ventas/por-categoria?{_periodo}")).EnumerateArray().Single();
        categoria.GetProperty("categoria").GetString().ShouldBe("Carnes");
        categoria.GetProperty("utilidad").GetDecimal().ShouldBe(170m);

        var cajero = (await Get($"ventas/por-cajero?{_periodo}")).EnumerateArray().Single();
        cajero.GetProperty("ventas").GetInt32().ShouldBe(2);
        cajero.GetProperty("anuladas").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Turnos_inventario_y_607()
    {
        var turno = (await Get($"turnos?{_periodo}")).EnumerateArray().Single();
        turno.GetProperty("efectivoEsperado").GetDecimal().ShouldBe(1566m);
        turno.GetProperty("diferencia").GetDecimal().ShouldBe(0m);

        var valor = (await Get("inventario/valor")).EnumerateArray().ToDictionary(p => p.GetProperty("productoId").GetGuid());
        valor[_chuleta].GetProperty("valorCosto").GetDecimal().ShouldBe(48m * 110m);
        valor[_refresco].GetProperty("existencia").GetDecimal().ShouldBe(20m);

        // Three sales and two credit notes (the void and the return), each pointing to its sale.
        var docs = (await Get($"dgii/607?{_periodo}")).EnumerateArray().ToList();
        docs.Count.ShouldBe(5);
        docs.Count(d => d.GetProperty("eNcf").GetString()!.StartsWith("E34", StringComparison.Ordinal) && d.GetProperty("eNcfModificado").GetString() is not null).ShouldBe(2);
    }

    [Fact]
    public async Task Exporta_a_CSV_sin_formulas_y_a_Excel()
    {
        var csv = await _admin.GetAsync($"/api/reportes/ventas/por-producto?{_periodo}&formato=csv");
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var texto = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
        texto.ShouldStartWith("﻿ProductoId,CategoriaId,Producto,");
        texto.ShouldContain("\"'=HYPERLINK(\"\"x\"\")\""); // neutralized: opens as text, not as a formula

        var xlsx = await _admin.GetAsync($"/api/reportes/inventario/valor?formato=xlsx");
        xlsx.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var libro = new XLWorkbook(await xlsx.Content.ReadAsStreamAsync());
        var hoja = libro.Worksheets.First();
        hoja.Cell(1, 2).GetString().ShouldBe("Producto");
        hoja.RowsUsed().Count().ShouldBe(3); // header + 2 products
    }

    [Fact]
    public async Task Solo_quien_ve_reportes_y_con_un_periodo_valido()
    {
        var cajero = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        (await cajero.GetAsync($"/api/reportes/ventas/por-dia?{_periodo}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await _admin.GetAsync("/api/reportes/ventas/por-dia?desde=2026-02-01&hasta=2026-01-01")).CodigoError()).ShouldBe("Reportes.PeriodoInvalido");
        (await (await _admin.GetAsync($"/api/reportes/turnos?{_periodo}&formato=pdf")).CodigoError()).ShouldBe("Reportes.FormatoInvalido");
    }
}
