using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Toki.Cloud.Tests;

public sealed class AccountsAndNodesTests(CloudApp app) : IClassFixture<CloudApp>
{
    [Fact]
    public async Task Registering_creates_a_tenant_for_the_owner()
    {
        var owner = await app.OwnerAsync();

        owner.TenantId.ShouldNotBe(Guid.Empty);
        var tenant = await app.InDbAsync(db => db.Tenants.SingleAsync(t => t.Id == owner.TenantId));
        tenant.Name.ShouldBe(owner.Email);
    }

    [Fact]
    public async Task Panel_endpoints_need_a_signed_in_owner()
    {
        (await app.CreateClient().GetAsync("/api/stores")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.CreateClient().GetAsync("/api/panel/summary")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_store_gets_an_8_digit_connection_code_valid_for_30_minutes()
    {
        var owner = await app.OwnerAsync();
        var created = await owner.Client.PostAsJsonAsync("/api/stores", new { name = "Carnicería Don Pepe" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var storeId = (await created.Json()).GetProperty("id").GetGuid();

        var code = await (await owner.Client.PostAsync($"/api/stores/{storeId}/connection-codes", null)).Json();
        code.GetProperty("code").GetString()!.ShouldMatch("^[0-9]{8}$");
        code.GetProperty("expiresUtc").GetDateTime().ToUniversalTime().ShouldBe(app.Clock.GetUtcNow().UtcDateTime.AddMinutes(30), TimeSpan.FromSeconds(1));

        var stores = await owner.Client.GetFromJsonAsync<JsonElement>("/api/stores");
        stores.EnumerateArray().ShouldHaveSingleItem().GetProperty("name").GetString().ShouldBe("Carnicería Don Pepe");
    }

    [Fact]
    public async Task An_owner_cannot_see_or_connect_another_tenants_store()
    {
        var a = await app.OwnerAsync();
        var b = await app.OwnerAsync();
        var storeA = (await (await a.Client.PostAsJsonAsync("/api/stores", new { name = "Tienda A" })).Json()).GetProperty("id").GetGuid();

        var r = await b.Client.PostAsync($"/api/stores/{storeA}/connection-codes", null);
        r.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await r.ErrorCode()).ShouldBe("Stores.NotFound");
        (await b.Client.GetFromJsonAsync<JsonElement>("/api/stores")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_node_registers_with_the_code_once_and_only_its_public_key_is_kept()
    {
        var owner = await app.OwnerAsync();
        var store = (await (await owner.Client.PostAsJsonAsync("/api/stores", new { name = "Tienda" })).Json()).GetProperty("id").GetGuid();
        var code = (await (await owner.Client.PostAsync($"/api/stores/{store}/connection-codes", null)).Json()).GetProperty("code").GetString();
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();

        var first = await app.CreateClient().PostAsJsonAsync("/api/nodes/register", new { code, nodeName = "PC principal", publicKey = Convert.ToBase64String(publicKey) });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var node = await first.Json();
        node.GetProperty("tenantId").GetGuid().ShouldBe(owner.TenantId);
        node.GetProperty("storeId").GetGuid().ShouldBe(store);

        var again = await app.CreateClient().PostAsJsonAsync("/api/nodes/register", new { code, nodeName = "Otra PC", publicKey = Convert.ToBase64String(publicKey) });
        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await again.ErrorCode()).ShouldBe("Nodes.InvalidCode");

        var saved = await app.InDbAsync(db => db.Nodes.SingleAsync(n => n.Id == node.GetProperty("nodeId").GetGuid()));
        saved.PublicKey.ShouldBe(publicKey);
    }

    [Fact]
    public async Task An_expired_or_unknown_code_is_rejected()
    {
        var owner = await app.OwnerAsync();
        var store = (await (await owner.Client.PostAsJsonAsync("/api/stores", new { name = "Tienda" })).Json()).GetProperty("id").GetGuid();
        var code = (await (await owner.Client.PostAsync($"/api/stores/{store}/connection-codes", null)).Json()).GetProperty("code").GetString();
        app.Clock.Advance(TimeSpan.FromMinutes(31));

        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var expired = await app.CreateClient().PostAsJsonAsync("/api/nodes/register",
            new { code, nodeName = "PC", publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        (await expired.ErrorCode()).ShouldBe("Nodes.InvalidCode");
        var unknown = await app.CreateClient().PostAsJsonAsync("/api/nodes/register", new { code = "abc", nodeName = "PC" });
        (await unknown.ErrorCode()).ShouldBe("Nodes.InvalidCode");
    }

    [Fact]
    public async Task The_model_has_its_migration()
    {
        // Same guard as the store modules (ADR-0005): a model change without its migration fails here, not in Azure SQL.
        await using var db = new Toki.Host.Cloud.Persistence.CloudDesignTimeFactory().CreateDbContext([]);
        db.Database.GetMigrations().ShouldContain(m => m.EndsWith("_InitialCloud", StringComparison.Ordinal));
        db.Database.HasPendingModelChanges().ShouldBeFalse("Run: dotnet dotnet-ef migrations add <Name> --project src/Hosts/Toki.Host.Cloud --context CloudDbContext --output-dir Persistence/Migrations");
    }
}
