using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.Fiscal;
using Toki.Fiscal.Domain;
using Toki.IntegrationTests.Infra;
using Toki.Sync;

namespace Toki.IntegrationTests.Sync;

internal static class SyncApp
{
    public static async Task ConnectAsync(this TokiApp app)
    {
        var r = await app.Client(await app.AdminAsync()).PostAsJsonAsync("/api/sync/connect", new { Code = FakeCloud.ValidCode });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public static Task<UploadPass> UploadAsync(this TokiApp app) =>
        app.InScopeAsync(sp => sp.GetRequiredService<CloudUploader>().UploadAsync(CancellationToken.None));

    public static async Task<JsonElement> StatusAsync(this TokiApp app) =>
        await app.Client(await app.AdminAsync()).GetFromJsonAsync<JsonElement>("/api/sync/status");

    /// <summary>Writes <paramref name="count"/> events straight into the outbox, as a command would.</summary>
    public static Task<List<Guid>> AddEventsAsync(this TokiApp app, int count) => app.InScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<InfraDbContext>();
        var events = Enumerable.Range(0, count).Select(i => new OutboxMessage
        {
            Id = Guid.CreateVersion7(), Type = "Toki.Tests.SomethingHappenedV1", AggregateId = Guid.NewGuid(), Payload = $$"""{"n":{{i}}}""",
            OccurredUtc = app.Clock.GetUtcNow().UtcDateTime,
        }).ToList();
        db.Outbox.AddRange(events);
        await db.SaveChangesAsync();
        return events.Select(e => e.Id).ToList();
    });

    /// <summary>Outbox ids not yet confirmed by the cloud, in Sequence order.</summary>
    public static Task<List<Guid>> PendingAsync(this TokiApp app) => app.InScopeAsync(sp =>
        sp.GetRequiredService<InfraDbContext>().Outbox.Where(m => m.UploadedUtc == null).OrderBy(m => m.Sequence).Select(m => m.Id).ToListAsync());

    /// <summary>Event ids the cloud received, in the order they were sent.</summary>
    public static List<Guid> Uploaded(this FakeCloud cloud) =>
        [.. cloud.To(CloudUploader.IngestPath).SelectMany(r => r.Body.GetProperty("events").EnumerateArray()).Select(e => e.GetProperty("id").GetGuid())];
}

/// <summary>TK-130: connecting with the panel's code. Own app: it owns the store's single cloud connection.</summary>
public sealed class SyncConnectTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Connect_stores_the_identity_without_the_secret_in_clear_and_disconnect_forgets_it()
    {
        var admin = app.Client(await app.AdminAsync());
        (await admin.GetFromJsonAsync<JsonElement>("/api/sync/status")).GetProperty("connected").GetBoolean().ShouldBeFalse();

        // A code that is not 8 digits never reaches the cloud; one the cloud does not know is refused.
        (await (await admin.PostAsJsonAsync("/api/sync/connect", new { Code = "12ab" })).ErrorCode()).ShouldBe("Sync.InvalidCode");
        app.Cloud.To("/api/nodes/register").ShouldBeEmpty();
        var unknown = await admin.PostAsJsonAsync("/api/sync/connect", new { Code = "87654321" });
        (unknown.StatusCode, await unknown.ErrorCode()).ShouldBe((HttpStatusCode.BadRequest, "Sync.InvalidCode"));

        // No internet: a clear error, nothing stored.
        app.Cloud.Respond = _ => throw new HttpRequestException("sin internet");
        (await (await admin.PostAsJsonAsync("/api/sync/connect", new { Code = FakeCloud.ValidCode })).ErrorCode()).ShouldBe("Sync.CloudUnreachable");
        app.Cloud.Respond = null;

        var connected = await admin.PostAsJsonAsync("/api/sync/connect", new { Code = FakeCloud.ValidCode });
        connected.StatusCode.ShouldBe(HttpStatusCode.OK);
        var status = await connected.Json();
        status.GetProperty("connected").GetBoolean().ShouldBeTrue();
        status.GetProperty("tenantId").GetGuid().ShouldBe(app.Cloud.TenantId);
        status.GetProperty("storeId").GetGuid().ShouldBe(app.Cloud.StoreId);
        status.GetProperty("cloudUrl").GetString().ShouldBe(FakeCloud.Url);
        var register = app.Cloud.To("/api/nodes/register").Last();
        (register.Origin, register.Body.GetProperty("code").GetString()).ShouldBe((FakeCloud.Url, FakeCloud.ValidCode));
        register.Body.GetProperty("nodeName").GetString().ShouldNotBeNullOrEmpty();

        var row = await app.InScopeAsync(sp => sp.GetRequiredService<SyncDbContext>().Connections.AsNoTracking().SingleAsync());
        row.NodeId.ShouldBe(app.Cloud.NodeId);
        row.NodeSecret.Contains(FakeCloud.NodeSecret, StringComparison.Ordinal).ShouldBeFalse();
        (await app.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Toki.Sync.NodeSecret").Unprotect(row.NodeSecret)))).ShouldBe(FakeCloud.NodeSecret);

        (await (await admin.PostAsJsonAsync("/api/sync/connect", new { Code = FakeCloud.ValidCode })).ErrorCode()).ShouldBe("Sync.AlreadyConnected");

        (await admin.PostAsync("/api/sync/disconnect", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.StatusAsync()).GetProperty("connected").GetBoolean().ShouldBeFalse();
        (await app.InScopeAsync(sp => sp.GetRequiredService<SyncDbContext>().Connections.CountAsync())).ShouldBe(0);
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task Only_who_manages_settings_sees_or_changes_the_cloud_connection()
    {
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.GetAsync("/api/sync/status")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.PostAsJsonAsync("/api/sync/connect", new { Code = FakeCloud.ValidCode })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

/// <summary>TK-130: the uploader. Own app: it counts every pending event of its database.</summary>
public sealed class CloudUploadTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Pending_events_go_in_order_with_the_node_header_and_only_accepted_ones_are_marked()
    {
        await app.ConnectAsync();
        var refused = (await app.AddEventsAsync(250)).Where((_, i) => i % 50 == 7).ToHashSet(); // 250: two batches
        var pending = await app.PendingAsync();
        (await app.StatusAsync()).GetProperty("pendingEvents").GetInt32().ShouldBe(pending.Count);
        app.Cloud.Accept = id => !refused.Contains(id);

        var pass = await app.UploadAsync();

        (pass.Accepted, pass.Error).ShouldBe((pending.Count - refused.Count, null));
        app.Cloud.Uploaded().ShouldBe(pending); // everything, once, in Sequence order
        var batches = app.Cloud.To(CloudUploader.IngestPath).ToList();
        batches.Select(b => b.Body.GetProperty("events").GetArrayLength()).ShouldAllBe(n => n <= CloudUploader.BatchSize);
        batches.ShouldAllBe(b => b.Gzip && b.Authorization == $"Node {app.Cloud.NodeId}.{FakeCloud.NodeSecret}");
        var first = batches[0].Body.GetProperty("events")[0];
        first.GetProperty("type").GetString().ShouldNotBeNullOrEmpty();
        first.GetProperty("aggregateId").GetGuid().ShouldNotBe(Guid.Empty);
        first.GetProperty("occurredUtc").GetDateTime().Kind.ShouldBe(DateTimeKind.Utc);
        first.GetProperty("payload").ValueKind.ShouldBe(JsonValueKind.Object); // the event's JSON, not a string

        (await app.PendingAsync()).ShouldBe(pending.Where(refused.Contains).ToList());
        var status = await app.StatusAsync();
        status.GetProperty("pendingEvents").GetInt32().ShouldBe(refused.Count);
        status.GetProperty("lastUploadUtc").ValueKind.ShouldNotBe(JsonValueKind.Null);

        // The refused ones go again next pass; the cloud answering ids it already had is fine.
        app.Cloud.Accept = _ => true;
        (await app.UploadAsync()).Accepted.ShouldBe(refused.Count);
        (await app.PendingAsync()).ShouldBeEmpty();
        (await app.UploadAsync()).Accepted.ShouldBe(0);
    }
}

/// <summary>TK-130: no internet or a cloud failure never loses an event. Own app: it moves the clock.</summary>
public sealed class CloudUploadFailureTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task A_failure_keeps_events_pending_and_the_next_pass_sends_them_again()
    {
        await app.ConnectAsync();
        await app.UploadAsync(); // what the setup wrote
        var events = await app.AddEventsAsync(3);

        // The cloud kept the batch but the answer never arrived: everything stays pending.
        app.Cloud.Respond = path => path == CloudUploader.IngestPath ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;
        var failed = await app.UploadAsync();
        failed.Error.ShouldBe("La nube respondió 503 al subir los datos.");
        (await app.PendingAsync()).ShouldBe(events, ignoreOrder: true);
        (await app.StatusAsync()).GetProperty("lastError").GetString().ShouldBe(failed.Error);

        app.Cloud.Respond = _ => throw new HttpRequestException("sin internet");
        (await app.UploadAsync()).Error.ShouldBe("No se pudo conectar con la nube: sin internet");

        // An hour later, still offline: the owner sees it in the health checks.
        app.Clock.Advance(TimeSpan.FromMinutes(61));
        var health = await app.InScopeAsync(sp => sp.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Name == "sync"));
        health.Entries["sync"].Status.ShouldBe(HealthStatus.Degraded);
        health.Entries["sync"].Description!.ShouldContain("sin subir a la nube");

        // Back online: the same events go again (the cloud dedupes them) and are marked.
        app.Cloud.Respond = null;
        var ok = await app.UploadAsync();
        (ok.Accepted, ok.Error).ShouldBe((events.Count, null));
        (await app.PendingAsync()).ShouldBeEmpty();
        (await app.StatusAsync()).GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Null);
        (await app.InScopeAsync(sp => sp.GetRequiredService<HealthCheckService>().CheckHealthAsync(c => c.Name == "sync"))).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public void The_retry_delay_grows_from_one_second_to_fifteen_minutes_with_jitter()
    {
        CloudUploader.RetryDelay(1).ShouldBeInRange(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1));
        CloudUploader.RetryDelay(4).ShouldBeInRange(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));
        CloudUploader.RetryDelay(100).ShouldBeInRange(TimeSpan.FromMinutes(7.5), TimeSpan.FromMinutes(15));
    }
}

/// <summary>TK-130: after disconnecting nothing goes up, and nothing is lost. Own app: it owns the connection.</summary>
public sealed class CloudDisconnectTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task Disconnecting_stops_the_uploads_and_keeps_the_events()
    {
        await app.ConnectAsync();
        await app.UploadAsync();
        var sent = app.Cloud.To(CloudUploader.IngestPath).Count();

        (await app.Client(await app.AdminAsync()).PostAsync("/api/sync/disconnect", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var events = await app.AddEventsAsync(2);
        (await app.UploadAsync()).ShouldBe(new UploadPass(0, null));

        app.Cloud.To(CloudUploader.IngestPath).Count().ShouldBe(sent);
        (await app.PendingAsync()).ShouldBe(events);
        var status = await app.StatusAsync();
        (status.GetProperty("connected").GetBoolean(), status.GetProperty("pendingEvents").GetInt32()).ShouldBe((false, 2));
    }
}

/// <summary>TK-131: the e-CF relay provider. Own app: it owns the connection.</summary>
public sealed class RelayEcfProviderTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task The_relay_sends_the_signed_document_as_the_node_and_maps_the_answer()
    {
        await app.AdminAsync();
        Task<SubmissionResult> Submit() => app.InScopeAsync(sp =>
            ActivatorUtilities.CreateInstance<RelayEcfProvider>(sp).SubmitAsync("E320000000123", [1, 2, 3], CancellationToken.None));

        // Not connected: an error the queue retries later.
        (await Should.ThrowAsync<InvalidOperationException>(Submit)).Message.ShouldBe("La tienda no está conectada a la nube.");

        await app.ConnectAsync();
        app.Cloud.RelayAnswer = new { status = "Accepted", trackId = "TRACK-9", message = (string?)null };
        (await Submit()).ShouldBe(new SubmissionResult(SubmissionStatus.Accepted, "TRACK-9"));
        var sent = app.Cloud.To(RelayEcfProvider.Path).Single();
        sent.Authorization.ShouldBe($"Node {app.Cloud.NodeId}.{FakeCloud.NodeSecret}");
        sent.Body.GetProperty("encf").GetString().ShouldBe("E320000000123");
        sent.Body.GetProperty("signedXmlGzip").GetBytesFromBase64().ShouldBe(new byte[] { 1, 2, 3 });

        app.Cloud.RelayAnswer = new { status = "Rejected", trackId = "TRACK-10", message = "RNC del comprador inválido" };
        (await Submit()).ShouldBe(new SubmissionResult(SubmissionStatus.Rejected, "TRACK-10", "RNC del comprador inválido"));

        // A cloud failure is thrown, so the queue retries it.
        app.Cloud.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        await Should.ThrowAsync<HttpRequestException>(Submit);
        app.Cloud.Respond = null;
    }
}
