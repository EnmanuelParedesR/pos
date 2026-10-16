using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Settings;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>TK-094. Own app: the test tampers with the audit table.</summary>
public sealed class AuditTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public void The_nightly_check_runs_at_3_am_store_time()
    {
        // 06:00 UTC is 02:00 in Santo Domingo: one hour to go. 08:00 UTC is 04:00: 23 hours.
        NightlyAuditCheck.UntilNext(new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(3)).ShouldBe(TimeSpan.FromHours(1));
        NightlyAuditCheck.UntilNext(new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(3)).ShouldBe(TimeSpan.FromHours(23));
    }

    [Fact]
    public async Task The_audit_log_lists_by_period_user_and_action_and_exports_to_csv()
    {
        var session = await app.AdminAsync();
        var admin = app.Client(session);
        await admin.CategoryAsync("Categoría de la auditoría");
        (await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Carnicería auditada", DiscountCapPercent = 10m })).EnsureSuccessStatusCode();
        var today = Abastalia.SharedKernel.BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime);
        var range = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";

        var rows = await admin.GetFromJsonAsync<JsonElement>($"/api/audit?{range}&userId={session.UserId}&action=settings.business");
        var row = rows.EnumerateArray().ShouldHaveSingleItem();
        row.GetProperty("user").GetString().ShouldNotBeNullOrEmpty();
        row.GetProperty("resourceId").ValueKind.ShouldBe(JsonValueKind.Null); // store-wide record
        row.GetProperty("data").GetString()!.ShouldContain("auditada");
        (await admin.GetFromJsonAsync<JsonElement>($"/api/audit?{range}&userId={Guid.NewGuid()}")).GetArrayLength().ShouldBe(0);
        var yesterday = today.AddDays(-1);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/audit?from={yesterday:yyyy-MM-dd}&to={yesterday:yyyy-MM-dd}")).GetArrayLength().ShouldBe(0);
        (await admin.GetFromJsonAsync<JsonElement>("/api/audit/users")).EnumerateArray().ShouldContain(u => u.GetProperty("id").GetGuid() == session.UserId);

        using var csv = await admin.GetAsync($"/api/audit?{range}&format=csv");
        csv.Content.Headers.ContentDisposition!.FileName!.ShouldContain("auditoria");
        var text = await csv.Content.ReadAsStringAsync();
        text.ShouldContain("Registro,Fecha,UsuarioId,Usuario,Supervisor,Accion,Entidad,EntidadId,Detalle");
        text.ShouldContain("settings.business");

        var accountant = app.Client(await app.UserAsync(Roles.Accountant));
        (await accountant.GetAsync($"/api/audit?{range}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
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
