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
    public async Task TK280_the_store_logo_is_served_as_a_PNG_the_newest_wins_and_a_removed_one_goes()
    {
        var owner = await app.OwnerAsync();
        var node = await app.StoreWithNodeAsync(owner, "Colmado El Sol");
        var anonymous = app.CreateClient();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
        (await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/catalog/{node.StoreId}")).GetProperty("logoVersion").ValueKind.ShouldBe(JsonValueKind.Null);

        (await node.SendAsync(
            Events.Envelope(new Abastalia.Settings.Contracts.BusinessLogoChangedV1(png), now),
            Events.Envelope(new Abastalia.Settings.Contracts.BusinessLogoChangedV1(null), now.AddHours(-1)), // an older removal arrives late
            Events.Envelope(new CatalogSettingsChangedV1("18095550101"), now.AddMinutes(1)))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await app.ApplyAsync();

        var page = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/catalog/{node.StoreId}");
        var version = page.GetProperty("logoVersion").GetString().ShouldNotBeNull();
        page.GetProperty("whatsApp").GetString().ShouldBe("18095550101"); // the WhatsApp and the logo share the row, not their times
        var logo = await anonymous.GetAsync($"/api/public/catalog/{node.StoreId}/logo?v={version}");
        logo.StatusCode.ShouldBe(HttpStatusCode.OK);
        logo.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        logo.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        (await logo.Content.ReadAsByteArrayAsync()).ShouldBe(png);

        // Something that is not a PNG never becomes an image/png of the catalog; removing it takes it off the page.
        (await node.SendAsync(Events.Envelope(new Abastalia.Settings.Contracts.BusinessLogoChangedV1("<svg/>"u8.ToArray()), now.AddMinutes(2)))).EnsureSuccessStatusCode();
        await app.ApplyAsync();
        (await anonymous.GetAsync($"/api/public/catalog/{node.StoreId}/logo")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await node.SendAsync(Events.Envelope(new Abastalia.Settings.Contracts.BusinessLogoChangedV1(png), now.AddMinutes(3)),
            Events.Envelope(new Abastalia.Settings.Contracts.BusinessLogoChangedV1(null), now.AddMinutes(4)))).EnsureSuccessStatusCode();
        await app.ApplyAsync();
        (await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/catalog/{node.StoreId}")).GetProperty("logoVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        (await anonymous.GetAsync($"/api/public/catalog/{node.StoreId}/logo")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await anonymous.GetAsync($"/api/public/catalog/{Guid.NewGuid()}/logo")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
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
