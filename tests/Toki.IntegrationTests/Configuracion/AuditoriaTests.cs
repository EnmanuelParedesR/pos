using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.Configuracion;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Configuracion;

/// <summary>TK-094. Own app: the test tampers with the audit table.</summary>
public sealed class AuditoriaTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public void La_verificacion_nocturna_corre_a_las_3_de_la_madrugada_de_la_tienda()
    {
        // 06:00 UTC is 02:00 in Santo Domingo: one hour to go. 08:00 UTC is 04:00: 23 hours.
        VerificacionNocturnaAuditoria.HastaLaProxima(new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc)).ShouldBe(TimeSpan.FromHours(1));
        VerificacionNocturnaAuditoria.HastaLaProxima(new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc)).ShouldBe(TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task Editar_o_borrar_registros_de_auditoria_rompe_la_cadena()
    {
        var admin = app.Cliente(await app.AdminAsync());
        for (var i = 0; i < 3; i++) await admin.CategoriaAsync($"Categoría {i}");
        async Task<JsonElement> Verificar() => await admin.GetFromJsonAsync<JsonElement>("/api/auditoria/verificar");
        (await Verificar()).GetProperty("intacta").GetBoolean().ShouldBeTrue();

        // Deleting the newest record (covering one's tracks) is caught: the chain no longer reaches the head.
        var ultima = await app.EnScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Auditoria.MaxAsync(r => r.Secuencia));
        await app.EnScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Auditoria.Where(r => r.Secuencia == ultima).ExecuteDeleteAsync());
        (await Verificar()).GetProperty("primerRegistroRoto").GetInt64().ShouldBe(ultima);

        // Editing an older record is caught at that record.
        await app.EnScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Auditoria.Where(r => r.Secuencia == 2)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Datos, "{}")));
        var resultado = await Verificar();
        resultado.GetProperty("intacta").GetBoolean().ShouldBeFalse();
        resultado.GetProperty("primerRegistroRoto").GetInt64().ShouldBe(2);

        var cajero = app.Cliente(await app.UsuarioAsync(Roles.Cajero));
        (await cajero.GetAsync("/api/auditoria/verificar")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
