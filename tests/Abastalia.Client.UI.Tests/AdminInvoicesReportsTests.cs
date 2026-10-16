using System.Net;
using System.Net.Http.Headers;
using Bunit;
using Shouldly;
using Abastalia.Client.UI.Pages.Admin;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-187: finding an invoice and reprinting it; downloading reports as files.</summary>
public sealed class AdminInvoicesReportsTests
{
    [Fact]
    public void An_invoice_is_found_by_its_eNCF_opened_and_shown_for_reprint()
    {
        var (ctx, api) = ScreenContext.Create("reports.view", "sales.create");
        using var _ = ctx;
        var id = Guid.NewGuid();
        api.Json("GET", "/api/sales", Array.Empty<object>())
           .Json("GET", $"/api/sales/{id}", new
           {
               id, number = 123L, eNcf = "E320000000123", documentType = "Consumer32", dateUtc = DateTime.UtcNow, status = "Voided", register = "Caja 1", cashier = "Yudelka",
               customerId = (Guid?)null, customer = (string?)null, discount = 0m, itbis = 38.14m, total = 250m, change = 0m, voidReason = "Cliente se arrepintió",
               lines = new[] { new { id = Guid.NewGuid(), description = "Bistec de res", quantity = 1m, unitPrice = 250m, discount = 0m, total = 250m, itbis = 38.14m, returnedQuantity = 0m, note = (string?)null, unit = "Pound" } },
               payments = new[] { new { method = "Cash", amount = 250m, received = (decimal?)300m, reference = (string?)null } },
               returns = Array.Empty<object>(),
           })
           .Json("GET", $"/api/sales/{id}/receipt", RegisterTests.ReceiptFor(250m) with { ENcf = "E320000000123", IsVoided = true });
        var page = ctx.Render<Invoices>();
        page.WaitForAssertion(() => page.Markup.ShouldContain("No hay facturas en estos días"));

        api.Json("GET", "/api/sales", new[] { new { id, number = 123L, eNcf = "E320000000123", dateUtc = DateTime.UtcNow, status = "Voided", cashier = "Yudelka", customer = (string?)null, total = 250m } });
        page.Find("input[aria-label='Buscar factura']").Input("e320000000123");
        page.Find("form").Submit();
        page.WaitForAssertion(() => page.Find("tbody tr").TextContent.ShouldContain("Anulada"));
        api.Requests.Last(r => r.Route.StartsWith("/api/sales?")).Route.ShouldBe("/api/sales?search=E320000000123&page=1");

        page.Find("tbody tr").Click();
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain("Motivo: Cliente se arrepintió"));
        page.Find("[role=dialog]").TextContent.ShouldContain("1.000 lb");
        page.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("reimprimir")).Click();
        page.WaitForAssertion(() => page.Find("[role=dialog] h2").TextContent.ShouldBe("Factura E320000000123"));
        page.Find(".receipt").ShouldNotBeNull();
    }

    [Fact]
    public void A_report_downloads_as_a_file_for_the_chosen_period()
    {
        var (ctx, api) = ScreenContext.Create("fiscal.reports");
        using var _ = ctx;
        api.Respond("GET", "/api/reports/dgii/607", _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            r.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "607-20261001-20261015.xlsx" };
            return r;
        });
        var page = ctx.Render<Reports>();

        page.Find("input[aria-label='Desde']").Change("2026-10-01");
        page.Find("input[aria-label='Hasta']").Change("2026-10-15");
        page.Find("button[aria-label='Descargar Formato 607 · ventas en Excel']").Click();

        page.WaitForAssertion(() => page.Markup.ShouldContain("Listo: 607-20261001-20261015.xlsx"));
        api.Requests.Single(r => r.Route.StartsWith("/api/reports/dgii/607")).Route.ShouldBe("/api/reports/dgii/607?format=xlsx&from=2026-10-01&to=2026-10-15");
        var call = ctx.JSInterop.Invocations.Single(i => i.Identifier == "escuadra.download");
        call.Arguments[0].ShouldBe("607-20261001-20261015.xlsx");
        call.Arguments[2].ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void A_refused_period_is_explained()
    {
        var (ctx, api) = ScreenContext.Create("reports.view");
        using var _ = ctx;
        api.Respond("GET", "/api/reports/sales/by-day", _ => FakeApi.Problem(HttpStatusCode.BadRequest, "Reports.InvalidPeriod", "El período…"));
        var page = ctx.Render<Reports>();

        page.Find("button[aria-label='Descargar Ventas por día en CSV']").Click();

        page.WaitForAssertion(() => page.Markup.ShouldContain("Revise las fechas"));
        ctx.JSInterop.Invocations.ShouldNotContain(i => i.Identifier == "escuadra.download");
    }
}
