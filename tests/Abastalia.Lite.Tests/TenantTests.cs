using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Persistence;

namespace Abastalia.Lite.Tests;

/// <summary>TK-320 (ADR-0013): two businesses provisioned in one Lite database. Each request runs as its device's business: it sees and changes
/// only its own rows, through the real host. On SQL Server Row-Level Security holds with the EF filters off too.</summary>
public sealed class TenantTests(LiteApp app) : IClassFixture<LiteApp>
{
    [Fact]
    public async Task Each_device_reads_and_writes_only_its_own_business()
    {
        var a = await app.BusinessAsync("Dueña de A");
        var b = await app.BusinessAsync("Dueño de B"); // the same owner code "dueno": unique per business

        // Every seeded row is there for each one: roles (signing in), the business, the audit head (the setup wrote to it).
        foreach (var o in new[] { a, b })
        {
            (await app.Client(o).GetFromJsonAsync<JsonElement>("/api/identity/roles")).GetProperty("roles").GetArrayLength().ShouldBeGreaterThan(1);
            (await app.Client(o).GetFromJsonAsync<JsonElement>("/api/settings/business")).GetProperty("name").GetString().ShouldBe("Mi tienda");
        }

        (await app.Client(a).PutAsJsonAsync("/api/settings/business", new { Name = "Colmado A", DiscountCapPercent = 10 })).EnsureSuccessStatusCode();
        (await app.Client(a).PostAsJsonAsync("/api/identity/users", new { Name = "Cajera de A", Code = "cajera", Pin = "4321", RoleId = new Guid("0199e000-0000-7000-8000-000000000004") }))
            .EnsureSuccessStatusCode();

        (await app.Client(b).GetFromJsonAsync<JsonElement>("/api/settings/business")).GetProperty("name").GetString().ShouldBe("Mi tienda");
        Names(await app.Client(b).GetFromJsonAsync<JsonElement>("/api/identity/users")).ShouldBe(["Dueño de B"]);
        Names(await app.Client(a).GetFromJsonAsync<JsonElement>("/api/identity/users")).ShouldBe(["Cajera de A", "Dueña de A"], ignoreOrder: true);

        // A's session on B's device (and the other way) is no session at all: it is looked up inside the device's business.
        (await app.Client(b, a.Token).GetAsync("/api/identity/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.Client(a, b.Token).GetAsync("/api/identity/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // With the filters off, SQL Server still shows each business only its rows (RLS).
        if (TestSqlServer.IsEnabled)
            foreach (var o in new[] { a, b })
            {
                await using var scope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateTenantScope(o.TenantId);
                var owners = await scope.ServiceProvider.GetRequiredService<InfraDbContext>().AuditRecords.IgnoreQueryFilters()
                    .Select(r => EF.Property<Guid>(r, ModuleDbContext.TenantColumn)).Distinct().ToListAsync();
                owners.ShouldBe([o.TenantId]);
            }
    }

    [Fact]
    public async Task Without_a_known_device_no_api_route_answers()
    {
        // The store's anonymous first run would create an owner in no business: Lite provisions instead.
        (await app.Device(null).PostAsJsonAsync("/api/identity/setup", new { AdministratorName = "X", AdministratorCode = "x", Pin = "1234", DeviceName = "PC" }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.Device(new TestDevice()).PostAsJsonAsync("/api/identity/sessions", new { Code = "dueno", Pin = LiteApp.Pin }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.CreateClient().GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Provisioning_needs_the_key_and_runs_once_per_business()
    {
        var body = new { TenantId = Guid.CreateVersion7(), OwnerName = "Dueña", OwnerCode = "dueno", Pin = LiteApp.Pin };
        (await app.Provision(body, key: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.Provision(body, key: "wrong-key-wrong-key-wrong-key-wrong")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await app.Provision(body)).StatusCode.ShouldBe(HttpStatusCode.Created);
        // Again: the rows are not seeded twice, and Identity refuses a second owner.
        (await app.Provision(body)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await app.Provision(body with { TenantId = Guid.Empty })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static List<string> Names(JsonElement users) => [.. users.EnumerateArray().Select(u => u.GetProperty("name").GetString()!)];
}
