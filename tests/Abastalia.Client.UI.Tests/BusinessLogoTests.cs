using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Client.UI.Components;
using Abastalia.Client.UI.Pages.Admin;
using Abastalia.Client.UI.State;
using Abastalia.Hardware;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-280: the business logo in Configuración → Negocio, on the receipt on screen and on what goes to the printer.</summary>
public sealed class BusinessLogoTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly object Raster = new { width = 16, height = 2, bits = new byte[] { 0xFF, 0x00, 0x0F, 0xF0 } };

    private static object Business(string? logo) => new { name = "Colmado El Sol", discountCapPercent = 10m, requirePaymentReference = true, logoVersion = logo };

    [Fact]
    public async Task The_owner_uploads_the_logo_sees_how_it_prints_and_removes_it()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("settings.manage");
        using var _ = ctx;
        api.Json("GET", "/api/settings/business", Business(null))
           .Json("GET", "/api/settings/approvals", Array.Empty<object>())
           .Respond("PUT", "/api/settings/business/logo", _ =>
           {
               api.Json("GET", "/api/settings/business", Business("1"))
                  .Json("GET", "/api/settings/business/logo", new { png = Png, version = "1" })
                  .Json("GET", "/api/settings/business/logo/receipt", Raster);
               return new HttpResponseMessage(HttpStatusCode.NoContent);
           })
           .Respond("DELETE", "/api/settings/business/logo", _ =>
           {
               api.Json("GET", "/api/settings/business", Business(null));
               return new HttpResponseMessage(HttpStatusCode.NoContent);
           });
        var page = ctx.Render<Settings>();
        page.WaitForAssertion(() => page.Markup.ShouldContain("Todavía no hay logo."));

        page.FindComponents<Microsoft.AspNetCore.Components.Forms.InputFile>().Single(f => f.Instance.AdditionalAttributes?["accept"] is string a && a.Contains(".png"))
            .UploadFiles(InputFileContent.CreateFromBinary(Png, "logo.png", contentType: "image/png"));

        page.WaitForAssertion(() => page.Markup.ShouldContain("Logo guardado."));
        api.Requests.Single(r => r.Method == "PUT" && r.Route == "/api/settings/business/logo").Body.ShouldContain($"\"file\":\"{Convert.ToBase64String(Png)}\"");
        page.Find("img[alt='Logo de Colmado El Sol']").GetAttribute("src").ShouldBe("data:image/png;base64," + Convert.ToBase64String(Png));
        page.Find("img[alt='El logo como sale en la impresora']").GetAttribute("src")!.ShouldStartWith("data:image/bmp;base64,");

        page.FindAll("button").Single(b => b.TextContent.Contains("Quitar el logo", StringComparison.Ordinal)).Click();
        api.Requests.ShouldNotContain(r => r.Method == "DELETE"); // a second tap confirms
        page.FindAll("button").Single(b => b.TextContent.Contains("¿Quitar el logo? Toque otra vez", StringComparison.Ordinal)).Click();
        page.WaitForAssertion(() => page.Markup.ShouldContain("Logo quitado."));
        page.Markup.ShouldContain("Todavía no hay logo.");
    }

    [Fact]
    public async Task A_file_the_server_refuses_is_explained_on_the_field()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("settings.manage");
        using var _ = ctx;
        api.Json("GET", "/api/settings/business", Business(null))
           .Json("GET", "/api/settings/approvals", Array.Empty<object>())
           .Respond("PUT", "/api/settings/business/logo", _ => FakeApi.Problem(HttpStatusCode.BadRequest, "Settings.LogoType", "El logo debe ser una imagen PNG o JPG."));
        var page = ctx.Render<Settings>();
        page.WaitForAssertion(() => page.Markup.ShouldContain("Todavía no hay logo."));

        page.FindComponents<Microsoft.AspNetCore.Components.Forms.InputFile>().Single(f => f.Instance.AdditionalAttributes?["accept"] is string a && a.Contains(".png"))
            .UploadFiles(InputFileContent.CreateFromText("GIF89a", "logo.png", contentType: "image/png"));

        page.WaitForAssertion(() => page.Markup.ShouldContain("El logo debe ser una imagen PNG o JPG."));
    }

    [Fact]
    public async Task The_receipt_shows_the_logo_as_printed_and_the_printer_gets_it_fetched_once()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        await ctx.Services.GetRequiredService<Peripherals>().SaveAsync(new(Printer: new("EPSON TM-T20", Width: 58)));
        api.Json("GET", "/api/settings/business/logo/receipt", Raster);
        var receipt = RegisterTests.ReceiptFor(210m) with { LogoVersion = "7" };
        var dialog = ctx.Render<ReceiptDialog>(p => p.Add(x => x.Receipt, receipt));

        dialog.WaitForAssertion(() => dialog.Find("img.receipt__logo").GetAttribute("src")!.ShouldStartWith("data:image/bmp;base64,"));
        dialog.FindAll("button").Single(b => b.TextContent.Contains("Imprimir", StringComparison.Ordinal)).Click();

        var printer = ctx.Services.GetRequiredService<TestDrivers>().Printer;
        dialog.WaitForAssertion(() => printer.Printed.ShouldHaveSingleItem().Receipt.Logo.ShouldNotBeNull().Bits.ShouldBe(new byte[] { 0xFF, 0x00, 0x0F, 0xF0 }));
        api.Requests.Where(r => r.Route.StartsWith("/api/settings/business/logo/receipt", StringComparison.Ordinal)).ShouldHaveSingleItem().Route.ShouldEndWith("?width=58");
    }

    [Fact]
    public async Task Without_a_logo_or_when_the_server_does_not_answer_the_receipt_prints_as_always()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        await ctx.Services.GetRequiredService<Peripherals>().SaveAsync(new(Printer: new("EPSON TM-T20")));
        api.Respond("GET", "/api/settings/business/logo/receipt", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var logo = ctx.Services.GetRequiredService<BusinessLogo>();
        var plain = RegisterTests.ReceiptFor(210m);

        (await logo.ForPrinterAsync(plain, new(Printer: new("EPSON TM-T20")))).ShouldBe(plain);
        api.Requests.ShouldNotContain(r => r.Route.Contains("/logo", StringComparison.Ordinal));
        (await logo.ForPrinterAsync(plain with { LogoVersion = "7" }, new(Printer: new("EPSON TM-T20")))).Logo.ShouldBeNull();

        // A printer that does not take images: this PC prints without the logo and does not even ask for it.
        api.Json("GET", "/api/settings/business/logo/receipt", Raster);
        var asked = api.Requests.Count;
        (await logo.ForPrinterAsync(plain with { LogoVersion = "8" }, new(Printer: new("EPSON TM-T20"), PrintLogo: false))).Logo.ShouldBeNull();
        api.Requests.Count.ShouldBe(asked);
        (await logo.ForPrinterAsync(plain with { LogoVersion = "8" }, new(Printer: new("EPSON TM-T20")))).Logo.ShouldNotBeNull();
    }

    [Fact]
    public async Task R12_a_new_logo_version_that_cannot_be_fetched_shows_no_logo_not_the_old_one()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("sales.create");
        using var _ = ctx;
        api.Json("GET", "/api/settings/business/logo", new { png = new byte[] { 1, 2, 3 }, version = "1" });
        var logo = ctx.Services.GetRequiredService<BusinessLogo>();
        (await logo.DataUrlAsync("1")).ShouldBe("data:image/png;base64,AQID");

        api.Respond("GET", "/api/settings/business/logo", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        (await logo.DataUrlAsync("2")).ShouldBeNull();
        (await logo.DataUrlAsync("1")).ShouldBe("data:image/png;base64,AQID"); // the kept one, for its own version
    }

    [Fact]
    public async Task TK288_the_Z_downloaded_from_Turnos_carries_the_business_logo()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("reports.view");
        using var _ = ctx;
        var shift = Guid.NewGuid();
        api.Json("GET", "/api/reports/shifts", new[] { new { shiftId = shift, register = "Caja 1", cashier = "Ana", openedUtc = DateTime.UtcNow.AddHours(-8), closedUtc = (DateTime?)DateTime.UtcNow, zNumber = (long?)7, openingFloat = 0m, expectedCash = (decimal?)0m, countedCash = (decimal?)0m, difference = (decimal?)0m } })
           .Json("GET", $"/api/cash/shifts/{shift}/z", new
            {
                shiftId = shift, zNumber = 7, register = "Caja 1", cashier = "Ana", openedUtc = DateTime.UtcNow.AddHours(-8), closedUtc = DateTime.UtcNow, openingFloat = 0m,
                byMethod = Array.Empty<object>(), saleCount = 0, voidedCount = 0, customerPayments = 0m, rounding = 0m, cashIn = 0m, withdrawals = 0m, expenses = 0m,
                expectedCash = 0m, countedCash = 0m, difference = 0m,
            })
           .Json("GET", "/api/settings/business", Business("3"))
           .Json("GET", "/api/settings/business/logo", new { png = Png, version = "3" });
        var page = ctx.Render<Shifts>();

        page.WaitForAssertion(() => page.Find("tbody tr.selectable").Click());
        page.WaitForAssertion(() => page.FindAll("button").Single(b => b.TextContent.Contains("Descargar para PDF", StringComparison.Ordinal)).Click());

        page.WaitForAssertion(() => page.Markup.ShouldContain("Listo: cierre-z-7.html"));
        var html = System.Text.Encoding.UTF8.GetString((byte[])ctx.JSInterop.Invocations.Single(i => i.Identifier == "escuadra.download").Arguments[2]!);
        html.ShouldContain($"<img class=\"logo\" src=\"data:image/png;base64,{Convert.ToBase64String(Png)}\"");
        html.ShouldNotContain("aria-label=\"Abastalia\"");
    }

    [Fact]
    public void TK288_the_statement_shows_the_business_logo_or_the_Abastalia_tag_without_one()
    {
        var statement = new Abastalia.Client.UI.Api.CustomerStatement(Guid.NewGuid(), "Juan Pérez", 500m, 0m, 1000m, []);
        var business = new Abastalia.Client.UI.Api.BusinessProfile("Colmado El Sol", null, null, null, null, 10m, LogoVersion: "3");
        var url = "data:image/png;base64," + Convert.ToBase64String(Png);

        var with = System.Text.Encoding.UTF8.GetString(Printables.Statement(statement, null, business, DateTime.UtcNow, url).Content);
        with.ShouldContain($"<div class=\"business\"><img class=\"logo\" src=\"{url}\" alt=\"\"><h1>Colmado El Sol</h1>");
        with.ShouldNotContain("aria-label=\"Abastalia\"");

        var without = System.Text.Encoding.UTF8.GetString(Printables.Statement(statement, null, business with { LogoVersion = null }, DateTime.UtcNow).Content);
        without.ShouldContain("aria-label=\"Abastalia\"");
        without.ShouldNotContain("<img");
    }

    [Fact]
    public void The_bmp_preview_is_the_raster_bottom_up_with_white_paper_and_black_ink()
    {
        var url = BusinessLogo.BmpDataUrl(new ReceiptLogo(16, 2, [0xFF, 0x00, 0x0F, 0xF0]));
        var bmp = Convert.FromBase64String(url["data:image/bmp;base64,".Length..]);
        (bmp[0], bmp[1], BitConverter.ToInt32(bmp, 2), BitConverter.ToInt32(bmp, 18), BitConverter.ToInt32(bmp, 22), bmp[28]).ShouldBe(((byte)'B', (byte)'M', bmp.Length, 16, 2, (byte)1));
        bmp[54..62].ShouldBe(new byte[] { 255, 255, 255, 0, 0, 0, 0, 0 }); // 0 = paper, 1 = ink
        bmp[62..66].ShouldBe(new byte[] { 0x0F, 0xF0, 0, 0 }); // the last row first, padded to 4 bytes
        bmp[66..70].ShouldBe(new byte[] { 0xFF, 0x00, 0, 0 });
    }
}
