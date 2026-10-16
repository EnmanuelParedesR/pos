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
using Toki.Settings.Contracts;
using Toki.Sync.Contracts;

namespace Toki.IntegrationTests.Fiscal;

/// <summary>A DGII that answers whatever each test queues, in order, and records which operation was called.</summary>
internal sealed class TestEcfProvider : IEcfProvider
{
    public Queue<Func<DgiiAnswer?>> Responses { get; } = new();
    public List<string> Calls { get; } = [];

    public bool IsSimulated => false;
    public FiscalEnvironment Environment => FiscalEnvironment.TesteCF;

    private Task<DgiiAnswer> Next(string call)
    {
        Calls.Add(call);
        return Task.FromResult(Responses.Dequeue()()!);
    }

    public Task<DgiiAnswer> SendEcfAsync(string issuerRnc, string encf, string signedXml, CancellationToken ct) => Next($"send {encf}");
    public Task<DgiiAnswer> GetResultAsync(string trackId, CancellationToken ct) => Next($"result {trackId}");
    public Task<DgiiAnswer> SendRfceAsync(string issuerRnc, string encf, string signedRfce, CancellationToken ct) => Next($"rfce {encf}");
    public Task<DgiiAnswer> VoidAsync(string issuerRnc, string signedAnecf, CancellationToken ct) => Next("void");

    public Task<DgiiAnswer?> FindAsync(string issuerRnc, string encf, string securityCode, EcfChannel channel, CancellationToken ct)
    {
        Calls.Add($"find {encf}");
        return Task.FromResult(Responses.Dequeue()());
    }
}

internal static class TestQueue
{
    public static Task<T> WithQueue<T>(this TokiApp app, IEcfProvider provider, Func<EcfSubmissionQueue, FiscalDbContext, Task<T>> action) => app.InScopeAsync(sp =>
    {
        var db = sp.GetRequiredService<FiscalDbContext>();
        return action(new EcfSubmissionQueue(db, provider, sp.GetRequiredService<ISettingsQueries>(), sp.GetRequiredService<ICloudClient>(), app.Clock,
            NullLogger<EcfSubmissionQueue>.Instance), db);
    });

    public static Task<int> ProcessAsync(this TokiApp app, IEcfProvider provider) => app.WithQueue(provider, (q, _) => q.ProcessAsync(CancellationToken.None));

    public static Task<FiscalDocument> DocumentAsync(this TokiApp app, Guid id) =>
        app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().SingleAsync(d => d.Id == id));

    /// <summary>A signed document waiting to be sent, as a real signer would leave it.</summary>
    public static Task<Guid> PendingAsync(this TokiApp app, string encf, DateTime deadlineUtc, DateTime? nextAttemptUtc = null, EcfChannel channel = EcfChannel.Ecf) =>
        app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            var d = new FiscalDocument
            {
                Id = Guid.CreateVersion7(), ENcf = encf, Type = EcfType.Consumer32, ReferenceId = Guid.NewGuid(), Total = 1m, SignedUtc = app.Clock.GetUtcNow().UtcDateTime,
                SecurityCode = "X", SignedXml = EcfSubmissionQueue.Zip("<ECF/>"), Status = SubmissionStatus.Pending, SubmitDeadlineUtc = deadlineUtc,
                NextAttemptUtc = nextAttemptUtc, Channel = channel, RfceXml = channel == EcfChannel.Rfce ? EcfSubmissionQueue.Zip("<RFCE/>") : null,
                Environment = FiscalEnvironment.TesteCF,
            };
            db.Documents.Add(d);
            await db.SaveChangesAsync();
            return d.Id;
        });
}

/// <summary>TK-146: the queue's state machine with a scripted DGII. Own app: it writes queue rows directly.</summary>
public sealed class EcfSubmissionTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task A_failure_is_retried_then_sent_with_a_TrackId_polled_while_in_process_and_accepted()
    {
        await app.AdminAsync();
        var id = await app.PendingAsync("E310000099001", app.Clock.GetUtcNow().UtcDateTime.AddHours(72));
        var dgii = new TestEcfProvider();
        dgii.Responses.Enqueue(() => throw new HttpRequestException("DGII caída"));

        (await app.ProcessAsync(dgii)).ShouldBe(1);
        var failed = await app.DocumentAsync(id);
        (failed.Status, failed.Attempts, failed.LastError).ShouldBe((SubmissionStatus.Pending, 1, "DGII caída"));

        // Not before its retry time. After it, the queue first asks whether the failed send arrived (it did not), then sends.
        (await app.ProcessAsync(dgii)).ShouldBe(0);
        app.Clock.Advance(EcfSubmissionQueue.RetryDelay(1));
        dgii.Responses.Enqueue(() => null);
        dgii.Responses.Enqueue(() => new DgiiAnswer(DgiiState.InProcess, "TRACK-1"));
        (await app.ProcessAsync(dgii)).ShouldBe(1);
        var sent = await app.DocumentAsync(id);
        (sent.Status, sent.TrackId, sent.LastError, sent.SentUtc).ShouldBe((SubmissionStatus.Sent, "TRACK-1", null, app.Clock.GetUtcNow().UtcDateTime));

        // "En proceso": asked again after the poll delay, then the final answer.
        app.Clock.Advance(EcfSubmissionQueue.PollDelay(0));
        dgii.Responses.Enqueue(() => new DgiiAnswer(DgiiState.InProcess, "TRACK-1"));
        (await app.ProcessAsync(dgii)).ShouldBe(1);
        (await app.ProcessAsync(dgii)).ShouldBe(0);
        app.Clock.Advance(EcfSubmissionQueue.PollDelay(1));
        dgii.Responses.Enqueue(() => new DgiiAnswer(DgiiState.AcceptedConditional, "TRACK-1", true, "1: Descuadre en totales"));
        (await app.ProcessAsync(dgii)).ShouldBe(1);
        var done = await app.DocumentAsync(id);
        (done.Status, done.DgiiCode, done.DgiiMessages, done.ResultUtc).ShouldBe((SubmissionStatus.AcceptedConditional, 4, "1: Descuadre en totales",
            app.Clock.GetUtcNow().UtcDateTime));
        dgii.Calls.ShouldBe(["send E310000099001", "find E310000099001", "send E310000099001", "result TRACK-1", "result TRACK-1"]);
    }
}

/// <summary>TK-146: a send that timed out but arrived, and the RFCE. Own app.</summary>
public sealed class EcfSubmissionTimeoutTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task A_timeout_that_reached_the_DGII_is_not_sent_twice_and_an_RFCE_ends_in_one_call()
    {
        await app.AdminAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var full = await app.PendingAsync("E310000097001", now.AddMinutes(1));
        var summary = await app.PendingAsync("E320000097002", now.AddMinutes(2), channel: EcfChannel.Rfce);
        var dgii = new TestEcfProvider();
        // An HTTP timeout surfaces as TaskCanceledException: a retry, not a crash of the service.
        dgii.Responses.Enqueue(() => throw new TaskCanceledException("timeout"));
        dgii.Responses.Enqueue(() => new DgiiAnswer(DgiiState.Accepted, SequenceUsed: true));

        (await app.ProcessAsync(dgii)).ShouldBe(2);
        (await app.DocumentAsync(full)).Status.ShouldBe(SubmissionStatus.Pending);
        var rfce = await app.DocumentAsync(summary);
        (rfce.Status, rfce.DgiiCode, rfce.TrackId).ShouldBe((SubmissionStatus.Accepted, 1, null));

        // The DGII already has it: the queue takes that TrackId instead of sending again.
        app.Clock.Advance(EcfSubmissionQueue.RetryDelay(1));
        dgii.Responses.Enqueue(() => new DgiiAnswer(DgiiState.InProcess, "TRACK-FOUND"));
        (await app.ProcessAsync(dgii)).ShouldBe(1);
        var found = await app.DocumentAsync(full);
        (found.Status, found.TrackId).ShouldBe((SubmissionStatus.Sent, "TRACK-FOUND"));
        dgii.Calls.ShouldBe(["send E310000097001", "rfce E320000097002", "find E310000097001"]);
    }
}

/// <summary>R8 #2: a void of a document "never sent" that comes while the queue is sending it. Own app.</summary>
public sealed class EcfVoidRaceTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task A_void_while_the_queue_sends_the_document_is_refused_and_the_DGII_never_gets_both()
    {
        await app.SetUpBusinessAsync();
        var admin = app.Client(await app.AdminAsync());
        var id = await app.PendingAsync("E320000095001", app.Clock.GetUtcNow().UtcDateTime.AddHours(72));
        HttpResponseMessage? voided = null;
        var dgii = new TestEcfProvider();
        // The owner taps "Anular" while the DGII is still answering the send.
        dgii.Responses.Enqueue(() =>
        {
            voided = Task.Run(() => admin.PostAsync($"/api/fiscal/documents/{id}/void", null)).GetAwaiter().GetResult();
            return new DgiiAnswer(DgiiState.Accepted, SequenceUsed: true);
        });

        (await app.ProcessAsync(dgii)).ShouldBe(1);
        (await voided!.ErrorCode()).ShouldBe("Fiscal.AlreadySent");
        (await app.DocumentAsync(id)).Status.ShouldBe(SubmissionStatus.Accepted);
        (await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Voids.CountAsync())).ShouldBe(0); // no ANECF for an accepted e-CF

        // A void that wins (before the queue reaches the document) leaves it alone: the queue skips it.
        var other = await app.PendingAsync("E320000095002", app.Clock.GetUtcNow().UtcDateTime.AddHours(72));
        (await admin.PostAsync($"/api/fiscal/documents/{other}/void", null)).StatusCode.ShouldBe(System.Net.HttpStatusCode.Created);
        (await app.ProcessAsync(dgii)).ShouldBe(0);
        (await app.DocumentAsync(other)).Status.ShouldBe(SubmissionStatus.Voided);
        dgii.Calls.ShouldBe(["send E320000095001"]);
    }
}

/// <summary>TK-146/147: a rejection. Own app.</summary>
public sealed class EcfSubmissionRejectionTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task A_rejection_is_final_and_shows_in_the_queue_until_it_is_replaced()
    {
        var admin = app.Client(await app.AdminAsync());
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var id = await app.PendingAsync("E320000096001", now.AddHours(-1), channel: EcfChannel.Rfce); // already past the 72 hours
        var queue = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/queue");
        queue.GetProperty("overdue").GetInt32().ShouldBeGreaterThanOrEqualTo(1); // the owner must act

        var dgii = new TestEcfProvider();
        dgii.Responses.Enqueue(() => new DgiiAnswer(DgiiState.Rejected, SequenceUsed: false, Messages: "2: El RNC del comprador no existe"));
        await app.ProcessAsync(dgii);
        var rejected = await app.DocumentAsync(id);
        (rejected.Status, rejected.SequenceUsed, rejected.DgiiMessages).ShouldBe((SubmissionStatus.Rejected, false, "2: El RNC del comprador no existe"));

        app.Clock.Advance(TimeSpan.FromHours(2));
        (await app.ProcessAsync(dgii)).ShouldBe(0); // never sent again
        queue = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/queue");
        queue.GetProperty("rejected").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/documents?rejectedOnly=true");
        list.EnumerateArray().Select(d => d.GetProperty("eNcf").GetString()).ShouldContain("E320000096001");
        EcfSubmissionQueue.RetryDelay(10).ShouldBe(TimeSpan.FromMinutes(60));
        EcfSubmissionQueue.PollDelay(20).ShouldBe(TimeSpan.FromMinutes(5));
    }
}
