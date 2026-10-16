using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Toki.BuildingBlocks.Messaging;
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
        // TK-241: the cloud got only the public key; the private one is kept protected for this PC.
        var credential = await UnprotectAsync(row.NodeSecret);
        credential.ShouldStartWith("ecdsa:");
        row.NodeSecret.Contains(credential["ecdsa:".Length..], StringComparison.Ordinal).ShouldBeFalse();
        using (var key = System.Security.Cryptography.ECDsa.Create())
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(credential["ecdsa:".Length..]), out _);
            key.ExportSubjectPublicKeyInfo().ShouldBe(app.Cloud.NodePublicKey);
        }

        (await (await admin.PostAsJsonAsync("/api/sync/connect", new { Code = FakeCloud.ValidCode })).ErrorCode()).ShouldBe("Sync.AlreadyConnected");

        // TK-241: disconnecting tells the cloud first (the node is revoked there), signed with the node key.
        (await admin.PostAsync("/api/sync/disconnect", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        app.Cloud.SignedByNode(app.Cloud.To("/api/nodes/revoke").Single()).ShouldBeTrue();
        (await app.StatusAsync()).GetProperty("connected").GetBoolean().ShouldBeFalse();
        (await app.InScopeAsync(sp => sp.GetRequiredService<SyncDbContext>().Connections.CountAsync())).ShouldBe(0);

        // A cloud from before TK-241 answers a secret: that is the credential then. Without internet the store still disconnects.
        app.Cloud.IssueSecret = true;
        (await admin.PostAsJsonAsync("/api/sync/connect", new { Code = FakeCloud.ValidCode })).StatusCode.ShouldBe(HttpStatusCode.OK);
        row = await app.InScopeAsync(sp => sp.GetRequiredService<SyncDbContext>().Connections.AsNoTracking().SingleAsync());
        (await UnprotectAsync(row.NodeSecret)).ShouldBe(FakeCloud.NodeSecret);
        app.Cloud.Respond = _ => throw new HttpRequestException("sin internet");
        (await admin.PostAsync("/api/sync/disconnect", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        app.Cloud.To("/api/nodes/revoke").Last().Authorization.ShouldBe($"Node {app.Cloud.NodeId}.{FakeCloud.NodeSecret}");
        (await app.InScopeAsync(sp => sp.GetRequiredService<SyncDbContext>().Connections.CountAsync())).ShouldBe(0);
        (app.Cloud.Respond, app.Cloud.IssueSecret) = (null, false);
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    private Task<string> UnprotectAsync(string protectedValue) => app.InScopeAsync(sp =>
        Task.FromResult(sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("Toki.Sync.NodeSecret").Unprotect(protectedValue)));

    [Fact]
    public async Task Only_who_manages_settings_sees_or_changes_the_cloud_connection()
    {
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.GetAsync("/api/sync/status")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.PostAsJsonAsync("/api/sync/connect", new { Code = FakeCloud.ValidCode })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

/// <summary>TK-165: users and registers send their names to the cloud, and connecting sends the ones that already existed. Own app: it connects.</summary>
public sealed class CloudNamesTests(TokiApp app) : IClassFixture<TokiApp>
{
    private Task<List<JsonElement>> EventsAsync<T>() => app.InScopeAsync(async sp =>
        (await sp.GetRequiredService<InfraDbContext>().Outbox.Where(m => m.Type == typeof(T).FullName).OrderBy(m => m.Sequence).Select(m => m.Payload).ToListAsync())
        .Select(p => JsonDocument.Parse(p).RootElement.Clone()).ToList());

    [Fact]
    public async Task User_and_register_changes_go_to_the_outbox_without_pins_and_connecting_sends_a_snapshot()
    {
        var admin = app.Client(await app.AdminAsync());
        var cashier = await app.UserAsync(Roles.Cashier);
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/identity/users");
        var code = users.EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == cashier.UserId).GetProperty("code").GetString();
        (await admin.PutAsJsonAsync($"/api/identity/users/{cashier.UserId}", new { Name = "Yudelka", RoleId = Roles.Cashier, IsActive = false, Pin = "9876" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var register = (await (await admin.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja 1" })).Json()).GetProperty("id").GetGuid();

        var changed = await EventsAsync<Toki.Identity.Contracts.UserProfileChangedV1>();
        changed.Select(e => e.GetProperty("userId").GetGuid()).ShouldBe([(await app.AdminAsync()).UserId, cashier.UserId, cashier.UserId]);
        (changed[^1].GetProperty("name").GetString(), changed[^1].GetProperty("active").GetBoolean()).ShouldBe(("Yudelka", false));
        changed.ShouldAllBe(e => e.EnumerateObject().All(p => new[] { "userId", "name", "active", "eventId", "occurredUtc", "aggregateId" }.Contains(p.Name)));
        changed.ShouldAllBe(e => !e.GetRawText().Contains("9876") && !e.GetRawText().Contains(code!));
        var registers = await EventsAsync<Toki.CashRegister.Contracts.RegisterChangedV1>();
        (registers.Single().GetProperty("registerId").GetGuid(), registers.Single().GetProperty("name").GetString()).ShouldBe((register, "Caja 1"));
        (await admin.PostAsJsonAsync("/api/customers", new { Name = "Ana Pérez" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        var customer = (await EventsAsync<Toki.Customers.Contracts.CustomerBalanceChangedV1>()).ShouldHaveSingleItem(); // TK-174
        (customer.GetProperty("name").GetString(), customer.GetProperty("debt").GetDecimal()).ShouldBe(("Ana Pérez", 0m));

        await app.ConnectAsync();

        (await EventsAsync<Toki.Identity.Contracts.UserProfileChangedV1>()).Count.ShouldBe(5); // + admin and cashier, as they are now
        (await EventsAsync<Toki.CashRegister.Contracts.RegisterChangedV1>()).Count.ShouldBe(2);
        (await EventsAsync<Toki.Customers.Contracts.CustomerBalanceChangedV1>()).Count.ShouldBe(2);
        await app.UploadAsync();
        app.Cloud.To(CloudUploader.IngestPath).SelectMany(r => r.Body.GetProperty("events").EnumerateArray())
            .Count(e => e.GetProperty("type").GetString() == typeof(Toki.Identity.Contracts.UserProfileChangedV1).FullName).ShouldBe(5);
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
        batches.ShouldAllBe(b => b.Gzip && app.Cloud.SignedByNode(b)); // TK-241: signed with the node key, no shared secret
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

/// <summary>TK-190: the node heartbeat. Own app: it owns the connection and counts the day's sales.</summary>
public sealed class HeartbeatTests(TokiApp app) : IClassFixture<TokiApp>
{
    private Task<List<string>> HeartbeatsAsync() => app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().Outbox
        .Where(m => m.Type == typeof(Toki.Sync.Contracts.NodeHeartbeatV1).FullName).OrderBy(m => m.Sequence).Select(m => m.Payload).ToListAsync());

    private async Task SendAsync() => (await app.InScopeAsync(sp =>
        sp.GetRequiredService<ICommandHandler<SendHeartbeat.Command, Ok>>().Handle(new SendHeartbeat.Command(), CancellationToken.None))).IsSuccess.ShouldBeTrue();

    [Fact]
    public async Task The_heartbeat_counts_usage_and_carries_no_personal_data()
    {
        var admin = app.Client(await app.AdminAsync());
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco Rojo", 59m, cost: 35m, stock: 24);
        var cashier = await app.CashierWithShiftAsync(); // also sets the business name and RNC
        (await cashier.SellAsync(Store.Sale(cashier.ShiftId, 118m, 200m, Store.Line(soda, 2)))).EnsureSuccessStatusCode();

        await SendAsync();
        (await HeartbeatsAsync()).ShouldBeEmpty(); // not connected: nothing to report

        await app.ConnectAsync();
        await SendAsync();

        var heartbeat = JsonDocument.Parse((await HeartbeatsAsync()).Single()).RootElement;
        heartbeat.GetProperty("nodeId").GetGuid().ShouldBe(app.Cloud.NodeId);
        heartbeat.GetProperty("appVersion").GetString().ShouldNotBeNullOrEmpty();
        heartbeat.GetProperty("os").GetString().ShouldNotBeNullOrEmpty();
        (heartbeat.GetProperty("activeRegisters").GetInt32(), heartbeat.GetProperty("openShifts").GetInt32()).ShouldBe((1, 1));
        heartbeat.GetProperty("outboxBacklog").GetInt32().ShouldBeGreaterThan(0);
        heartbeat.GetProperty("lastErrorCode").ValueKind.ShouldBe(JsonValueKind.Null);
        var features = heartbeat.GetProperty("features");
        (features.GetProperty("onAccount").GetBoolean(), features.GetProperty("fiscalProvider").GetString()).ShouldBe((false, "Simulated"));
        features.GetProperty("scaleConfigured").ValueKind.ShouldBe(JsonValueKind.Null); // the register knows, not the server
        var setup = heartbeat.GetProperty("setup");
        (setup.GetProperty("businessNameSet").GetBoolean(), setup.GetProperty("rncSet").GetBoolean()).ShouldBe((true, true));
        setup.GetProperty("activeProducts").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        // TK-222: what the cloud alerts on.
        var fiscal = heartbeat.GetProperty("fiscal");
        fiscal.GetProperty("rejectedWithoutReplacement").GetInt32().ShouldBe(0);
        fiscal.GetProperty("oldestPendingUtc").ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        var today = heartbeat.GetProperty("days")[1];
        today.GetProperty("day").GetString().ShouldBe(Toki.SharedKernel.BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd"));
        (today.GetProperty("sales").GetInt32(), today.GetProperty("salesAmount").GetDecimal(), today.GetProperty("activeCashiers").GetInt32()).ShouldBe((1, 118m, 1));
        // TK-196: the license status, so the console sees each customer's trial.
        heartbeat.GetProperty("license").GetProperty("status").GetString().ShouldBe("Trial");

        // Only counts and flags: no names, codes, PINs, RNC, products or customers.
        var allowed = new[]
        {
            "nodeId", "appVersion", "os", "uptimeMinutes", "activeRegisters", "openShifts", "outboxBacklog", "lastErrorCode", "features", "setup", "days",
            "eventId", "occurredUtc", "aggregateId", "onAccount", "scaleConfigured", "printerConfigured", "fiscalProvider", "businessNameSet", "rncSet",
            "activeProducts", "day", "sales", "salesAmount", "activeCashiers", "voids", "returns", "license", "status", "endsUtc",
            "fiscal", "rejectedWithoutReplacement", "oldestPendingUtc", "certificateExpiresUtc",
        };
        Names(heartbeat).ShouldAllBe(n => allowed.Contains(n));
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/identity/users");
        var secrets = users.EnumerateArray().SelectMany(u => new[] { u.GetProperty("name").GetString()!, u.GetProperty("code").GetString()! })
            .Concat(["Carnicería de prueba", "131245678", "Refresco Rojo", TokiApp.AdminPin, "4321"]);
        var raw = heartbeat.GetRawText();
        secrets.ShouldAllBe(s => !raw.Contains(s, StringComparison.OrdinalIgnoreCase));

        // It goes up with the other events.
        await app.UploadAsync();
        app.Cloud.To(CloudUploader.IngestPath).SelectMany(r => r.Body.GetProperty("events").EnumerateArray())
            .Count(e => e.GetProperty("type").GetString() == typeof(Toki.Sync.Contracts.NodeHeartbeatV1).FullName).ShouldBe(1);

        // The register reports its scale and printer (only the kind, never the port); the next heartbeat says whether some register has one.
        (await (await admin.PutAsJsonAsync("/api/identity/devices/current/peripherals", new { ScaleProtocol = "Cas", PrinterWidth = 76 })).ErrorCode())
            .ShouldBe("Identity.InvalidPeripherals");
        (await admin.PutAsJsonAsync("/api/identity/devices/current/peripherals", new { ScaleProtocol = "Cas", PrinterWidth = (int?)null }))
            .StatusCode.ShouldBe(System.Net.HttpStatusCode.NoContent);
        await SendAsync();
        features = JsonDocument.Parse((await HeartbeatsAsync()).Last()).RootElement.GetProperty("features");
        (features.GetProperty("scaleConfigured").GetBoolean(), features.GetProperty("printerConfigured").GetBoolean()).ShouldBe((true, false));
    }

    [Fact]
    public void Upload_errors_travel_as_a_kind_never_as_text()
    {
        CloudUploader.ErrorCode(null).ShouldBeNull();
        CloudUploader.ErrorCode("La nube respondió 503 al subir los datos.").ShouldBe("http_503");
        CloudUploader.ErrorCode("No se pudo conectar con la nube: sin internet").ShouldBe("unreachable");
        CloudUploader.ErrorCode("La clave de la nube no se puede leer en esta PC").ShouldBe("other");
    }

    private static IEnumerable<string> Names(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => Names(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(Names),
        _ => [],
    };
}
