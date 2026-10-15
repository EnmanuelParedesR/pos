using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Toki.Fiscal.Domain;

namespace Toki.Fiscal;

/// <summary>The contracted e-CF provider (it signs nothing: it receives the signed XML and talks to the DGII).
/// Throws on a transient failure (network, provider down): the document is retried. Returns Rejected when the DGII refuses it.</summary>
/// <remarks>shortcut: one call returns the final state; the real provider (TK-055) answers with a TrackId and the state is queried later.</remarks>
internal interface IEcfProvider
{
    Task<SubmissionResult> SubmitAsync(string encf, byte[] signedXmlGzip, CancellationToken ct);
}

internal sealed record SubmissionResult(SubmissionStatus Status, string? TrackId, string? Message = null);

/// <summary>Stand-in until a provider is contracted (e-CF on hold). Nothing reaches DGII, so nothing is marked accepted.</summary>
internal sealed class SimulatedEcfProvider : IEcfProvider
{
    public Task<SubmissionResult> SubmitAsync(string encf, byte[] signedXmlGzip, CancellationToken ct) =>
        Task.FromResult(new SubmissionResult(SubmissionStatus.Simulated, null));
}

/// <summary>Sends pending e-CF, closest DGII deadline first. A failure waits 1, 2, 4… up to 60 minutes before the next try.
/// The sale never waits for this: it was committed with the document as Pending (contingency up to 72 hours).</summary>
internal sealed class EcfSubmissionQueue(FiscalDbContext db, IEcfProvider provider, TimeProvider clock, ILogger<EcfSubmissionQueue> log)
{
    public const int BatchSize = 20;

    /// <returns>How many documents were tried.</returns>
    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var documents = await db.Documents
            .Where(d => d.Status == SubmissionStatus.Pending && (d.NextAttemptUtc == null || d.NextAttemptUtc <= now))
            .OrderBy(d => d.SubmitDeadlineUtc).Take(BatchSize).ToListAsync(ct);
        foreach (var d in documents)
        {
            try
            {
                var r = await provider.SubmitAsync(d.ENcf, d.SignedXml, ct);
                (d.Status, d.TrackId, d.LastError, d.NextAttemptUtc) = (r.Status, r.TrackId, r.Message, null);
            }
            // An HTTP timeout is a TaskCanceledException too: only our own shutdown token ends the pass.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                d.Attempts++;
                d.NextAttemptUtc = now + RetryDelay(d.Attempts);
                d.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                log.LogWarning(ex, "Could not send e-CF {ENcf} (attempt {Attempts})", d.ENcf, d.Attempts);
            }
            // One save per document: a crash after the provider answered resends at most one (the provider dedupes by e-NCF).
            await db.SaveChangesAsync(ct);
        }
        return documents.Count;
    }

    public static TimeSpan RetryDelay(int attempts) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(attempts, 7) - 1)));
}

/// <summary>Runs <see cref="EcfSubmissionQueue"/> every <c>Fiscal:SubmitIntervalSeconds</c> (30) when <c>Fiscal:AutoSubmit</c> is true.</summary>
internal sealed class EcfSubmissionWorker(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<EcfSubmissionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue<bool>("Fiscal:AutoSubmit")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.GetValue("Fiscal:SubmitIntervalSeconds", 30)), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<EcfSubmissionQueue>();
                while (await queue.ProcessAsync(stoppingToken) == EcfSubmissionQueue.BatchSize) { }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "The e-CF submission queue failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
