using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Toki.BuildingBlocks.Persistence;
using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Messaging;

/// <summary>Reacts to an integration event of another module, after the change that raised it was committed.
/// Delivery is at least once: a handler must give the same result when it runs twice.</summary>
public interface IManejadorEvento<in TEvento> where TEvento : IIntegrationEvent
{
    Task ManejarAsync(TEvento evento, CancellationToken ct);
}

/// <summary>Delivers outbox messages in order to the in-process handlers, then marks them processed.
/// A failing message is logged and retried on the next pass; the messages after it still go.</summary>
/// <remarks>shortcut: no upload to the cloud yet (SubidoUtc stays empty); that comes with the cloud backend (TK-088, phase 2).</remarks>
public sealed class ProcesadorOutbox(InfraDbContext db, IServiceProvider servicios, TiposEvento tipos, TimeProvider reloj, ILogger<ProcesadorOutbox> log)
{
    public const int Lote = 100;

    /// <returns>How many messages were taken.</returns>
    public async Task<int> ProcesarAsync(CancellationToken ct)
    {
        var mensajes = await db.Outbox.Where(m => m.ProcesadoUtc == null).OrderBy(m => m.Secuencia).Take(Lote).AsNoTracking().ToListAsync(ct);
        foreach (var m in mensajes)
        {
            try
            {
                if (tipos.PorNombre.TryGetValue(m.Tipo, out var tipo))
                {
                    var evento = JsonSerializer.Deserialize(m.Payload, tipo, Json.Opciones)!;
                    foreach (var manejador in servicios.GetServices(typeof(IManejadorEvento<>).MakeGenericType(tipo)))
                        await (Task)manejador!.GetType().GetMethod(nameof(IManejadorEvento<IIntegrationEvent>.ManejarAsync))!.Invoke(manejador, [evento, ct])!;
                }
                await db.Outbox.Where(x => x.Secuencia == m.Secuencia).ExecuteUpdateAsync(s => s.SetProperty(x => x.ProcesadoUtc, reloj.GetUtcNow().UtcDateTime), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "No se pudo procesar el evento {Tipo} {Id}; se reintenta en la próxima pasada", m.Tipo, m.Id);
            }
        }
        return mensajes.Count;
    }
}

/// <summary>Event types that have handlers, by full name: the outbox stores the name, the handlers know the type.</summary>
public sealed record TiposEvento(IReadOnlyDictionary<string, Type> PorNombre);

/// <summary>Runs <see cref="ProcesadorOutbox"/> every <c>Outbox:IntervaloSegundos</c> (5), unless <c>Outbox:Procesar</c> is false
/// (the in-memory test host, whose tests run it themselves against a fake clock).</summary>
internal sealed class ProcesadorOutboxWorker(IServiceScopeFactory scopes, IConfiguration config, TimeProvider reloj, ILogger<ProcesadorOutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Outbox:Procesar", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.GetValue("Outbox:IntervaloSegundos", 5)), reloj);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var procesador = scope.ServiceProvider.GetRequiredService<ProcesadorOutbox>();
                while (await procesador.ProcesarAsync(stoppingToken) == ProcesadorOutbox.Lote) { }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Falló el procesador del outbox");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Degraded when an event waits more than 10 minutes: a handler keeps failing.</summary>
internal sealed class OutboxHealthCheck(InfraDbContext db, TimeProvider reloj) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var limite = reloj.GetUtcNow().UtcDateTime.AddMinutes(-10);
        return await db.Outbox.AnyAsync(m => m.ProcesadoUtc == null && m.OcurridoUtc < limite, cancellationToken)
            ? HealthCheckResult.Degraded("Hay eventos sin procesar desde hace más de 10 minutos.")
            : HealthCheckResult.Healthy();
    }
}
