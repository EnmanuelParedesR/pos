using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Tienda;

namespace Toki.IntegrationTests.Reportes;

/// <summary>R5 #2: a return lands on the day it happens in every report, and a closed day never changes. Own app: it moves the clock.</summary>
public sealed class DevolucionOtroDiaTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Una_devolucion_al_dia_siguiente_cuenta_ese_dia_en_todos_los_reportes()
    {
        var admin = app.Cliente(await app.AdminAsync());
        var refresco = await admin.ProductoAsync(await admin.CategoriaAsync("Bebidas"), "Refresco", 59m, costo: 35m, existencia: 10);
        var cajero = await app.CajeroConTurnoAsync(fondo: 1000m);
        var venta = (await (await cajero.VenderAsync(Venta(cajero.TurnoId, 177m, 177m, Linea(refresco, 3m)))).Json()).GetProperty("ventaId").GetGuid();
        var dia1 = DateOnly.FromDateTime(app.Reloj.GetUtcNow().UtcDateTime.AddHours(-4));
        async Task<string> Reporte(string ruta, DateOnly dia) => (await admin.GetFromJsonAsync<JsonElement>($"/api/reportes/{ruta}?desde={dia:yyyy-MM-dd}&hasta={dia:yyyy-MM-dd}")).ToString();
        var antes = (await Reporte("ventas/por-dia", dia1), await Reporte("ventas/por-producto", dia1), await Reporte("ventas/por-categoria", dia1));

        // A day later the sessions have expired (12 h): the owner logs in again and takes the return on the same shift.
        app.Reloj.Advance(TimeSpan.FromDays(1));
        var dia2 = dia1.AddDays(1);
        admin = app.Cliente(await app.IniciarSesionAsync("admin", TokiApp.PinAdmin, (await app.AdminAsync()).Dispositivo));
        var linea = (await admin.GetFromJsonAsync<JsonElement>($"/api/ventas/{venta}")).GetProperty("lineas")[0].GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync($"/api/ventas/{venta}/devoluciones", new
        {
            DevolucionId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Medio = "Efectivo", Motivo = "vencido", Lineas = new[] { new { LineaVentaId = linea, Cantidad = 1m } },
        })).EnsureSuccessStatusCode();

        // Day 1 does not change after the fact.
        (await Reporte("ventas/por-dia", dia1), await Reporte("ventas/por-producto", dia1), await Reporte("ventas/por-categoria", dia1)).ShouldBe(antes);

        // Day 2: −59 with ITBIS, −50 without, its cost comes back (35): profit −15 by day, by product and by category alike.
        var porDia = JsonDocument.Parse(await Reporte("ventas/por-dia", dia2)).RootElement.EnumerateArray().Single();
        porDia.GetProperty("devoluciones").GetDecimal().ShouldBe(59m);
        porDia.GetProperty("utilidad").GetDecimal().ShouldBe(-15m);
        JsonDocument.Parse(await Reporte("ventas/por-producto", dia2)).RootElement.EnumerateArray().Single().GetProperty("cantidad").GetDecimal().ShouldBe(-1m);
        JsonDocument.Parse(await Reporte("ventas/por-categoria", dia2)).RootElement.EnumerateArray().Sum(c => c.GetProperty("utilidad").GetDecimal()).ShouldBe(-15m);
    }
}
