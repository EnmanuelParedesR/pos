using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Toki.Host.Cloud.Persistence;

namespace Toki.Host.Cloud.Ingest;

/// <summary>Wakes the applier after a batch is received.</summary>
public sealed class InboxSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Notify() => _channel.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}

/// <summary>Applies received events with their projector, in arrival order, one transaction per event (projection + AppliedUtc together).
/// Unknown types stay stored and unapplied; they are picked up once a projector for them exists (after a deploy, on start).</summary>
public sealed class InboxApplier(IServiceScopeFactory scopes, TimeProvider clock, ILogger<InboxApplier> log)
{
    /// <summary>An event that keeps failing stops being retried and stays visible with its error.</summary>
    public const int MaxAttempts = 10;

    /// <returns>How many events were applied.</returns>
    public async Task<int> ApplyPendingAsync(CancellationToken ct)
    {
        var types = Projectors.ByType.Keys.ToList();
        var applied = 0;
        long after = 0; // each event is tried at most once per run
        while (true)
        {
            List<long> batch;
            await using (var scope = scopes.CreateAsyncScope())
            {
                batch = await scope.ServiceProvider.GetRequiredService<CloudDbContext>().Inbox
                    .Where(i => i.Sequence > after && i.AppliedUtc == null && i.Attempts < MaxAttempts && types.Contains(i.Type))
                    .OrderBy(i => i.Sequence).Select(i => i.Sequence).Take(100).ToListAsync(ct);
            }
            if (batch.Count == 0)
            {
                if (applied > 0) await AlertAsync(ct);
                await PurgeAsync(ct);
                return applied;
            }
            foreach (var sequence in batch)
            {
                if (await ApplyAsync(sequence, ct)) applied++;
                after = sequence;
            }
        }
    }

    /// <summary>TK-222: the alerts look at what just arrived, while the database is awake anyway. A failure never stops the applier.</summary>
    private async Task AlertAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<Alerts.AlertNotifier>().RunAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "The alerts run failed");
        }
    }

    /// <summary>TK-251: applied events live on in the replica; their inbox rows go after this long. A store re-sending one that old
    /// gets it applied again, which is harmless (projections replace by id).</summary>
    public static readonly TimeSpan KeepApplied = TimeSpan.FromDays(30);

    /// <summary>Deletes applied inbox rows older than <see cref="KeepApplied"/>, in batches so no statement holds a long lock.
    /// Failed and unknown events are kept: they are still waiting.</summary>
    private async Task PurgeAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
            var before = clock.GetUtcNow().UtcDateTime - KeepApplied;
            int deleted;
            do
            {
                var old = db.Inbox.Where(i => i.AppliedUtc != null && i.AppliedUtc < before).OrderBy(i => i.Sequence).Select(i => i.Sequence).Take(1000);
                deleted = await db.Inbox.Where(i => old.Contains(i.Sequence)).ExecuteDeleteAsync(ct);
            } while (deleted > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "The inbox purge failed");
        }
    }

    private async Task<bool> ApplyAsync(long sequence, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
            // Azure SQL retries transient errors (EnableRetryOnFailure), which needs the transaction inside the execution strategy.
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var e = await db.Inbox.SingleAsync(i => i.Sequence == sequence, ct);
                if (e.AppliedUtc is not null) return;
                await Projectors.ApplyAsync(scope.ServiceProvider, db, e, ct);
                e.AppliedUtc = clock.GetUtcNow().UtcDateTime;
                e.Error = null;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No payload in the log: it can carry customer data. R11 B-Low2: nor the exception's text, which can quote values (a truncated column's
            // value, a JSON fragment): only its kind, here and in the row the panel shows.
            var error = ex is JsonException { Path: { } path } ? $"{nameof(JsonException)} at {path}" : ex.GetType().Name + (ex.InnerException is { } inner ? $" ({inner.GetType().Name})" : "");
            log.LogWarning("Inbox event {Sequence} could not be applied: {Error}", sequence, error);
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CloudDbContext>().Inbox.Where(i => i.Sequence == sequence)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Attempts, i => i.Attempts + 1).SetProperty(i => i.Error, error), ct);
            return false;
        }
    }
}

/// <summary>Runs the applier on start and after every received batch.</summary>
/// <remarks>shortcut: no periodic poll, so the serverless database can pause at night; a failed event is retried with the next batch.
/// One App Service instance (B1); with several, two appliers could replay the same event (harmless: projections replace by id).</remarks>
internal sealed class InboxApplierWorker(InboxApplier applier, InboxSignal signal, IConfiguration config, ILogger<InboxApplierWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Ingest:ApplyInBackground", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await applier.ApplyPendingAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Inbox applier run failed"); }
            await signal.WaitAsync(stoppingToken);
        }
    }
}
