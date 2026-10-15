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

/// <summary>A provider that answers whatever each test queues, in order.</summary>
internal sealed class ProveedorDePrueba : IEcfProveedor
{
    public Queue<Func<ResultadoEnvio>> Respuestas { get; } = new();

    public Task<ResultadoEnvio> EnviarAsync(string encf, byte[] xmlFirmadoGzip, CancellationToken ct) => Task.FromResult(Respuestas.Dequeue()());
}

internal static class ColaDePrueba
{
    public static Task<T> ConEnvio<T>(this TokiApp app, ProveedorDePrueba proveedor, Func<EnvioEcf, FiscalDbContext, Task<T>> accion) => app.EnScopeAsync(sp =>
    {
        var db = sp.GetRequiredService<FiscalDbContext>();
        return accion(new EnvioEcf(db, proveedor, app.Reloj, NullLogger<EnvioEcf>.Instance), db);
    });

    /// <summary>A signed document waiting to be sent, as a real signer would leave it.</summary>
    public static Task<Guid> PendienteAsync(this TokiApp app, string encf, DateTime limiteUtc, DateTime? proximoIntentoUtc = null) => app.EnScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<FiscalDbContext>();
        var d = new DocumentoFiscal
        {
            Id = Guid.CreateVersion7(), ENcf = encf, Tipo = TipoEcf.Consumo32, ReferenciaId = Guid.NewGuid(), Total = 1m, FechaFirmaUtc = app.Reloj.GetUtcNow().UtcDateTime,
            CodigoSeguridad = "X", XmlFirmado = [1], Estado = EstadoEnvio.Pendiente, FechaLimiteEnvioUtc = limiteUtc, ProximoIntentoUtc = proximoIntentoUtc,
        };
        db.Documentos.Add(d);
        await db.SaveChangesAsync();
        return d.Id;
    });
}

/// <summary>The e-CF send queue (TK-051) with a provider that fails first and then answers. Own app: it writes queue rows directly.</summary>
public sealed class EnvioEcfTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Un_fallo_se_reintenta_mas_tarde_y_luego_queda_aceptado()
    {
        await app.AdminAsync();
        var id = await app.PendienteAsync("E320000099001", app.Reloj.GetUtcNow().UtcDateTime.AddHours(72));
        var proveedor = new ProveedorDePrueba();
        proveedor.Respuestas.Enqueue(() => throw new HttpRequestException("proveedor caído"));
        proveedor.Respuestas.Enqueue(() => new ResultadoEnvio(EstadoEnvio.Aceptado, "TRACK-1"));

        (await app.ConEnvio(proveedor, (e, _) => e.ProcesarAsync(CancellationToken.None))).ShouldBe(1);
        var fallido = await app.ConEnvio(proveedor, (_, db) => db.Documentos.AsNoTracking().SingleAsync(d => d.Id == id));
        fallido.Estado.ShouldBe(EstadoEnvio.Pendiente);
        fallido.Intentos.ShouldBe(1);
        fallido.UltimoError.ShouldBe("proveedor caído");

        // Not before its retry time; after it, the document goes and is accepted.
        (await app.ConEnvio(proveedor, (e, _) => e.ProcesarAsync(CancellationToken.None))).ShouldBe(0);
        app.Reloj.Advance(EnvioEcf.Espera(1));
        (await app.ConEnvio(proveedor, (e, _) => e.ProcesarAsync(CancellationToken.None))).ShouldBe(1);
        var enviado = await app.ConEnvio(proveedor, (_, db) => db.Documentos.AsNoTracking().SingleAsync(d => d.Id == id));
        enviado.Estado.ShouldBe(EstadoEnvio.Aceptado);
        enviado.TrackId.ShouldBe("TRACK-1");
        enviado.UltimoError.ShouldBeNull();
    }

    [Fact]
    public async Task La_cola_muestra_pendientes_vencidos_y_simulados()
    {
        var admin = app.Cliente(await app.AdminAsync());
        var ahora = app.Reloj.GetUtcNow().UtcDateTime;
        await app.PendienteAsync("E320000098001", ahora.AddHours(-1), proximoIntentoUtc: ahora.AddDays(30));
        await app.EnScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            db.Documentos.Add(new DocumentoFiscal
            {
                Id = Guid.CreateVersion7(), ENcf = "E320000098002", Tipo = TipoEcf.Consumo32, ReferenciaId = Guid.NewGuid(), Total = 1m, FechaFirmaUtc = ahora,
                CodigoSeguridad = "X", XmlFirmado = [1], Estado = EstadoEnvio.Simulado, FechaLimiteEnvioUtc = ahora.AddHours(72),
            });
            return await db.SaveChangesAsync();
        });

        var cola = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/cola");
        cola.GetProperty("vencidos").GetInt32().ShouldBeGreaterThanOrEqualTo(1); // past the 72 hours: the owner must act
        cola.GetProperty("simulados").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        EnvioEcf.Espera(10).ShouldBe(TimeSpan.FromMinutes(60));
    }
}

/// <summary>R5 #4: failures the real provider will produce. Own app, so no other test's documents are in the batch.</summary>
public sealed class EnvioEcfFallosTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Un_timeout_del_proveedor_se_reintenta_y_un_rechazo_no()
    {
        await app.AdminAsync();
        var ahora = app.Reloj.GetUtcNow().UtcDateTime;
        var lento = await app.PendienteAsync("E320000096001", ahora.AddHours(1));       // closest deadline: sent first
        var rechazado = await app.PendienteAsync("E320000096002", ahora.AddHours(2));
        var proveedor = new ProveedorDePrueba();
        // An HTTP timeout surfaces as TaskCanceledException: it must be a retry, not a crash of the service.
        proveedor.Respuestas.Enqueue(() => throw new TaskCanceledException("timeout"));
        proveedor.Respuestas.Enqueue(() => new ResultadoEnvio(EstadoEnvio.Rechazado, "TRACK-R", "RNC del comprador inválido"));

        (await app.ConEnvio(proveedor, (e, _) => e.ProcesarAsync(CancellationToken.None))).ShouldBe(2);
        var doc = await app.ConEnvio(proveedor, (_, db) => db.Documentos.AsNoTracking().SingleAsync(d => d.Id == lento));
        (doc.Estado, doc.Intentos).ShouldBe((EstadoEnvio.Pendiente, 1));
        var rech = await app.ConEnvio(proveedor, (_, db) => db.Documentos.AsNoTracking().SingleAsync(d => d.Id == rechazado));
        (rech.Estado, rech.UltimoError).ShouldBe((EstadoEnvio.Rechazado, "RNC del comprador inválido"));

        // A rejected document is not sent again.
        app.Reloj.Advance(TimeSpan.FromHours(2));
        proveedor.Respuestas.Enqueue(() => new ResultadoEnvio(EstadoEnvio.Aceptado, "TRACK-2"));
        (await app.ConEnvio(proveedor, (e, _) => e.ProcesarAsync(CancellationToken.None))).ShouldBe(1);
    }
}
