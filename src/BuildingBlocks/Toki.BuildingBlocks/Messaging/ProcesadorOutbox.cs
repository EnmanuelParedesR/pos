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
/// A failing message is logged and retried on the next run; the messages after it still go. Each message has its own DI scope,
/// so a handler that fails halfway leaves nothing tracked for the next one.</summary>
/// <remarks>shortcut: no upload to the cloud yet (SubidoUtc stays empty); that comes with the cloud backend (TK-088, phase 2).</remarks>
public sealed class ProcesadorOutbox(InfraDbContext db, IServiceScopeFactory scopes, TiposEvento tipos, TimeProvider reloj, ILogger<ProcesadorOutbox> log)
{
    public const int Lote = 100;

    /// <summary>One run over everything pending. Each message is read once per run (a watermark), so failing ones cannot keep the run spinning.</summary>
    /// <returns>How many messages were taken.</returns>
    public async Task<int> ProcesarAsync(CancellationToken ct)
    {
        long desde = 0;
        var total = 0;
        while (true)
        {
            var mensajes = await db.Outbox.Where(m => m.ProcesadoUtc == null && m.Secuencia > desde).OrderBy(m => m.Secuencia).Take(Lote).AsNoTracking().ToListAsync(ct);
            foreach (var m in mensajes) await EntregarAsync(m, ct);
            total += mensajes.Count;
            if (mensajes.Count < Lote) return total;
            desde = mensajes[^1].Secuencia;
        }
    }

    private async Task EntregarAsync(OutboxMensaje m, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (tipos.PorNombre.TryGetValue(m.Tipo, out var tipo))
            {
                var evento = JsonSerializer.Deserialize(m.Payload, tipo, Json.Opciones)!;
                foreach (var manejador in scope.ServiceProvider.GetServices(typeof(IManejadorEvento<>).MakeGenericType(tipo)))
                    await (Task)manejador!.GetType().GetMethod(nameof(IManejadorEvento<IIntegrationEvent>.ManejarAsync))!.Invoke(manejador, [evento, ct])!;
            }
            await db.Outbox.Where(x => x.Secuencia == m.Secuencia).ExecuteUpdateAsync(s => s.SetProperty(x => x.ProcesadoUtc, reloj.GetUtcNow().UtcDateTime), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "No se pudo procesar el evento {Tipo} {Id}; se reintenta en la próxima pasada", m.Tipo, m.Id);
        }
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
                await procesador.ProcesarAsync(stoppingToken);
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
