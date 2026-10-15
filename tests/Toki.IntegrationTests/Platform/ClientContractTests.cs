using System.Collections;
using System.Net.Http.Json;
using System.Reflection;
using Shouldly;
using Toki.IntegrationTests.Infra;
using Toki.SharedKernel;
using C = Toki.Client.UI.Api;

namespace Toki.IntegrationTests.Platform;

/// <summary>TK-124 (R7 #13b): the register app's own records and JSON options against the real local API, so a renamed or dropped field fails here
/// instead of showing an empty screen. Every non-nullable field must arrive (no null, no empty Guid, no default date), nested records too.
/// Own app: it does the first-run setup itself, the way the register does.</summary>
public sealed class ClientContractTests(TokiApp app) : IClassFixture<TokiApp>
{
    private static readonly NullabilityInfoContext Nullability = new();

    [Fact]
    public async Task The_register_app_reads_every_response_it_uses_with_its_own_records()
    {
        var anonymous = app.Anonymous();
        var setup = await Send<C.SetupResult>(anonymous, HttpMethod.Post, "/api/identity/setup", new C.SetupRequest("Dueña", "admin", TokiApp.AdminPin, "PC principal"));
        var device = app.Anonymous(setup.DeviceToken);
        var cashiers = await Send<List<C.ActiveUser>>(device, HttpMethod.Get, "/api/identity/users/active");
        cashiers.ShouldHaveSingleItem().Name.ShouldBe("Dueña");
        var session = await Send<C.OpenedSession>(device, HttpMethod.Post, "/api/identity/sessions", new { Code = "admin", Pin = TokiApp.AdminPin });
        var api = app.Client(new UserSession(session.Token, setup.DeviceToken, session.UserId));
        (await Send<C.Me>(api, HttpMethod.Get, "/api/identity/me")).Permissions.ShouldNotBeEmpty();

        await Send<C.Empty>(api, HttpMethod.Put, "/api/settings/business", new C.BusinessProfile("Carnicería La Esquina", "131245678", "Calle 1", "809-555-0101", "Gracias", 10));
        (await Send<C.BusinessProfile>(api, HttpMethod.Get, "/api/settings/business")).Rnc.ShouldBe("131245678");
        foreach (var type in new[] { 32, 34 })
            await Send<C.Created>(api, HttpMethod.Post, "/api/fiscal/sequences", new { Type = type, From = 1, To = 1000, ExpiresOn = "2030-12-31" });
        (await Send<List<C.SequenceAlert>>(api, HttpMethod.Get, "/api/fiscal/alerts")).ShouldNotBeEmpty();
        await Send<C.LicenseFeatures>(api, HttpMethod.Get, "/api/settings/features");
        (await Send<List<C.User>>(api, HttpMethod.Get, "/api/identity/users")).ShouldHaveSingleItem().Role.ShouldNotBeNullOrEmpty();
        (await Send<C.RoleList>(api, HttpMethod.Get, "/api/identity/roles")).AvailablePermissions.ShouldNotBeEmpty();
        (await Send<List<C.Device>>(api, HttpMethod.Get, "/api/identity/devices")).ShouldHaveSingleItem().Name.ShouldBe("PC principal");

        // Catalog
        var category = (await Send<C.Created>(api, HttpMethod.Post, "/api/inventory/categories", new { Name = "Res", Color = "#B42318", Notes = new[] { "molida" } })).Id;
        (await Send<List<C.Category>>(api, HttpMethod.Get, "/api/inventory/categories")).ShouldHaveSingleItem().Notes.ShouldBe(["molida"]);
        var steak = (await Send<C.Created>(api, HttpMethod.Post, "/api/inventory/products", new C.CreateProduct("Bistec de res", category, C.UnitOfMeasure.Pound, null,
            C.ItbisRate.General18, 250, 230, 150, false, 5, ["7460000000017"], 100))).Id;
        var summary = (await Send<List<C.ProductSummary>>(api, HttpMethod.Get, $"/api/inventory/products?categoryId={category}")).ShouldHaveSingleItem();
        (summary.Price, summary.Stock).ShouldBe((250m, 100m));
        var detail = await Send<C.ProductDetail>(api, HttpMethod.Get, $"/api/inventory/products/{steak}");
        (detail.Cost, detail.WholesalePrice, detail.Unit).ShouldBe((150m, 230m, C.UnitOfMeasure.Pound));
        var byCode = await Send<C.ProductByCode>(api, HttpMethod.Get, "/api/inventory/products/by-code/7460000000017");
        (byCode.ProductId, byCode.Price, byCode.Factor).ShouldBe((steak, 250m, 1m));

        // Register and shift
        var register = (await Send<C.Created>(api, HttpMethod.Post, "/api/cash/registers", new { Name = "Caja 1" })).Id;
        var shift = (await Send<C.ShiftOpened>(api, HttpMethod.Post, "/api/cash/shifts", new { RegisterId = register, OpeningFloat = 2000m })).ShiftId;
        var current = await Send<C.CurrentShift>(api, HttpMethod.Get, "/api/cash/shifts/current");
        (current.ShiftId, current.Register, current.OpeningFloat).ShouldBe((shift, "Caja 1", 2000m));
        (await Send<List<C.RegisterInfo>>(api, HttpMethod.Get, "/api/cash/registers")).ShouldHaveSingleItem().OpenShiftId.ShouldBe(shift);

        // Sale, receipt, list and detail
        var sold = await Send<C.SaleRegistered>(api, HttpMethod.Post, "/api/sales", new C.RegisterSale(Guid.CreateVersion7(), shift,
            [new C.SaleLine(steak, null, 2)], [new C.Payment(C.PaymentMethod.Cash, 1000)], 500, C.EcfType.Consumer32, null, null, null, C.PriceLevel.Retail, null, []));
        (sold.Total, sold.Change, sold.Number).ShouldBe((500m, 500m, 1L));
        var line = sold.Receipt.Lines.ShouldHaveSingleItem();
        (line.Quantity, line.Price, line.Total).ShouldBe((2m, 250m, 500m));
        sold.Receipt.Itbis.ShouldHaveSingleItem().Itbis.ShouldBeGreaterThan(0);
        sold.Receipt.Payments.ShouldHaveSingleItem().Received.ShouldBe(1000m);
        (await Send<Toki.Hardware.Receipt>(api, HttpMethod.Get, $"/api/sales/{sold.SaleId}/receipt")).ENcf.ShouldBe(sold.ENcf);
        (await Send<List<C.SaleSummary>>(api, HttpMethod.Get, $"/api/sales?shiftId={shift}")).ShouldHaveSingleItem().Total.ShouldBe(500m);
        var sale = await Send<C.SaleDetail>(api, HttpMethod.Get, $"/api/sales/{sold.SaleId}");
        (sale.Lines.ShouldHaveSingleItem().Quantity, sale.Payments.ShouldHaveSingleItem().Amount, sale.Total).ShouldBe((2m, 500m, 500m));

        // Return with a supervisor's approval
        var approval = await Send<C.ApprovalGranted>(api, HttpMethod.Post, "/api/identity/approvals",
            new { Code = "admin", Pin = TokiApp.AdminPin, Permission = "sales.return", ResourceId = sold.SaleId });
        var returned = await Send<C.ReturnRecorded>(api, HttpMethod.Post, $"/api/sales/{sold.SaleId}/returns", new
        {
            ReturnId = Guid.CreateVersion7(), ShiftId = shift, Lines = new[] { new C.ReturnLine(sale.Lines[0].Id, 1) }, Method = C.PaymentMethod.Cash,
            Reason = "Devolución", SupervisorApprovalId = approval.ApprovalId,
        });
        (returned.Total, returned.Method).ShouldBe((250m, C.PaymentMethod.Cash));
        var afterReturn = await Send<C.SaleDetail>(api, HttpMethod.Get, $"/api/sales/{sold.SaleId}");
        (afterReturn.Lines[0].ReturnedQuantity, afterReturn.Returns.ShouldHaveSingleItem().Total).ShouldBe((1m, 250m));

        // Z report and reports
        var z = await Send<C.ZReport>(api, HttpMethod.Get, $"/api/cash/shifts/{shift}/z");
        (z.SaleCount, z.ExpectedCash).ShouldBe((1, 2250m));
        z.ByMethod.Single(m => m.Method == C.PaymentMethod.Cash).SalePayments.ShouldBe(500m);
        var today = BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime);
        var range = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        var day = (await Send<List<C.DailySales>>(api, HttpMethod.Get, $"/api/reports/sales/by-day?{range}")).ShouldHaveSingleItem();
        (day.SaleCount, day.SalesWithItbis, day.Returns).ShouldBe((1, 500m, 250m));
        (await Send<List<C.ProductSales>>(api, HttpMethod.Get, $"/api/reports/sales/by-product?{range}")).ShouldHaveSingleItem().Quantity.ShouldBe(1m);
        (await Send<List<C.ShiftSummary>>(api, HttpMethod.Get, $"/api/reports/shifts?{range}")).ShouldHaveSingleItem().OpeningFloat.ShouldBe(2000m);

        // Customers
        var customer = (await Send<C.Created>(api, HttpMethod.Post, "/api/customers", new C.NewCustomer("Ana Pérez", null, "809-555-0102", null, false))).Id;
        (await Send<List<C.CustomerSummary>>(api, HttpMethod.Get, "/api/customers?search=Ana")).ShouldHaveSingleItem().Id.ShouldBe(customer);
        (await Send<C.CustomerDetail>(api, HttpMethod.Get, $"/api/customers/{customer}")).Name.ShouldBe("Ana Pérez");
    }

    [Fact]
    public void A_missing_field_or_id_is_caught()
    {
        Should.Throw<ShouldAssertException>(() => AssertFilled(new List<C.ActiveUser> { new(Guid.CreateVersion7(), null!, "admin") }, "renamed name"));
        Should.Throw<ShouldAssertException>(() => AssertFilled(new C.Created(Guid.Empty), "renamed id"));
        AssertFilled(new C.SaleSummary(Guid.CreateVersion7(), 1, "E320000000001", DateTime.UtcNow, "Completed", "Ana", null, 100), "nullable customer");
    }

    /// <summary>Sends like <c>TokiApi</c> (its JSON options both ways) and checks the answer has every non-nullable field.</summary>
    private static async Task<T> Send<T>(HttpClient client, HttpMethod method, string url, object? body = null) where T : class
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: C.TokiApi.Json);
        using var r = await client.SendAsync(request);
        r.IsSuccessStatusCode.ShouldBeTrue($"{method} {url}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        if (typeof(T) == typeof(C.Empty)) return default!;
        var value = await r.Content.ReadFromJsonAsync<T>(C.TokiApi.Json);
        AssertFilled(value.ShouldNotBeNull($"{url} answered nothing"), $"{url} {typeof(T).Name}");
        return value;
    }

    private static void AssertFilled(object value, string path)
    {
        if (value is IEnumerable items and not string)
        {
            var i = 0;
            foreach (var item in items) AssertFilled(item, $"{path}[{i++}]");
            return;
        }
        if (value.GetType().Namespace != typeof(C.TokiApi).Namespace) return;
        foreach (var p in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var v = p.GetValue(value);
            var where = $"{path}.{p.Name}";
            if (v is null)
            {
                Nullability.Create(p).ReadState.ShouldBe(NullabilityState.Nullable, $"{where} is missing");
                continue;
            }
            (v is Guid g && g == Guid.Empty).ShouldBeFalse($"{where} is an empty id");
            (v is DateTime d && d == default).ShouldBeFalse($"{where} has no date");
            AssertFilled(v, where);
        }
    }
}
