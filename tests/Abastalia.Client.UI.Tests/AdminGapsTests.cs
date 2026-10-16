using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Shouldly;
using Abastalia.Client.UI.Pages.Admin;
using static Abastalia.Client.UI.Tests.AdminInventoryTests;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-191 (suggested order), TK-200 (Z and statement to print as PDF) and TK-201 (costs only with costs.view).</summary>
public sealed class AdminGapsTests
{
    private static readonly Guid Farm = Guid.NewGuid(), Ribs = Guid.NewGuid(), Salt = Guid.NewGuid(), Beer = Guid.NewGuid(), Box = Guid.NewGuid();

    [Fact]
    public void The_suggested_order_groups_by_last_supplier_and_creates_a_draft_without_NCF()
    {
        var (ctx, api) = ScreenContext.Create("purchasing.manage");
        using var _ = ctx;
        api.Json("GET", "/api/purchasing/suggested-order", new object[]
            {
                new
                {
                    supplierId = (Guid?)Farm, supplier = "Granja del Cibao", subtotal = 6175m,
                    lines = new[]
                    {
                        new { productId = Ribs, product = "Costilla", unit = "Pound", itbisRate = "Exempt", stock = 15.5m, minimumStock = 20m, maximumStock = 50m, suggested = 35m,
                            lastCost = (decimal?)125m, presentationId = (Guid?)null, presentation = (string?)null, factor = 1m, quantity = 35m },
                        new { productId = Beer, product = "Cerveza", unit = "Unit", itbisRate = "Exempt", stock = 12m, minimumStock = 20m, maximumStock = 40m, suggested = 36m,
                            lastCost = (decimal?)600m, presentationId = (Guid?)Box, presentation = (string?)"Caja 12", factor = 12m, quantity = 3m },
                    },
                },
                new
                {
                    supplierId = (Guid?)null, supplier = (string?)null, subtotal = 0m,
                    lines = new[] { new { productId = Salt, product = "Sal", unit = "Unit", itbisRate = "General18", stock = 2m, minimumStock = 5m, maximumStock = 10m, suggested = 8m,
                        lastCost = (decimal?)null, presentationId = (Guid?)null, presentation = (string?)null, factor = 1m, quantity = 8m } },
                },
            })
           .Respond("POST", "/api/purchasing/purchases", body =>
                FakeApi.Response(HttpStatusCode.Created, new { id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid(), subtotal = 4375m, itbis = 0m, total = 4375m }));
        var page = ctx.Render<SuggestedOrder>();

        page.WaitForAssertion(() => page.FindAll("section.esc-card").Count.ShouldBe(2));
        var farm = page.Find("section[aria-label='Granja del Cibao']");
        farm.TextContent.ShouldContain("35 lb");
        farm.TextContent.ShouldContain("4,375.00");
        farm.TextContent.ShouldContain("3 × Caja 12"); // in the presentation last bought, with what it adds up to
        farm.TextContent.ShouldContain("36 unidades");
        farm.TextContent.ShouldContain("1,800.00");
        page.Find("section[aria-label='Sin compras anteriores']").QuerySelector("button").ShouldBeNull(); // no supplier to order from
        page.Find("button[aria-label='Crear compra a Granja del Cibao']").Click();

        page.WaitForAssertion(() => page.Find(".esc-notice--success").TextContent.ShouldContain("Compra en borrador para Granja del Cibao"));
        var sent = api.Requests.Single(r => r.Method == "POST").Body;
        sent.ShouldContain($"\"supplierId\":\"{Farm}\",\"ncf\":\"\"");
        sent.ShouldContain($"\"lines\":[{{\"productId\":\"{Ribs}\",\"presentationId\":null,\"quantity\":35,\"unitCost\":125,\"itbisRate\":\"Exempt\"}},"
            + $"{{\"productId\":\"{Beer}\",\"presentationId\":\"{Box}\",\"quantity\":3,\"unitCost\":600,\"itbisRate\":\"Exempt\"}}]");
        Button(page, "Borrador creado").ShouldNotBeNull();
    }

    [Fact]
    public void Without_costs_view_the_inventory_shows_no_cost_or_value()
    {
        var (ctx, api) = ScreenContext.Create("inventory.view");
        using var _ = ctx;
        api.Json("GET", "/api/inventory/warehouses", new[] { new { id = Guid.NewGuid(), name = "Tienda", isActive = true, isMain = true } })
           .Json("GET", "/api/inventory/stock", new[]
            {
                new { productId = Ribs, name = "Costilla", category = "Res", unit = "Pound", quantity = 4.5m, cost = (decimal?)null, value = (decimal?)null, belowMinimum = true, needsCount = false },
            });
        var page = ctx.Render<Inventory>();

        page.WaitForAssertion(() => page.FindAll("tbody tr.selectable").Count.ShouldBe(1));
        page.Find("thead").TextContent.ShouldNotContain("Costo");
        page.Find("thead").TextContent.ShouldNotContain("Valor");
        page.Find(".esc-stat__label").TextContent.ShouldContain("Productos activos");
        page.Markup.ShouldNotContain("Valor al costo");
    }

    [Fact]
    public void The_Z_downloads_as_a_page_to_print_with_the_business_and_the_difference()
    {
        var (ctx, api) = ScreenContext.Create("reports.view");
        using var _ = ctx;
        var shift = Guid.NewGuid();
        var supervisor = Guid.NewGuid();
        api.Json("GET", "/api/reports/shifts", new[]
            {
                new { shiftId = shift, register = "Caja 1", cashier = "Yudelka M.", openedUtc = DateTime.UtcNow.AddHours(-8), closedUtc = (DateTime?)DateTime.UtcNow, zNumber = (long?)7, openingFloat = 1000m, expectedCash = (decimal?)3500m, countedCash = (decimal?)3450m, difference = (decimal?)-50m },
            })
           .Json("GET", $"/api/cash/shifts/{shift}/z", new
            {
                shiftId = shift, zNumber = 7, register = "Caja 1", cashier = "Yudelka M.", openedUtc = DateTime.UtcNow.AddHours(-8), closedUtc = DateTime.UtcNow, openingFloat = 1000m,
                byMethod = new[] { new { method = "Cash", salePayments = 2500m, voids = 0m, refunds = 0m, customerPayments = 100m, net = 2600m } },
                saleCount = 12, voidedCount = 1, customerPayments = 100m, rounding = 0m, cashIn = 0m, withdrawals = 100m, expenses = 0m,
                expectedCash = 3500m, countedCash = 3450m, difference = -50m, closingSupervisorId = supervisor, auditChainHead = "#42 abc", orderDeposits = 300m,
            })
           .Json("GET", "/api/settings/business", new { name = "Carnicería La Esquina", rnc = "131245678", address = "Calle 1", phone = "809-555-0101", receiptFooter = (string?)null, discountCapPercent = 10m })
           .Json("GET", "/api/identity/users/active", new[] { new { id = supervisor, name = "Ramón D.", code = "02" } });
        var page = ctx.Render<Shifts>();

        page.WaitForAssertion(() => page.Find("tbody tr.selectable").Click());
        page.WaitForAssertion(() => Button(page, "Descargar para PDF").Click());

        page.WaitForAssertion(() => page.Markup.ShouldContain("Listo: cierre-z-7.html"));
        var call = ctx.JSInterop.Invocations.Single(i => i.Identifier == "escuadra.download");
        (call.Arguments[0], call.Arguments[1]).ShouldBe(("cierre-z-7.html", "text/html"));
        var html = Encoding.UTF8.GetString((byte[])call.Arguments[2]!);
        html.ShouldContain("Cierre de turno Z #7");
        html.ShouldContain("Carnicería La Esquina");
        html.ShouldContain("RNC 131245678");
        html.ShouldContain("Faltaron RD$ 50.00");
        html.ShouldContain("Ramón D.");
        // R8 #15: order deposits on their own line, apart from payments on credit.
        html.ShouldContain("<tr><td>Abonos de fiado</td><td class=\"num\">100.00</td></tr>");
        html.ShouldContain("<tr><td>Abonos de pedidos</td><td class=\"num\">300.00</td></tr>");
        html.ShouldContain("aria-label=\"Abastalia\""); // logo C5
        html.ShouldContain("@page { size: A4");
    }
}
