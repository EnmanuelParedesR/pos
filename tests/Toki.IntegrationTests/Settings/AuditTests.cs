using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.Settings;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Settings;

/// <summary>TK-094. Own app: the test tampers with the audit table.</summary>
public sealed class AuditTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public void The_nightly_check_runs_at_3_am_store_time()
    {
        // 06:00 UTC is 02:00 in Santo Domingo: one hour to go. 08:00 UTC is 04:00: 23 hours.
        NightlyAuditCheck.UntilNext(new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(3)).ShouldBe(TimeSpan.FromHours(1));
        NightlyAuditCheck.UntilNext(new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(3)).ShouldBe(TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task Editing_or_deleting_audit_records_breaks_the_chain()
    {
        var admin = app.Client(await app.AdminAsync());
        for (var i = 0; i < 3; i++) await admin.CategoryAsync($"Categoría {i}");
        async Task<JsonElement> Verify() => await admin.GetFromJsonAsync<JsonElement>("/api/audit/verify");
        (await Verify()).GetProperty("isIntact").GetBoolean().ShouldBeTrue();

        // Deleting the newest record (covering one's tracks) is caught: the chain no longer reaches the head.
        var last = await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.MaxAsync(r => r.Sequence));
        await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.Where(r => r.Sequence == last).ExecuteDeleteAsync());
        (await Verify()).GetProperty("firstBrokenRecord").GetInt64().ShouldBe(last);

        // Editing an older record is caught at that record.
        await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.Where(r => r.Sequence == 2)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Data, "{}")));
        var result = await Verify();
        result.GetProperty("isIntact").GetBoolean().ShouldBeFalse();
        result.GetProperty("firstBrokenRecord").GetInt64().ShouldBe(2);

        // The check that runs after every start (and at 03:00) turns /health red.
        var check = app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<NightlyAuditCheck>().Single();
        await check.VerifyAsync(CancellationToken.None);
        (await app.Anonymous().GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.GetAsync("/api/audit/verify")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
