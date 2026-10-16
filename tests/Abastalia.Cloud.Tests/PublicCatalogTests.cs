using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Shouldly;
using Abastalia.Inventory.Contracts;
using Abastalia.Sales.Contracts;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-195: the store's catalog events become a public page, without sign-in and without cost or stock.</summary>
public sealed class PublicCatalogTests(CloudApp app) : IClassFixture<CloudApp>
{
    [Fact]
    public async Task Shown_products_are_public_follow_price_changes_and_an_older_event_never_wins()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner, "Carnicería Don Pepe");
        var (steak, ground, hidden) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var now = app.Clock.GetUtcNow().UtcDateTime;

        (await node.SendAsync(
            Events.Envelope(new CatalogItemChangedV1(steak, "Bistec de res", "Res", "Pound", 250m, true), now),
            Events.Envelope(new CatalogItemChangedV1(ground, "Carne molida", "Res", "Pound", 180m, true), now),
            Events.Envelope(new CatalogItemChangedV1(hidden, "Hígado", "Res", "Pound", 90m, true), now),
            Events.Envelope(new CatalogItemChangedV1(hidden, "Hígado", "Res", "Pound", 90m, false), now.AddMinutes(1)),
            Events.Envelope(new CatalogItemChangedV1(ground, "Nombre viejo", "Res", "Pound", 1m, true), now.AddHours(-1)), // arrives late
            Events.Envelope(new PriceChangedV1(steak, 250m, 275m, null, null), now.AddMinutes(2)),
            Events.Envelope(new CatalogSettingsChangedV1("18095550101"), now))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.ApplyAsync()).ShouldBe(7);

        var anonymous = app.CreateClient();
        var r = await anonymous.GetAsync($"/api/public/catalog/{node.StoreId}");
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        r.Headers.CacheControl!.MaxAge.ShouldBe(TimeSpan.FromMinutes(1));
        var page = await r.Content.ReadFromJsonAsync<JsonElement>();
        page.GetProperty("storeName").GetString().ShouldBe("Carnicería Don Pepe");
        page.GetProperty("whatsApp").GetString().ShouldBe("18095550101");
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => (i.GetProperty("name").GetString(), i.GetProperty("price").GetDecimal())).ShouldBe([("Bistec de res", 275m), ("Carne molida", 180m)]);
        items.ShouldAllBe(i => i.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "category", "name", "price", "productId", "unit" }));

        (await anonymous.GetAsync($"/api/public/catalog/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_disabled_business_has_no_public_catalog()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner);
        var staff = await app.StaffAsync();
        (await app.CreateClient().GetAsync($"/api/public/catalog/{node.StoreId}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await staff.Client.PostAsync($"/api/platform/tenants/{owner.TenantId}/disable", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await app.CreateClient().GetAsync($"/api/public/catalog/{node.StoreId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_public_catalog_is_rate_limited_per_address()
    {
        await using var limited = app.WithWebHostBuilder(b => b.UseSetting("Catalog:RequestsPerMinute", "2"));
        var anonymous = limited.CreateClient();
        var store = Guid.NewGuid();
        (await anonymous.GetAsync($"/api/public/catalog/{store}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync($"/api/public/catalog/{store}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync($"/api/public/catalog/{store}")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
