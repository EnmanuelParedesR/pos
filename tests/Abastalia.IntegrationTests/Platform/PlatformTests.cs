using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.Host.Local;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Platform;

/// <summary>TK-093: health checks and the server's TLS certificate. Own app: the test breaks the health on purpose.</summary>
public sealed class PlatformTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private Task<string> HealthAsync() => app.HealthAsync();

    [Fact]
    public async Task Health_warns_if_the_clock_goes_back_or_an_eCF_misses_its_deadline()
    {
        var admin = app.Client(await app.AdminAsync());
        await admin.CategoryAsync();
        (await HealthAsync()).ShouldNotBe("Unhealthy"); // Healthy, or Degraded while no backup exists

        // The newest audit record is an hour ahead of the server clock: someone moved Windows time back.
        var last = await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.MaxAsync(r => r.Sequence));
        var original = await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.Where(r => r.Sequence == last).Select(r => r.DateUtc).SingleAsync());
        Task SetDate(DateTime d) => app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.Where(r => r.Sequence == last)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DateUtc, d)));
        await SetDate(original.AddHours(1));
        (await HealthAsync()).ShouldBe("Unhealthy");
        // R12: /health only says the server is up (the registers poll it); the checks are in Configuración → Salud.
        (await app.Anonymous().GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await SetDate(original);
        (await HealthAsync()).ShouldNotBe("Unhealthy");

        // An e-CF still pending after its 72 hours.
        var now = app.Clock.GetUtcNow().UtcDateTime;
        await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            db.Documents.Add(new FiscalDocument
            {
                Id = Guid.CreateVersion7(), ENcf = "E320000097001", Type = EcfType.Consumer32, ReferenceId = Guid.NewGuid(), Total = 1m, SignedUtc = now.AddHours(-73),
                SecurityCode = "X", SignedXml = [1], Status = SubmissionStatus.Pending, SubmitDeadlineUtc = now.AddHours(-1),
            });
            return await db.SaveChangesAsync();
        });
        (await HealthAsync()).ShouldBe("Unhealthy");
    }
}
