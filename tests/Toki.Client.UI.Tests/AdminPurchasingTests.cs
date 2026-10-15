using System.Net;
using System.Text.Json;
using Bunit;
using Shouldly;
using Toki.Client.UI.Pages.Admin;
using static Toki.Client.UI.Tests.AdminInventoryTests;

namespace Toki.Client.UI.Tests;

/// <summary>TK-185: purchases, suppliers, expenses with NCF and supplier payments.</summary>
public sealed class AdminPurchasingTests
{
    private static readonly Guid Farm = Guid.NewGuid(), NoRnc = Guid.NewGuid(), Steak = Guid.NewGuid(), Main = Guid.NewGuid();

    private static void Suppliers(FakeApi api) =>
        api.Json("GET", "/api/purchasing/suppliers", new[]
        {
            new { id = Farm, name = "Granja del Cibao", rnc = (string?)"101234567", phone = (string?)null, contact = (string?)"Pedro", creditDays = 30, isActive = true },
            new { id = NoRnc, name = "Colmado Pepe", rnc = (string?)null, phone = (string?)null, contact = (string?)null, creditDays = 0, isActive = true },
        });

    [Fact]
    public void A_purchase_is_checked_next_to_each_field_then_saved_and_received_with_the_last_cost()
    {
        var (ctx, api) = ScreenContext.Create("purchasing.manage");
        using var _ = ctx;
        Suppliers(api);
        api.Json("GET", "/api/purchasing/purchases", Array.Empty<object>())
           .Json("GET", "/api/inventory/warehouses", new[] { new { id = Main, name = "Tienda", isActive = true, isMain = true } })
           .Json("GET", "/api/inventory/products", new[]
            {
                new { id = Steak, name = "Bistec de res", category = "Res", unit = "Pound", itbisRate = "General18", price = 250m, wholesalePrice = (decimal?)null, stock = (decimal?)10m, isActive = true },
            })
           .Json("GET", $"/api/inventory/products/{Steak}", new
            {
                id = Steak, version = 0, name = "Bistec de res", categoryId = Guid.NewGuid(), unit = "Pound", plu = (string?)null, itbisRate = "General18",
                price = 250m, wholesalePrice = (decimal?)null, cost = 140.5m, sellWithoutStock = false, minimumStock = 5m, isActive = true,
            })
           .Respond("POST", "/api/purchasing/purchases", body =>
            {
                var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
                api.Respond("POST", $"/api/purchasing/purchases/{id}/post", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
                return FakeApi.Response(HttpStatusCode.Created, new { id, subtotal = 1405m, itbis = 252.9m, total = 1657.9m });
            });
        var page = ctx.Render<Purchases>();

        page.WaitForAssertion(() => page.Find(".esc-empty__title").TextContent.ShouldBe("No hay compras en este periodo"));
        Button(page, "Nueva compra").Click();
        page.WaitForAssertion(() => Button(page, "Recibir mercancía").Click());
        var errors = page.FindAll(".esc-field__error").Select(e => e.TextContent).ToList();
        errors.ShouldContain(e => e.Contains("Elija el suplidor."));
        errors.ShouldContain(e => e.Contains("El NCF es B01"));
        page.Find("[role=dialog] option[value='" + NoRnc + "']").HasAttribute("disabled").ShouldBeTrue();
        api.Requests.ShouldNotContain(r => r.Method == "POST");

        page.Find("[role=dialog] select").Change(Farm.ToString());
        page.Find("input[maxlength='13']").Change("b0100000001");
        page.Find(".picker input").Input("bistec");
        page.WaitForAssertion(() => page.Find(".picker__list button").Click());
        page.WaitForAssertion(() => page.Find("input[aria-label='Costo de Bistec de res']").GetAttribute("value").ShouldBe("140.5"));
        page.Find("input[aria-label='Cantidad de Bistec de res']").Input("10");
        page.Find(".totals__grand").TextContent.ShouldBe("RD$ 1,657.90");
        Button(page, "Recibir mercancía").Click();

        page.WaitForAssertion(() => page.Find(".esc-notice--success").TextContent.ShouldContain("Compra de Granja del Cibao recibida"));
        var sent = api.Requests.Single(r => r.Route == "/api/purchasing/purchases").Body;
        sent.ShouldContain("\"ncf\":\"B0100000001\"");
        sent.ShouldContain($"\"lines\":[{{\"productId\":\"{Steak}\",\"presentationId\":null,\"quantity\":10,\"unitCost\":140.5,\"itbisRate\":\"General18\"}}]");
        sent.ShouldContain("\"terms\":\"Credit\"");
        api.Requests.ShouldContain(r => r.Route.EndsWith("/post", StringComparison.Ordinal));
    }

    [Fact]
    public void A_supplier_with_a_wrong_RNC_is_not_sent()
    {
        var (ctx, api) = ScreenContext.Create("purchasing.manage");
        using var _ = ctx;
        Suppliers(api);
        var page = ctx.Render<Pages.Admin.Suppliers>();

        page.WaitForAssertion(() => page.FindAll("tbody tr.selectable").Count.ShouldBe(2));
        page.Find("tbody").TextContent.ShouldContain("Sin RNC");
        Button(page, "Nuevo suplidor").Click();
        page.Find("[role=dialog] input[maxlength='150']").Change("Distribuidora Núñez");
        page.Find("[role=dialog] input[maxlength='13']").Change("12345");
        Button(page, "Guardar").Click();
        page.Find(".esc-field__error").TextContent.ShouldContain("El RNC tiene 9 números y la cédula 11.");
        api.Requests.ShouldNotContain(r => r.Method == "POST");
    }

    [Fact]
    public void What_is_owed_shows_the_overdue_part_and_a_payment_goes_to_the_supplier_account()
    {
        var (ctx, api) = ScreenContext.Create("purchasing.manage", "reports.view");
        using var _ = ctx;
        var payment = Guid.NewGuid();
        api.Json("GET", "/api/reports/payables", new[]
            {
                new { supplierId = Farm, supplier = "Granja del Cibao", notYetDue = 1000m, overdue1To30 = 500m, overdue31To60 = 0m, overdue61To90 = 0m, overdueOver90 = 0m, total = 1500m },
            })
           .Json("GET", $"/api/purchasing/suppliers/{Farm}/statement", new
            {
                supplierId = Farm, supplier = "Granja del Cibao", balance = 1500m,
                lines = new[] { new { date = new DateOnly(2026, 9, 1), type = "Compra", document = "B0100000001", charge = 1500m, payment = 0m, balance = 1500m, dueDate = (DateOnly?)new DateOnly(2026, 10, 1), paymentId = (Guid?)null } },
            })
           .Json("POST", "/api/purchasing/payments", new { supplierPaymentId = payment, allocations = new[] { new { payableId = Guid.NewGuid(), ncf = "B0100000001", amount = 500m, remainingBalance = 1000m } } });
        var page = ctx.Render<Payables>();

        page.WaitForAssertion(() => page.FindAll(".esc-stat__value")[1].TextContent.ShouldBe("RD$ 500.00"));
        Button(page, "Pagar").Click();
        page.WaitForAssertion(() => page.Find("[role=dialog] input.esc-num").GetAttribute("value").ShouldBe("1500"));
        page.Find("[role=dialog] input.esc-num").Change("2000");
        Button(page, "Registrar pago").Click();
        page.Find(".esc-field__error").TextContent.ShouldContain("Es más de lo que se le debe (RD$ 1,500.00)");

        page.Find("[role=dialog] input.esc-num").Change("500");
        Button(page, "Registrar pago").Click();
        page.WaitForAssertion(() => page.Find(".esc-notice--success").TextContent.Trim().ShouldBe("Pago de RD$ 500.00 aplicado a B0100000001."));
        api.Requests.Single(r => r.Route == "/api/purchasing/payments").Body.ShouldContain($"\"supplierId\":\"{Farm}\",\"amount\":500,\"method\":\"Transfer\"");
    }
}
