using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Toki.Fiscal.Domain;
using Toki.Settings.Contracts;
using Toki.Sync.Contracts;

namespace Toki.Fiscal;

/// <summary>TK-146: sends signed documents straight to the DGII, closest 72-hour deadline first.
/// <list type="bullet">
/// <item>Full e-CF (31, 33, 34 and 32 of RD$250,000 or more): Pending → recepción → Sent (TrackId) → consultaresultado every 5 s, 10 s… up to
/// 5 minutes while "en proceso" → Accepted / AcceptedConditional / Rejected.</item>
/// <item>RFCE (32 under RD$250,000): Pending → recepcionfc → final state in the same call.</item>
/// <item>A failure (network, timeout, DGII down) waits 1, 2, 4… up to 60 minutes. Before sending again after a failure, it asks the DGII
/// what it already has for that e-NCF, so a send that timed out but arrived is not duplicated.</item>
/// <item>With the DGII provider, every final document goes to the cloud archive (XML, RFCE and result): no submission there (ADR-0008 changed by ecf-propio §10.4).</item>
/// </list>
/// The sale never waits for this: it was committed with the document as Pending (contingency up to 72 hours).</summary>
internal sealed class EcfSubmissionQueue(FiscalDbContext db, IEcfProvider provider, ISettingsQueries settings, ICloudClient cloud, TimeProvider clock,
    ILogger<EcfSubmissionQueue> log)
{
    public const int BatchSize = 20;
    public const string ArchivePath = "/fiscal/v1/archive";

    /// <returns>How many documents were tried (sends and polls; archive uploads do not count).</returns>
    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var documents = await db.Documents
            .Where(d => (d.Status == SubmissionStatus.Pending || d.Status == SubmissionStatus.Sent) && (d.NextAttemptUtc == null || d.NextAttemptUtc <= now))
            .OrderBy(d => d.SubmitDeadlineUtc).Take(BatchSize).ToListAsync(ct);
        var voids = await db.Voids.Where(v => v.Status == SubmissionStatus.Pending && (v.NextAttemptUtc == null || v.NextAttemptUtc <= now))
            .OrderBy(v => v.CreatedUtc).Take(BatchSize).ToListAsync(ct);
        var rnc = documents.Count + voids.Count > 0 ? (await settings.GetBusinessProfileAsync(ct)).Rnc ?? "" : "";

        foreach (var d in documents)
        {
            // R8 #2: claims a pending document before calling the DGII. A void needs SentUtc null, so it can no longer win; one that already won
            // (it changed the status after the load above) leaves 0 rows and the document is skipped.
            // R9 #7: a pending document that still has its claim was being sent when the process died: that send may have arrived.
            var mayHaveArrived = d.Attempts > 0 || d.SentUtc is not null;
            if (d.Status == SubmissionStatus.Pending && !await ClaimAsync(d, now, ct)) continue;
            try
            {
                var answer = d.Status == SubmissionStatus.Sent ? await provider.GetResultAsync(d.TrackId!, ct) : await SendAsync(d, rnc, mayHaveArrived, ct);
                Apply(d, answer, now);
            }
            // An HTTP timeout is a TaskCanceledException too: only our own shutdown token ends the pass.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Failed(d.ENcf, ex, ++d.Attempts, now, (next, error) => (d.NextAttemptUtc, d.LastError) = (next, error));
                if (d.Status == SubmissionStatus.Pending) d.SentUtc = null; // releases the claim: it can be voided again (the void asks the DGII first)
            }
            // One save per document: a crash after the DGII answered loses at most one answer, and the next pass asks before resending.
            await db.SaveChangesAsync(ct);
        }

        foreach (var v in voids)
        {
            try
            {
                var answer = await provider.VoidAsync(rnc, Unzip(v.SignedXml), ct);
                (v.Status, v.Message, v.ResultUtc, v.NextAttemptUtc) = (Final(answer.State), answer.Messages, now, null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Failed($"ANECF {v.Id}", ex, ++v.Attempts, now, (next, error) => (v.NextAttemptUtc, v.Message) = (next, error));
            }
            await db.SaveChangesAsync(ct);
        }

        if (!provider.IsSimulated) await ArchiveAsync(ct);
        return documents.Count + voids.Count;
    }

    private async Task<bool> ClaimAsync(FiscalDocument d, DateTime now, CancellationToken ct)
    {
        if (await db.Documents.Where(x => x.Id == d.Id && x.Status == SubmissionStatus.Pending)
                .ExecuteUpdateAsync(x => x.SetProperty(q => q.SentUtc, now), ct) != 1)
        {
            db.Entry(d).State = EntityState.Detached;
            return false;
        }
        var sent = db.Entry(d).Property(x => x.SentUtc);
        (sent.OriginalValue, sent.CurrentValue) = (now, now);
        return true;
    }

    /// <param name="mayHaveArrived">A previous try failed (a timeout after delivery) or was cut by a crash: ask the DGII before sending.</param>
    private async Task<DgiiAnswer> SendAsync(FiscalDocument d, string rnc, bool mayHaveArrived, CancellationToken ct)
    {
        if (mayHaveArrived && await provider.FindAsync(rnc, d.ENcf, d.SecurityCode, d.Channel, ct) is { } known) return known;
        return d.Channel == EcfChannel.Rfce
            ? await provider.SendRfceAsync(rnc, d.ENcf, Unzip(d.RfceXml!), ct)
            : await provider.SendEcfAsync(rnc, d.ENcf, Unzip(d.SignedXml), ct);
    }

    private static void Apply(FiscalDocument d, DgiiAnswer a, DateTime now)
    {
        d.LastError = null;
        if (a.State == DgiiState.Simulated)
        {
            (d.Status, d.NextAttemptUtc) = (SubmissionStatus.Simulated, null);
            return;
        }
        if (a.State is DgiiState.InProcess or DgiiState.NotFound)
        {
            // Just sent, or still being validated: ask again later.
            if (d.Status == SubmissionStatus.Pending) (d.Status, d.TrackId, d.SentUtc, d.Attempts) = (SubmissionStatus.Sent, a.TrackId, now, 0);
            else d.Attempts++;
            d.NextAttemptUtc = now + PollDelay(d.Attempts);
            return;
        }
        d.SentUtc ??= now;
        (d.Status, d.DgiiCode, d.DgiiMessages, d.SequenceUsed, d.ResultUtc, d.NextAttemptUtc) =
            (Final(a.State), (int)a.State, a.Messages, a.SequenceUsed, now, null);
        d.TrackId ??= a.TrackId;
    }

    private static SubmissionStatus Final(DgiiState state) => state switch
    {
        DgiiState.Accepted => SubmissionStatus.Accepted,
        DgiiState.AcceptedConditional => SubmissionStatus.AcceptedConditional,
        DgiiState.Simulated => SubmissionStatus.Simulated,
        _ => SubmissionStatus.Rejected,
    };

    private void Failed(string what, Exception ex, int attempts, DateTime now, Action<DateTime, string> set)
    {
        set(now + RetryDelay(attempts), ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message);
        log.LogWarning(ex, "Could not send {Document} to the DGII (attempt {Attempts})", what, attempts);
    }

    /// <summary>Uploads final documents to the cloud archive (10-year copy outside the store). Best effort: a store not connected to the
    /// cloud, or a cloud that is down, never holds up the DGII queue; the next pass tries again.</summary>
    private async Task ArchiveAsync(CancellationToken ct)
    {
        var final = new[] { SubmissionStatus.Accepted, SubmissionStatus.AcceptedConditional, SubmissionStatus.Rejected };
        var documents = await db.Documents.Where(d => d.ArchivedUtc == null && final.Contains(d.Status)).OrderBy(d => d.ResultUtc).Take(BatchSize).ToListAsync(ct);
        foreach (var d in documents)
        {
            try
            {
                using var response = await cloud.PostAsJsonAsync(ArchivePath, new ArchiveRequest(d.ENcf, d.Environment.ToString(), d.Status.ToString(), d.TrackId,
                    d.DgiiCode, d.DgiiMessages, d.SignedUtc, Convert.ToBase64String(d.SignedXml), d.RfceXml is null ? null : Convert.ToBase64String(d.RfceXml),
                    d.ReplacesENcf, d.ReplacedByENcf), ct);
                if (!response.IsSuccessStatusCode) return;
                d.ArchivedUtc = clock.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
            }
            catch (InvalidOperationException) { return; } // not connected to the cloud
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Could not archive e-CF {ENcf} in the cloud", d.ENcf);
                return;
            }
        }
    }

    /// <param name="SignedXmlGzip">Base64 of the gzip of the signed e-CF, as stored.</param>
    internal sealed record ArchiveRequest(string Encf, string Environment, string Status, string? TrackId, int? DgiiCode, string? Messages,
        DateTime SignedUtc, string SignedXmlGzip, string? RfceGzip, string? ReplacesENcf, string? ReplacedByENcf);

    public static TimeSpan RetryDelay(int attempts) => TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(attempts, 7) - 1)));

    /// <summary>Result polls: 5 s, 10 s, 20 s… up to 5 minutes (the DGII validates in about 200 ms on average).</summary>
    public static TimeSpan PollDelay(int polls) => TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(polls, 6))));

    internal static string Unzip(byte[] gzip)
    {
        using var z = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
        using var reader = new StreamReader(z, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static byte[] Zip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize)) gzip.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }
}

/// <summary>Runs <see cref="EcfSubmissionQueue"/> every <c>Fiscal:SubmitIntervalSeconds</c> (30) while <see cref="FiscalMode.AutoSubmit"/> is true
/// (TK-238: read on every tick, so a change on screen applies without a restart).</summary>
internal sealed class EcfSubmissionWorker(IServiceScopeFactory scopes, IConfiguration config, FiscalMode mode, TimeProvider clock, ILogger<EcfSubmissionWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(config.GetValue("Fiscal:SubmitIntervalSeconds", 30)), clock);
        do
        {
            if (!mode.AutoSubmit) continue;
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
