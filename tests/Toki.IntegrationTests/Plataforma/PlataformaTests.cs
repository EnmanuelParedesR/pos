using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.Fiscal;
using Toki.Fiscal.Contracts;
using Toki.Fiscal.Domain;
using Toki.Host.Local;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Plataforma;

/// <summary>TK-093: health checks and the server's TLS certificate. Own app: the test breaks the health on purpose.</summary>
public sealed class PlataformaTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<(HttpStatusCode, string)> SaludAsync()
    {
        var r = await app.Anonimo().GetAsync("/health");
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_avisa_si_la_hora_retrocede_o_un_eCF_pasa_el_plazo()
    {
        var admin = app.Cliente(await app.AdminAsync());
        await admin.CategoriaAsync();
        (await SaludAsync()).Item1.ShouldBe(HttpStatusCode.OK); // Healthy, or Degraded while no backup exists

        // The newest audit record is an hour ahead of the server clock: someone moved Windows time back.
        var ultima = await app.EnScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Auditoria.MaxAsync(r => r.Secuencia));
        var original = await app.EnScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Auditoria.Where(r => r.Secuencia == ultima).Select(r => r.FechaUtc).SingleAsync());
        Task Fecha(DateTime f) => app.EnScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Auditoria.Where(r => r.Secuencia == ultima)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.FechaUtc, f)));
        await Fecha(original.AddHours(1));
        (await SaludAsync()).ShouldBe((HttpStatusCode.ServiceUnavailable, "Unhealthy"));
        await Fecha(original);
        (await SaludAsync()).Item1.ShouldBe(HttpStatusCode.OK);

        // An e-CF still pending after its 72 hours.
        var ahora = app.Reloj.GetUtcNow().UtcDateTime;
        await app.EnScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            db.Documentos.Add(new DocumentoFiscal
            {
                Id = Guid.CreateVersion7(), ENcf = "E320000097001", Tipo = TipoEcf.Consumo32, ReferenciaId = Guid.NewGuid(), Total = 1m, FechaFirmaUtc = ahora.AddHours(-73),
                CodigoSeguridad = "X", XmlFirmado = [1], Estado = EstadoEnvio.Pendiente, FechaLimiteEnvioUtc = ahora.AddHours(-1),
            });
            return await db.SaveChangesAsync();
        });
        (await SaludAsync()).ShouldBe((HttpStatusCode.ServiceUnavailable, "Unhealthy"));
    }

    [Fact]
    public void El_certificado_del_nodo_se_crea_una_vez_y_se_reutiliza()
    {
        var sujeto = $"CN=Toki Prueba {Guid.NewGuid():N}";
        using var primero = CertificadoNodo.ObtenerOCrear(StoreLocation.CurrentUser, TimeProvider.System, sujeto);
        try
        {
            primero.HasPrivateKey.ShouldBeTrue();
            primero.NotAfter.ShouldBeGreaterThan(DateTime.Now.AddYears(4));
            using var segundo = CertificadoNodo.ObtenerOCrear(StoreLocation.CurrentUser, TimeProvider.System, sujeto);
            segundo.Thumbprint.ShouldBe(primero.Thumbprint);
        }
        finally
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(primero);
        }
    }
}
