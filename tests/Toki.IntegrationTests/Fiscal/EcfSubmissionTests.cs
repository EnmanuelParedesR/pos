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
internal sealed class TestEcfProvider : IEcfProvider
{
    public Queue<Func<SubmissionResult>> Responses { get; } = new();

    public Task<SubmissionResult> SubmitAsync(string encf, byte[] signedXmlGzip, CancellationToken ct) => Task.FromResult(Responses.Dequeue()());
}

internal static class TestQueue
{
    public static Task<T> WithQueue<T>(this TokiApp app, TestEcfProvider provider, Func<EcfSubmissionQueue, FiscalDbContext, Task<T>> action) => app.InScopeAsync(sp =>
    {
        var db = sp.GetRequiredService<FiscalDbContext>();
        return action(new EcfSubmissionQueue(db, provider, app.Clock, NullLogger<EcfSubmissionQueue>.Instance), db);
    });

    /// <summary>A signed document waiting to be sent, as a real signer would leave it.</summary>
    public static Task<Guid> PendingAsync(this TokiApp app, string encf, DateTime deadlineUtc, DateTime? nextAttemptUtc = null) => app.InScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<FiscalDbContext>();
        var d = new FiscalDocument
        {
            Id = Guid.CreateVersion7(), ENcf = encf, Type = EcfType.Consumer32, ReferenceId = Guid.NewGuid(), Total = 1m, SignedUtc = app.Clock.GetUtcNow().UtcDateTime,
            SecurityCode = "X", SignedXml = [1], Status = SubmissionStatus.Pending, SubmitDeadlineUtc = deadlineUtc, NextAttemptUtc = nextAttemptUtc,
        };
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d.Id;
    });
}

/// <summary>The e-CF send queue (TK-051) with a provider that fails first and then answers. Own app: it writes queue rows directly.</summary>
public sealed class EcfSubmissionTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task A_failure_is_retried_later_and_then_ends_up_accepted()
    {
        await app.AdminAsync();
        var id = await app.PendingAsync("E320000099001", app.Clock.GetUtcNow().UtcDateTime.AddHours(72));
        var provider = new TestEcfProvider();
        provider.Responses.Enqueue(() => throw new HttpRequestException("proveedor caído"));
        provider.Responses.Enqueue(() => new SubmissionResult(SubmissionStatus.Accepted, "TRACK-1"));

        (await app.WithQueue(provider, (q, _) => q.ProcessAsync(CancellationToken.None))).ShouldBe(1);
        var failed = await app.WithQueue(provider, (_, db) => db.Documents.AsNoTracking().SingleAsync(d => d.Id == id));
        failed.Status.ShouldBe(SubmissionStatus.Pending);
        failed.Attempts.ShouldBe(1);
        failed.LastError.ShouldBe("proveedor caído");

        // Not before its retry time; after it, the document goes and is accepted.
        (await app.WithQueue(provider, (q, _) => q.ProcessAsync(CancellationToken.None))).ShouldBe(0);
        app.Clock.Advance(EcfSubmissionQueue.RetryDelay(1));
        (await app.WithQueue(provider, (q, _) => q.ProcessAsync(CancellationToken.None))).ShouldBe(1);
        var sent = await app.WithQueue(provider, (_, db) => db.Documents.AsNoTracking().SingleAsync(d => d.Id == id));
        sent.Status.ShouldBe(SubmissionStatus.Accepted);
        sent.TrackId.ShouldBe("TRACK-1");
        sent.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task The_queue_shows_pending_overdue_and_simulated()
    {
        var admin = app.Client(await app.AdminAsync());
        var now = app.Clock.GetUtcNow().UtcDateTime;
        await app.PendingAsync("E320000098001", now.AddHours(-1), nextAttemptUtc: now.AddDays(30));
        await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            db.Documents.Add(new FiscalDocument
            {
                Id = Guid.CreateVersion7(), ENcf = "E320000098002", Type = EcfType.Consumer32, ReferenceId = Guid.NewGuid(), Total = 1m, SignedUtc = now,
                SecurityCode = "X", SignedXml = [1], Status = SubmissionStatus.Simulated, SubmitDeadlineUtc = now.AddHours(72),
            });
            return await db.SaveChangesAsync();
        });

        var queue = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/queue");
        queue.GetProperty("overdue").GetInt32().ShouldBeGreaterThanOrEqualTo(1); // past the 72 hours: the owner must act
        queue.GetProperty("simulated").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        EcfSubmissionQueue.RetryDelay(10).ShouldBe(TimeSpan.FromMinutes(60));
    }
}

/// <summary>R5 #4: failures the real provider will produce. Own app, so no other test's documents are in the batch.</summary>
public sealed class EcfSubmissionFailureTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task A_provider_timeout_is_retried_and_a_rejection_is_not()
    {
        await app.AdminAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var slow = await app.PendingAsync("E320000096001", now.AddHours(1));       // closest deadline: sent first
        var rejected = await app.PendingAsync("E320000096002", now.AddHours(2));
        var provider = new TestEcfProvider();
        // An HTTP timeout surfaces as TaskCanceledException: it must be a retry, not a crash of the service.
        provider.Responses.Enqueue(() => throw new TaskCanceledException("timeout"));
        provider.Responses.Enqueue(() => new SubmissionResult(SubmissionStatus.Rejected, "TRACK-R", "RNC del comprador inválido"));

        (await app.WithQueue(provider, (q, _) => q.ProcessAsync(CancellationToken.None))).ShouldBe(2);
        var doc = await app.WithQueue(provider, (_, db) => db.Documents.AsNoTracking().SingleAsync(d => d.Id == slow));
        (doc.Status, doc.Attempts).ShouldBe((SubmissionStatus.Pending, 1));
        var rej = await app.WithQueue(provider, (_, db) => db.Documents.AsNoTracking().SingleAsync(d => d.Id == rejected));
        (rej.Status, rej.LastError).ShouldBe((SubmissionStatus.Rejected, "RNC del comprador inválido"));

        // A rejected document is not sent again.
        app.Clock.Advance(TimeSpan.FromHours(2));
        provider.Responses.Enqueue(() => new SubmissionResult(SubmissionStatus.Accepted, "TRACK-2"));
        (await app.WithQueue(provider, (q, _) => q.ProcessAsync(CancellationToken.None))).ShouldBe(1);
    }
}
