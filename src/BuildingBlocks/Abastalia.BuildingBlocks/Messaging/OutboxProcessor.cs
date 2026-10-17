using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.BuildingBlocks.Messaging;

/// <summary>Reacts to an integration event of another module, after the change that raised it was committed.
/// Delivery is at least once: a handler must give the same result when it runs twice.</summary>
public interface IEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent @event, CancellationToken ct);
}

/// <summary>Delivers outbox messages in order to the in-process handlers, then marks them processed.
/// A failing message is logged and retried on the next run; the messages after it still go. Each message has its own DI scope,
/// so a handler that fails halfway leaves nothing tracked for the next one.</summary>
/// <remarks>The upload to the cloud is separate (Abastalia.Sync's CloudUploader, column UploadedUtc); this only sets ProcessedUtc.</remarks>
public sealed class OutboxProcessor(InfraDbContext db, IServiceScopeFactory scopes, EventTypes types, TimeProvider clock, ILogger<OutboxProcessor> log)
{
    public const int BatchSize = 100;

    /// <summary>One run over everything pending. Each message is read once per run (a watermark), so failing ones cannot keep the run spinning.</summary>
    /// <returns>How many messages were taken.</returns>
    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        long after = 0;
        var total = 0;
        while (true)
        {
            var messages = await db.Outbox.Where(m => m.ProcessedUtc == null && m.Sequence > after).OrderBy(m => m.Sequence).Take(BatchSize).AsNoTracking().ToListAsync(ct);
            foreach (var m in messages) await DeliverAsync(m, ct);
            total += messages.Count;
            if (messages.Count < BatchSize) return total;
            after = messages[^1].Sequence;
        }
    }

    /// <summary>TK-304: how long a delivered and uploaded event stays (the cloud has it; nothing here reads it again).</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);

    /// <summary>Deletes the events both processed here and uploaded to the cloud more than <see cref="KeepFor"/> ago, in small batches so a sale
    /// writing its own event never waits long. An event the cloud has not confirmed stays, however old.</summary>
    /// <returns>How many were deleted.</returns>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var before = clock.GetUtcNow().UtcDateTime - KeepFor;
        var total = 0;
        int deleted;
        do
        {
            deleted = await db.Outbox.Where(m => m.ProcessedUtc < before && m.UploadedUtc < before).OrderBy(m => m.Sequence).Take(1000).ExecuteDeleteAsync(ct);
            total += deleted;
        } while (deleted == 1000);
        return total;
    }

    private async Task DeliverAsync(OutboxMessage m, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            if (types.ByName.TryGetValue(m.Type, out var type))
            {
                var @event = JsonSerializer.Deserialize(m.Payload, type, Json.Options)!;
                foreach (var handler in scope.ServiceProvider.GetServices(typeof(IEventHandler<>).MakeGenericType(type)))
                    await (Task)handler!.GetType().GetMethod(nameof(IEventHandler<IIntegrationEvent>.HandleAsync))!.Invoke(handler, [@event, ct])!;
            }
            await db.Outbox.Where(x => x.Sequence == m.Sequence).ExecuteUpdateAsync(s => s.SetProperty(x => x.ProcessedUtc, clock.GetUtcNow().UtcDateTime), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Could not process event {Type} {Id}; it is retried on the next run", m.Type, m.Id);
        }
    }
}

/// <summary>Event types that have handlers, by full name: the outbox stores the name, the handlers know the type.</summary>
public sealed record EventTypes(IReadOnlyDictionary<string, Type> ByName);

/// <summary>Runs <see cref="OutboxProcessor"/> every <c>Outbox:IntervalSeconds</c> (5), unless <c>Outbox:Process</c> is false
/// (the in-memory test host, whose tests run it themselves against a fake clock).</summary>
internal sealed class OutboxProcessorWorker(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<OutboxProcessorWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Outbox:Process", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.GetValue("Outbox:IntervalSeconds", 5)), clock);
        var purged = DateTimeOffset.MinValue;
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                await processor.ProcessAsync(stoppingToken);
                if (clock.GetUtcNow() - purged >= TimeSpan.FromHours(1))
                {
                    purged = clock.GetUtcNow();
                    await processor.PurgeAsync(stoppingToken); // TK-304: the table does not grow forever
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "The outbox processor failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Degraded when an event waits more than 10 minutes: a handler keeps failing.</summary>
internal sealed class OutboxHealthCheck(InfraDbContext db, TimeProvider clock) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var limit = clock.GetUtcNow().UtcDateTime.AddMinutes(-10);
        return await db.Outbox.AnyAsync(m => m.ProcessedUtc == null && m.OccurredUtc < limit, cancellationToken)
            ? HealthCheckResult.Degraded("Some events have been waiting more than 10 minutes to be processed.")
            : HealthCheckResult.Healthy();
    }
}
