using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Inventory;

public sealed class InventoryTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Client(await app.AdminAsync());

    [Fact]
    public async Task Create_a_product_and_scan_it_by_barcode()
    {
        var c = await Admin();
        var cat = await c.CategoryAsync();
        var id = await c.ProductAsync(cat, "Salami Induveca", 185m, code: "7460312004518", stock: 10);

        var r = await c.GetFromJsonAsync<JsonElement>("/api/inventory/products/by-code/7460312004518");
        r.GetProperty("productId").GetGuid().ShouldBe(id);
        r.GetProperty("price").GetDecimal().ShouldBe(185m);
        r.GetProperty("stock").GetDecimal().ShouldBe(10m);
        r.GetProperty("notes").EnumerateArray().Select(n => n.GetString()).ShouldContain("molida");
    }

    [Fact]
    public async Task A_cashier_reads_the_current_prices_of_the_cart_products_with_their_presentations()
    {
        var c = await Admin();
        var beer = await c.ProductAsync(await c.CategoryAsync(), $"Cerveza {Guid.NewGuid().ToString()[..4]}", 120m);
        var box = (await (await c.PostAsJsonAsync($"/api/inventory/products/{beer}/presentations",
            new { Name = "Caja 12", Factor = 12m, Price = 1300m, WholesalePrice = 1250m })).Json()).GetProperty("id").GetGuid();
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));

        var prices = await cashier.GetFromJsonAsync<JsonElement>($"/api/inventory/products/prices?ids={beer}&ids={Guid.NewGuid()}");
        prices.EnumerateArray().Select(p => (p.GetProperty("presentationId").ValueKind == JsonValueKind.Null ? (Guid?)null : p.GetProperty("presentationId").GetGuid(),
            p.GetProperty("price").GetDecimal())).OrderBy(p => p.Item2).ShouldBe([(null, 120m), (box, 1300m)]);
        (await cashier.GetAsync("/api/inventory/products/prices")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_scale_label_carries_the_weight()
    {
        var c = await Admin();
        var cat = await c.CategoryAsync("Cerdo");
        var id = await c.ProductAsync(cat, "Chuleta", 165m, unit: "Pound", plu: "321");
        var label = "200032102350";
        label += Toki.Inventory.Domain.ScaleLabel.CheckDigit(label);

        var r = await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/by-code/{label}");
        r.GetProperty("productId").GetGuid().ShouldBe(id);
        r.GetProperty("labelQuantity").GetDecimal().ShouldBe(2.35m);

        var bad = await c.GetAsync($"/api/inventory/products/by-code/{label[..12]}{(label[12] - '0' + 1) % 10}");
        (await bad.ErrorCode()).ShouldBe("Inventory.LabelMisread");
    }

    [Fact]
    public async Task An_unknown_code_gives_404()
    {
        var r = await (await Admin()).GetAsync("/api/inventory/products/by-code/0000000000000");
        r.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_same_code_cannot_go_on_two_products()
    {
        var c = await Admin();
        var cat = await c.CategoryAsync();
        await c.ProductAsync(cat, "Uno", 10m, code: "111");
        var r = await c.PostAsJsonAsync("/api/inventory/products", new
        {
            Name = "Dos", CategoryId = cat, Unit = "Unit", ItbisRate = "General18", Price = 10m, Cost = 0m, Barcodes = new[] { "111" },
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_adjustment_does_not_take_out_more_than_there_is_and_stays_in_the_kardex()
    {
        var c = await Admin();
        var id = await c.ProductAsync(await c.CategoryAsync(), "Queso", 300m, unit: "Pound", stock: 5);

        var tooMuch = await c.PostAsJsonAsync("/api/inventory/adjustments", new { ProductId = id, Quantity = -6m, Reason = "Shrinkage" });
        tooMuch.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await tooMuch.ErrorCode()).ShouldBe("Inventory.OutOfStock");

        (await c.PostAsJsonAsync("/api/inventory/adjustments", new { ProductId = id, Quantity = -1.25m, Reason = "Shrinkage" })).EnsureSuccessStatusCode();
        (await c.StockAsync(id)).ShouldBe(3.75m);

        var kardex = await c.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{id}/movements");
        kardex.GetArrayLength().ShouldBe(2);
        kardex[0].GetProperty("quantity").GetDecimal().ShouldBe(-1.25m);
        kardex[0].GetProperty("resultingStock").GetDecimal().ShouldBe(3.75m);
    }

    [Fact]
    public async Task The_stock_list_filters_what_is_below_its_minimum_and_what_needs_a_count()
    {
        // TK-178: filtering after projecting into the record could not be translated (500).
        var c = await Admin();
        var cat = await c.CategoryAsync("Bajo mínimo");
        var r = await c.PostAsJsonAsync("/api/inventory/products", new
        {
            Name = "Costilla TK178", CategoryId = cat, Unit = "Pound", ItbisRate = "Exempt", Price = 180m, Cost = 120m, MinimumStock = 10m, InitialStock = 4m,
        });
        r.EnsureSuccessStatusCode();
        var low = (await r.Json()).GetProperty("id").GetGuid();
        var plenty = await c.ProductAsync(cat, "Longaniza TK178", 150m, stock: 50);

        var below = await c.GetFromJsonAsync<JsonElement>("/api/inventory/stock?belowMinimumOnly=true");
        var ids = below.EnumerateArray().Select(i => i.GetProperty("productId").GetGuid()).ToList();
        ids.ShouldContain(low);
        ids.ShouldNotContain(plenty);
        below.EnumerateArray().ShouldAllBe(i => i.GetProperty("belowMinimum").GetBoolean());

        (await c.GetAsync("/api/inventory/stock?needsCountOnly=true")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changing_the_price_needs_permission_and_the_version_detects_simultaneous_edits()
    {
        var admin = await Admin();
        var category = await admin.CategoryAsync();
        var id = await admin.ProductAsync(category, "Arroz", 45m);

        // A role that can edit products but not prices.
        var role = await (await admin.PostAsJsonAsync("/api/identity/roles", new { Name = "Almacén", Permissions = new[] { "products.manage", "inventory.view" } })).Json();
        var stockClerk = app.Client(await app.UserAsync(role.GetProperty("id").GetGuid()));

        object Change(int version, decimal price) => new
        {
            Version = version, Name = "Arroz Selecto", CategoryId = category,
            Unit = "Unit", ItbisRate = "General18", Price = price, SellWithoutStock = false, MinimumStock = 0, IsActive = true,
        };

        var noPermission = await stockClerk.PutAsJsonAsync($"/api/inventory/products/{id}", Change(0, 50m));
        (await noPermission.ErrorCode()).ShouldBe("Inventory.NoPricePermission");
        (await stockClerk.PutAsJsonAsync($"/api/inventory/products/{id}", Change(0, 45m))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var stale = await admin.PutAsJsonAsync($"/api/inventory/products/{id}", Change(0, 50m));
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/inventory/products/{id}", Change(1, 50m))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task An_update_without_the_maximum_keeps_it()
    {
        // R8 #22: a client that does not know the maximum (older register, other screen) no longer erases it.
        var admin = await Admin();
        var category = await admin.CategoryAsync();
        var id = (await (await admin.PostAsJsonAsync("/api/inventory/products", new
        {
            Name = "Aceite", CategoryId = category, Unit = "Unit", ItbisRate = "General18", Price = 300m, Cost = 0m, MinimumStock = 5m, MaximumStock = 20m,
        })).Json()).GetProperty("id").GetGuid();
        object Change(int version, decimal minimum) => new
        {
            Version = version, Name = "Aceite de soya", CategoryId = category, Unit = "Unit", ItbisRate = "General18", Price = 300m, SellWithoutStock = false,
            MinimumStock = minimum, IsActive = true,
        };

        (await admin.PutAsJsonAsync($"/api/inventory/products/{id}", Change(0, 6m))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var saved = await admin.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{id}");
        (saved.GetProperty("minimumStock").GetDecimal(), saved.GetProperty("maximumStock").GetDecimal()).ShouldBe((6m, 20m));
        (await (await admin.PutAsJsonAsync($"/api/inventory/products/{id}", Change(1, 25m))).ErrorCode()).ShouldBe("Validation"); // over the kept maximum
    }

    [Fact]
    public async Task Transferring_moves_stock_between_warehouses()
    {
        var c = await Admin();
        var id = await c.ProductAsync(await c.CategoryAsync(), "Pollo entero", 95m, unit: "Pound", stock: 20);
        var cold = (await (await c.PostAsJsonAsync("/api/inventory/warehouses", new { Name = "Cuarto frío" })).Json()).GetProperty("id").GetGuid();
        var store = (await c.GetFromJsonAsync<JsonElement>("/api/inventory/warehouses")).EnumerateArray().First(a => a.GetProperty("isMain").GetBoolean()).GetProperty("id").GetGuid();

        (await c.PostAsJsonAsync("/api/inventory/transfers", new { Source = store, Destination = cold, Lines = new[] { new { ProductId = id, Quantity = 8m } } }))
            .EnsureSuccessStatusCode();
        (await c.StockAsync(id)).ShouldBe(12m);
        (await c.StockAsync(id, cold)).ShouldBe(8m);
    }

    [Fact]
    public async Task The_count_is_blind_and_applying_it_asks_for_a_supervisor_from_someone_who_only_counts()
    {
        var admin = await Admin();
        var cat = await admin.CategoryAsync("Embutidos");
        var a = await admin.ProductAsync(cat, "Jamón", 250m, unit: "Pound", stock: 10, cost: 150, code: "JAMON-" + Guid.NewGuid().ToString()[..6]);
        var b = await admin.ProductAsync(cat, "Mortadela", 120m, unit: "Pound", stock: 4, cost: 80);
        var hamCode = (await admin.GetFromJsonAsync<JsonElement>($"/api/inventory/products/{a}")).GetProperty("barcodes")[0].GetProperty("code").GetString();

        var role = await (await admin.PostAsJsonAsync("/api/identity/roles", new { Name = "Contador", Permissions = new[] { "inventory.count" } })).Json();
        var counter = app.Client(await app.UserAsync(role.GetProperty("id").GetGuid()));

        var count = await (await counter.PostAsJsonAsync("/api/inventory/stock-counts", new { CategoryId = cat })).Json();
        var countId = count.GetProperty("stockCountId").GetGuid();
        count.GetProperty("productCount").GetInt32().ShouldBe(2);

        var reading = await counter.GetFromJsonAsync<JsonElement>($"/api/inventory/stock-counts/{countId}");
        reading.GetRawText().ShouldNotContain("system", Case.Insensitive);
        // Scanning or searching does not reveal the stock to someone who can only count.
        (await counter.GetFromJsonAsync<JsonElement>($"/api/inventory/products/by-code/{hamCode}")).GetProperty("stock").ValueKind.ShouldBe(JsonValueKind.Null);
        (await counter.GetFromJsonAsync<JsonElement>("/api/inventory/products?search=Jam"))[0].GetProperty("stock").ValueKind.ShouldBe(JsonValueKind.Null);

        (await counter.PutAsJsonAsync($"/api/inventory/stock-counts/{countId}/lines", new { Lines = new[] { new { ProductId = a, Counted = 9.5m }, new { ProductId = b, Counted = 4m } } }))
            .EnsureSuccessStatusCode();

        // The store keeps selling after the shelf was counted: 3 lb of jamón go out before the count is applied.
        (await app.CommandAsync(new MoveStock(a, 3m, Inbound: false))).IsSuccess.ShouldBeTrue();

        var noSupervisor = await counter.PostAsJsonAsync($"/api/inventory/stock-counts/{countId}/apply", new { });
        noSupervisor.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await noSupervisor.ErrorCode()).ShouldBe("Approval.Required");

        var approval = await (await counter.PostAsJsonAsync("/api/identity/approvals",
            new { Code = "admin", Pin = TokiApp.AdminPin, Permission = "inventory.adjust", ResourceId = countId })).Json();
        var applied = await counter.PostAsJsonAsync($"/api/inventory/stock-counts/{countId}/apply", new { SupervisorApprovalId = approval.GetProperty("approvalId").GetGuid() });
        applied.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await applied.Json();
        result.GetProperty("differences").GetArrayLength().ShouldBe(1);
        // TK-201: the value at cost (−75) is not for someone without costs.view, even with a supervisor's approval to apply.
        result.GetProperty("differenceValue").ValueKind.ShouldBe(JsonValueKind.Null);
        result.GetProperty("differences")[0].GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);

        (await admin.StockAsync(a)).ShouldBe(6.5m); // counted 9.5 minus the 3 sold after counting
        (await app.KardexMismatchesAsync()).ShouldBeEmpty();

        // The approval is single-use.
        var again = await counter.PostAsJsonAsync($"/api/inventory/stock-counts/{countId}/apply", new { SupervisorApprovalId = approval.GetProperty("approvalId").GetGuid() });
        again.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }
}
