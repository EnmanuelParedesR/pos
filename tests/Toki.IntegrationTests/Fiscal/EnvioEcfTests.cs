using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Toki.Fiscal;
using Toki.Fiscal.Contracts;
using Toki.Fiscal.Domain;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Fiscal;

/// <summary>The e-CF send queue (TK-051) with a provider that fails first and then answers. Own app: it writes queue rows directly.</summary>
public sealed class EnvioEcfTests(TokiApp app) : IClassFixture<TokiApp>
{
    private sealed class ProveedorDePrueba : IEcfProveedor
    {
        public Queue<Func<ResultadoEnvio>> Respuestas { get; } = new();

        public Task<ResultadoEnvio> EnviarAsync(string encf, byte[] xmlFirmadoGzip, CancellationToken ct) => Task.FromResult(Respuestas.Dequeue()());
    }

    private Task<T> ConEnvio<T>(ProveedorDePrueba proveedor, Func<EnvioEcf, FiscalDbContext, Task<T>> accion) => app.EnScopeAsync(sp =>
    {
        var db = sp.GetRequiredService<FiscalDbContext>();
        return accion(new EnvioEcf(db, proveedor, app.Reloj, NullLogger<EnvioEcf>.Instance), db);
    });

    [Fact]
    public async Task Un_fallo_se_reintenta_mas_tarde_y_luego_queda_aceptado()
    {
        await app.AdminAsync();
        var ahora = app.Reloj.GetUtcNow().UtcDateTime;
        var id = Guid.CreateVersion7();
        await app.EnScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            db.Documentos.Add(new DocumentoFiscal
            {
                Id = id, ENcf = "E320000099001", Tipo = TipoEcf.Consumo32, ReferenciaId = Guid.NewGuid(), Total = 100m, FechaFirmaUtc = ahora,
                CodigoSeguridad = "ABC123", XmlFirmado = [1], Estado = EstadoEnvio.Pendiente, FechaLimiteEnvioUtc = ahora.AddHours(72),
            });
            return await db.SaveChangesAsync();
        });
        var proveedor = new ProveedorDePrueba();
        proveedor.Respuestas.Enqueue(() => throw new HttpRequestException("proveedor caído"));
        proveedor.Respuestas.Enqueue(() => new ResultadoEnvio(EstadoEnvio.Aceptado, "TRACK-1"));

        (await ConEnvio(proveedor, (e, _) => e.ProcesarAsync(CancellationToken.None))).ShouldBe(1);
        var fallido = await ConEnvio(proveedor, (_, db) => db.Documentos.AsNoTracking().SingleAsync(d => d.Id == id));
        fallido.Estado.ShouldBe(EstadoEnvio.Pendiente);
        fallido.Intentos.ShouldBe(1);
        fallido.UltimoError.ShouldBe("proveedor caído");

        // Not before its retry time; after it, the document goes and is accepted.
        (await ConEnvio(proveedor, (e, _) => e.ProcesarAsync(CancellationToken.None))).ShouldBe(0);
        app.Reloj.Advance(EnvioEcf.Espera(1));
        (await ConEnvio(proveedor, (e, _) => e.ProcesarAsync(CancellationToken.None))).ShouldBe(1);
        var enviado = await ConEnvio(proveedor, (_, db) => db.Documentos.AsNoTracking().SingleAsync(d => d.Id == id));
        enviado.Estado.ShouldBe(EstadoEnvio.Aceptado);
        enviado.TrackId.ShouldBe("TRACK-1");
        enviado.UltimoError.ShouldBeNull();
    }

    [Fact]
    public async Task La_cola_muestra_pendientes_vencidos_y_simulados()
    {
        var admin = app.Cliente(await app.AdminAsync());
        var ahora = app.Reloj.GetUtcNow().UtcDateTime;
        await app.EnScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            DocumentoFiscal Doc(string encf, EstadoEnvio estado, DateTime limite) => new()
            {
                Id = Guid.CreateVersion7(), ENcf = encf, Tipo = TipoEcf.Consumo32, ReferenciaId = Guid.NewGuid(), Total = 1m, FechaFirmaUtc = ahora,
                CodigoSeguridad = "X", XmlFirmado = [1], Estado = estado, FechaLimiteEnvioUtc = limite, ProximoIntentoUtc = ahora.AddDays(30),
            };
            db.Documentos.AddRange(Doc("E320000098001", EstadoEnvio.Pendiente, ahora.AddHours(-1)), Doc("E320000098002", EstadoEnvio.Simulado, ahora.AddHours(72)));
            return await db.SaveChangesAsync();
        });

        var cola = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/cola");
        cola.GetProperty("vencidos").GetInt32().ShouldBeGreaterThanOrEqualTo(1); // past the 72 hours: the owner must act
        cola.GetProperty("simulados").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        EnvioEcf.Espera(10).ShouldBe(TimeSpan.FromMinutes(60));
    }
}
