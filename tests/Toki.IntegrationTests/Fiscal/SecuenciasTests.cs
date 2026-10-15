using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Fiscal;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Tienda;

namespace Toki.IntegrationTests.Fiscal;

/// <summary>e-NCF ranges: oldest usable range first, expired ranges skipped, no range means nothing is saved. Own app: it loads its own ranges.</summary>
public sealed class SecuenciasTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Los_rangos_se_usan_en_orden_saltan_los_vencidos_y_sin_rango_no_se_guarda_nada()
    {
        await app.ConfigurarNegocioAsync();
        var admin = app.Cliente(await app.AdminAsync());
        var hoy = DateOnly.FromDateTime(app.Reloj.GetUtcNow().UtcDateTime.AddHours(-4));
        async Task Rango(int tipo, long desde, long hasta, DateOnly vence) =>
            (await admin.PostAsJsonAsync("/api/fiscal/secuencias", new { Tipo = tipo, Desde = desde, Hasta = hasta, VenceEl = vence })).EnsureSuccessStatusCode();
        await Rango(31, 1, 1, new DateOnly(2030, 12, 31));
        await Rango(31, 2, 5, new DateOnly(2030, 12, 31));
        await Rango(32, 1, 10, hoy.AddDays(-1));
        await Rango(32, 11, 20, new DateOnly(2030, 12, 31));

        var cajero = await app.CajeroConTurnoAsync(cargarSecuencias: false);
        var refresco = await admin.ProductoAsync(await admin.CategoriaAsync(), "Refresco", 59m, existencia: 10);
        async Task<JsonElement> Vender(string tipo) => await (await cajero.VenderAsync(new
        {
            VentaId = Guid.CreateVersion7(), TurnoId = cajero.TurnoId, Lineas = new[] { Linea(refresco, 1m) }, Pagos = new[] { new { Medio = "Efectivo", Monto = 59m } },
            TotalEsperado = 59m, TipoComprobante = tipo, RncCliente = "131245678", NombreCliente = "Colmado El Sol SRL",
        })).Json();

        // The first range runs out and the next one takes over; the expired consumo range is skipped.
        (await Vender("CreditoFiscal31")).GetProperty("eNcf").GetString().ShouldBe("E310000000001");
        (await Vender("CreditoFiscal31")).GetProperty("eNcf").GetString().ShouldBe("E310000000002");
        var consumo = await Vender("Consumo32");
        consumo.GetProperty("eNcf").GetString().ShouldBe("E320000000011");
        var ventaId = consumo.GetProperty("ventaId").GetGuid();

        // No credit-note range: the void fails whole (sale, stock and cash stay as they were).
        async Task<HttpResponseMessage> Anular() => await cajero.Cliente.PostAsJsonAsync($"/api/ventas/{ventaId}/anular",
            new { Motivo = "Error", AutorizacionSupervisorId = await cajero.Cliente.AutorizarAsync("ventas.anular", ventaId) });
        (await (await Anular()).CodigoError()).ShouldBe("Fiscal.SinSecuencia");
        (await cajero.Cliente.GetFromJsonAsync<JsonElement>($"/api/ventas/{ventaId}")).GetProperty("estado").GetString().ShouldBe("Completada");
        (await admin.ExistenciaAsync(refresco)).ShouldBe(7m);

        var alertas = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/alertas");
        bool Alerta(string tipo) => alertas.EnumerateArray().Single(a => a.GetProperty("tipo").GetString() == tipo).GetProperty("alerta").GetBoolean();
        Alerta("NotaCredito34").ShouldBeTrue(); // nothing loaded
        Alerta("Consumo32").ShouldBeFalse();

        // A range that expires within 30 days raises the alert; the credit note points to the sale it cancels.
        await Rango(34, 1, 100, hoy.AddDays(20));
        (await Anular()).EnsureSuccessStatusCode();
        alertas = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/alertas");
        Alerta("NotaCredito34").ShouldBeTrue();
        var modificado = await app.EnScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documentos
            .Where(d => d.ENcf == "E340000000001").Select(d => d.ENcfModificado).SingleAsync());
        modificado.ShouldBe("E320000000011");
    }
}
