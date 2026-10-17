using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.IntegrationTests.Fiscal;
using Abastalia.IntegrationTests.Infra;
using Abastalia.Settings.Contracts;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>TK-254: Configuración → Actualizaciones, the registers' reports and the audit of every action. TK-208: the 31 waits for the DGII.</summary>
public sealed class UpdatesTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private Task<bool> AuditedAsync(string action) =>
        app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.AnyAsync(r => r.Action == action));

    [Fact]
    public async Task The_owner_sets_the_channel_asks_to_update_now_and_closes_a_register_to_update_all_audited()
    {
        var admin = app.Client(await app.AdminAsync());
        var info = await admin.GetFromJsonAsync<JsonElement>("/api/settings/updates");
        (info.GetProperty("configured").GetBoolean(), info.GetProperty("channel").GetString(), info.GetProperty("outsideHours").GetBoolean())
            .ShouldBe((false, "stable", true));

        (await admin.PutAsJsonAsync("/api/settings/updates", new { Channel = "beta", OutsideHours = true })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync("/api/settings/updates", new { Channel = "pilot", OutsideHours = false })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AuditedAsync("settings.updates")).ShouldBeTrue();
        var policy = await app.InScopeAsync(sp => sp.GetRequiredService<IUpdatePolicy>().GetAsync(CancellationToken.None));
        (policy.Channel, policy.OutsideHours, policy.RequestedUtc).ShouldBe(("pilot", false, null));

        // Nothing to install yet; then the server's check finds 9.9.0 and the owner asks for it.
        (await (await admin.PostAsync("/api/settings/updates/apply-now", null)).ErrorCode()).ShouldBe("Settings.NoUpdate");
        await app.InScopeAsync(async sp => { await sp.GetRequiredService<IUpdatePolicy>().SetAvailableAsync("9.9.0", CancellationToken.None); return 0; });
        (await admin.PostAsync("/api/settings/updates/apply-now", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AuditedAsync("settings.updates.apply-now")).ShouldBeTrue();
        (await admin.GetFromJsonAsync<JsonElement>("/api/settings/updates")).GetProperty("requestedUtc").ValueKind.ShouldNotBe(JsonValueKind.Null);

        // A register reports 1.0.0: it gets 9.9.0 once closed; the owner asks it to close; it is told so on its next report.
        var report = await (await admin.PutAsJsonAsync("/api/settings/updates/register", new { Version = "1.0.0" })).Content.ReadFromJsonAsync<JsonElement>();
        (report.GetProperty("channel").GetString(), report.GetProperty("pendingVersion").GetString(), report.GetProperty("restartRequested").GetBoolean())
            .ShouldBe(("pilot", "9.9.0", false));
        var device = (await admin.GetFromJsonAsync<JsonElement>("/api/settings/updates")).GetProperty("registers")[0].GetProperty("deviceId").GetGuid();
        (await admin.PostAsync($"/api/settings/updates/registers/{device}/restart", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await AuditedAsync("settings.updates.register-restart")).ShouldBeTrue();
        report = await (await admin.PutAsJsonAsync("/api/settings/updates/register", new { Version = "1.0.0" })).Content.ReadFromJsonAsync<JsonElement>();
        report.GetProperty("restartRequested").GetBoolean().ShouldBeTrue();

        // Updated: nothing pending, the request is gone.
        report = await (await admin.PutAsJsonAsync("/api/settings/updates/register", new { Version = "9.9.0" })).Content.ReadFromJsonAsync<JsonElement>();
        (report.GetProperty("pendingVersion").ValueKind, report.GetProperty("restartRequested").GetBoolean()).ShouldBe((JsonValueKind.Null, false));

        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.GetAsync("/api/settings/updates")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.PostAsync("/api/settings/updates/apply-now", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_31_is_sent_at_once_and_its_answer_returned_and_without_sending_the_register_does_not_wait()
    {
        var admin = app.Client(await app.AdminAsync());
        var id = await app.PendingAsync("E310000077001", app.Clock.GetUtcNow().UtcDateTime.AddHours(72));
        await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.Where(d => d.Id == id)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.Type, EcfType.FiscalCredit31)));

        // This store does not send (Simulated): the answer comes at once, as pending.
        var r = await (await admin.PostAsync("/api/fiscal/documents/E310000077001/await-result", null)).Content.ReadFromJsonAsync<JsonElement>();
        r.GetProperty("status").GetString().ShouldBe("Pending");
        (await admin.PostAsync("/api/fiscal/documents/E319999999999/await-result", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Sent right away: accepted in the same call.
        var dgii = new TestEcfProvider();
        dgii.Responses.Enqueue(() => new DgiiAnswer(DgiiState.Accepted, "T-1"));
        await app.WithQueue(dgii, async (q, _) => { await q.SendNowAsync(id, TimeSpan.Zero, CancellationToken.None); return 0; });
        (await app.DocumentAsync(id)).Status.ShouldBe(SubmissionStatus.Accepted);
        r = await (await admin.PostAsync("/api/fiscal/documents/E310000077001/await-result", null)).Content.ReadFromJsonAsync<JsonElement>();
        r.GetProperty("status").GetString().ShouldBe("Accepted");

        // A document already tried is left to the queue and its backoff.
        await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.Where(d => d.Id == id)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.Status, SubmissionStatus.Pending).SetProperty(x => x.Attempts, 1)));
        await app.WithQueue(dgii, async (q, _) => { await q.SendNowAsync(id, TimeSpan.Zero, CancellationToken.None); return 0; });
        dgii.Calls.ShouldBe(["send E310000077001"]);
    }

    [Fact]
    public async Task TK292_the_server_reaches_the_feed()
    {
        // On the VM the check never got to the feed: reading the owner's choices disposed its scope synchronously, and the scope holds the
        // UnitOfWork (IAsyncDisposable only), so it threw every minute. The feed here fails, so nothing is written to update.json.
        var feed = new FailingFeed();
        var check = new Abastalia.Host.Local.ServerUpdateCheck(feed, app.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new Abastalia.Host.Local.UpdateSettings { FeedUrl = "https://feed.test" }), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Abastalia.Host.Local.ServerUpdateCheck>.Instance);
        await check.StartAsync(CancellationToken.None);
        try { (await Task.WhenAny(feed.Asked.Task, Task.Delay(TimeSpan.FromSeconds(20)))).ShouldBe(feed.Asked.Task); }
        finally { await check.StopAsync(CancellationToken.None); }
        (await feed.Asked.Task).ShouldStartWith("https://feed.test/releases."); // the channel may be the other test's
    }

    private sealed class FailingFeed : HttpMessageHandler, IHttpClientFactory
    {
        public TaskCompletionSource<string> Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.TrySetResult(request.RequestUri!.ToString());
            throw new HttpRequestException("no feed in tests");
        }
    }
}
