using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.CashRegister;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Domain;
using Abastalia.IntegrationTests.Fiscal;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>TK-238: the DGII environment, the backup schedule and the store's health from the back office; the blind count limit kept in the database.</summary>
public sealed class StoreOperationsTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static IEnumerable<string> Blockers(JsonElement mode, string env) =>
        mode.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("environment").GetString() == env)
            .GetProperty("blockers").EnumerateArray().Select(b => b.GetString()!);

    [Fact]
    public async Task The_DGII_environment_changes_on_screen_with_guard_rails_audited_and_without_a_restart()
    {
        var admin = app.Client(await app.AdminAsync());
        var mode = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/mode");
        (mode.GetProperty("environment").GetString(), mode.GetProperty("saved").GetBoolean()).ShouldBe(("Simulated", false));
        Blockers(mode, "TesteCF").ShouldContain(b => b.Contains("certificado"));
        Blockers(mode, "eCF").ShouldContain(b => b.Contains("Verificación del RNC"));

        // No confirmation, or something missing: nothing changes.
        (await admin.PutAsJsonAsync("/api/fiscal/mode", new { Environment = "TesteCF", Confirm = false })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await (await admin.PutAsJsonAsync("/api/fiscal/mode", new { Environment = "TesteCF", Confirm = true })).ErrorCode()).ShouldBe("Fiscal.ModeBlocked");
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.GetAsync("/api/fiscal/mode")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Certificate, RNC and address: TesteCF, at once.
        await app.WithAddressAsync();
        using var cert = TestCertificates.Create(DateTimeOffset.UtcNow.AddYears(1));
        (await admin.PostAsJsonAsync("/api/fiscal/certificate", new { File = Convert.ToBase64String(cert.Export(X509ContentType.Pkcs12, "c")), Password = "c" }))
            .EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync("/api/fiscal/mode", new { Environment = "TesteCF", Confirm = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        mode = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/mode");
        (mode.GetProperty("environment").GetString(), mode.GetProperty("saved").GetBoolean(), mode.GetProperty("autoSubmit").GetBoolean()).ShouldBe(("TesteCF", true, true));
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/queue")).GetProperty("environment").GetString().ShouldBe("TesteCF");
        (await app.InScopeAsync(sp => sp.GetRequiredService<Abastalia.BuildingBlocks.Persistence.InfraDbContext>().AuditRecords
            .AnyAsync(r => r.Action == "fiscal.change-mode"))).ShouldBeTrue();

        // Production needs its own ranges and the DGII's verification.
        Blockers(mode, "eCF").Count().ShouldBe(2);
        (await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = "Consumer32", From = 1, To = 100, Environment = "eCF" })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/fiscal/certification/steps/{FiscalModeChecks.VerifiedStep}", new { Done = true })).EnsureSuccessStatusCode();
        Blockers(await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/mode"), "eCF").ShouldBeEmpty();

        // R10 #3: production with e-CF issued does not go back with one click; only with support's flag.
        (await admin.PutAsJsonAsync("/api/fiscal/mode", new { Environment = "eCF", Confirm = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var issued = await app.PendingAsync("E320000000001", app.Clock.GetUtcNow().UtcDateTime.AddHours(72), environment: FiscalEnvironment.eCF);
        await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.Where(d => d.Id == issued)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.Status, SubmissionStatus.Accepted).SetProperty(x => x.ArchivedUtc, DateTime.UtcNow)));
        var refused = await admin.PutAsJsonAsync("/api/fiscal/mode", new { Environment = "TesteCF", Confirm = true });
        (refused.StatusCode, await refused.ErrorCode()).ShouldBe((HttpStatusCode.Conflict, "Fiscal.ModeBlocked"));
        Blockers(await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/mode"), "Simulated").ShouldContain(b => b.Contains("soporte"));
        using var support = app.WithWebHostBuilder(b => b.UseSetting("Fiscal:AllowLeavingProduction", "true"));
        var supportAdmin = support.CreateClient();
        foreach (var h in admin.DefaultRequestHeaders) supportAdmin.DefaultRequestHeaders.Add(h.Key, h.Value);

        // Back to Simulated: the saved choice is read again after a restart.
        (await supportAdmin.PutAsJsonAsync("/api/fiscal/mode", new { Environment = "Simulated", Confirm = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var restarted = app.WithWebHostBuilder(_ => { });
        restarted.Services.GetRequiredService<FiscalMode>().Environment.ShouldBe(FiscalEnvironment.Simulated);
    }

    [Fact]
    public async Task The_backup_schedule_is_saved_on_screen_and_a_missing_passphrase_is_reported()
    {
        var admin = app.Client(await app.AdminAsync());
        var settings = await admin.GetFromJsonAsync<JsonElement>("/api/backups/settings");
        settings.GetProperty("saved").GetBoolean().ShouldBeFalse();
        settings.GetProperty("automatic").GetBoolean().ShouldBeFalse(); // the test host's appsettings
        settings.GetProperty("passphraseProblem").GetString()!.ShouldContain("frase");
        (await admin.GetFromJsonAsync<JsonElement>("/api/backups/warning")).GetProperty("passphraseProblem").GetString().ShouldNotBeNull();

        (await admin.PutAsJsonAsync("/api/backups/settings", new { CopyTo = "relativa", Time = "25:00", Keep = 0, Automatic = false }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // R10 #10: not into the backup folder itself.
        (await (await admin.PutAsJsonAsync("/api/backups/settings", new { CopyTo = Path.Combine(settings.GetProperty("folder").GetString()!, "copia"), Time = "21:15", Keep = 5, Automatic = false }))
            .ErrorCode()).ShouldBe("Backup.CopyInsideData");
        var copy = Path.Combine(Path.GetTempPath(), $"abastalia_copy_{Guid.NewGuid():N}");
        (await admin.PutAsJsonAsync("/api/backups/settings", new { CopyTo = copy, Time = "21:15", Keep = 5, Automatic = false }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        settings = await admin.GetFromJsonAsync<JsonElement>("/api/backups/settings");
        (settings.GetProperty("copyTo").GetString(), settings.GetProperty("time").GetString(), settings.GetProperty("keep").GetInt32(), settings.GetProperty("saved").GetBoolean())
            .ShouldBe((copy, "21:15", 5, true));

        if (!TestSqlServer.BacksUpOnThisMachine) return;
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase = "la chuleta de los viernes 2026" })).EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/backups", null)).StatusCode.ShouldBe(HttpStatusCode.Created);
        settings = await admin.GetFromJsonAsync<JsonElement>("/api/backups/settings");
        settings.GetProperty("passphraseProblem").ValueKind.ShouldBe(JsonValueKind.Null);
        settings.GetProperty("last").GetProperty("fileName").GetString()!.ShouldStartWith("abastalia-");
        settings.GetProperty("copyProblem").ValueKind.ShouldBe(JsonValueKind.Null);
        Directory.GetFiles(copy).Length.ShouldBe(1); // the folder saved on screen, not appsettings'
    }

    [Fact]
    public async Task The_health_screen_lists_every_check_in_Spanish_with_what_to_do()
    {
        var admin = app.Client(await app.AdminAsync());
        var checks = (await admin.GetFromJsonAsync<JsonElement>("/api/settings/health")).EnumerateArray().ToList();
        checks.Select(c => c.GetProperty("name").GetString()).ShouldBe(["backup", "store-ca", "pin-failures", "shared-pin", "outbox", "clock", "ecf-queue", "ecf-operations", "audit", "sync", "disk-encryption"],
            ignoreOrder: true);
        var backup = checks.Single(c => c.GetProperty("name").GetString() == "backup");
        backup.GetProperty("title").GetString().ShouldBe("Respaldos");
        if (backup.GetProperty("status").GetString() != "Healthy") backup.GetProperty("action").GetString()!.ShouldContain("Configuración → Respaldos");
        (await app.Client(await app.UserAsync(Roles.Cashier)).GetAsync("/api/settings/health")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_blind_count_limit_is_kept_in_the_database()
    {
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        var register = (await (await app.Client(await app.AdminAsync()).PostAsJsonAsync("/api/cash/registers", new { Name = "Caja " + Guid.NewGuid().ToString()[..4] }))
            .Json()).GetProperty("id").GetGuid();
        var shift = (await (await cashier.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 2000m })).Json()).GetProperty("shiftId").GetGuid();
        foreach (var wrong in new[] { 1, 3 })
            (await (await cashier.PostAsJsonAsync($"/api/cash/shifts/{shift}/close", new { CashCount = new[] { new { Denomination = 1000m, Quantity = wrong } } })).ErrorCode())
                .ShouldBe("Approval.Required");
        (await app.InScopeAsync(sp => sp.GetRequiredService<CashRegisterDbContext>().Shifts.Where(s => s.Id == shift).Select(s => s.BlindMisses).SingleAsync()))
            .ShouldBe(2);
    }
}
