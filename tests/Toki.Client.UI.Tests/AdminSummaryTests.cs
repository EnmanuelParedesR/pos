using Bunit;
using Shouldly;
using Toki.Client.UI.Pages.Admin;
using Toki.Escuadra;

namespace Toki.Client.UI.Tests;

/// <summary>TK-173: the back office's Resumen tells the owner what to review and whether the day is going well.</summary>
public sealed class AdminSummaryTests
{
    [Fact]
    public void A_shortage_goes_first_with_the_person_the_amount_and_a_way_to_see_it()
    {
        var (ctx, api) = ScreenContext.Create("reports.view");
        using var _ = ctx;
        var today = Format.Today;
        var shiftId = Guid.NewGuid();
        api.Json("GET", "/api/reports/sales/by-day", new[]
            {
                new { day = today.AddDays(-7), saleCount = 10, salesWithItbis = 10_000m, discount = 0m, returns = 0m, itbis = 0m, netWithoutItbis = 9_000m, cost = 0m, profit = 2_000m },
                new { day = today, saleCount = 12, salesWithItbis = 12_000m, discount = 0m, returns = 0m, itbis = 0m, netWithoutItbis = 10_800m, cost = 0m, profit = 2_500m },
            })
           .Json("GET", "/api/reports/shifts", new[]
            {
                new { shiftId, register = "Caja 2", cashier = "Yudelka Martínez", openedUtc = DateTime.UtcNow.AddHours(-6), closedUtc = (DateTime?)DateTime.UtcNow.AddHours(-1),
                      zNumber = (long?)14, openingFloat = 1_000m, expectedCash = (decimal?)10_100m, countedCash = (decimal?)9_950m, difference = (decimal?)(-150m) },
            });
        var page = ctx.Render<Summary>();

        page.WaitForAssertion(() => page.Find(".esc-alert--error .esc-alert__title").TextContent.ShouldBe("Faltaron RD$ 150.00 en Caja 2"));
        page.Find(".esc-alert--error .esc-alert__meta").TextContent.ShouldStartWith("Yudelka Martínez · cierre Z #14");
        page.Find(".esc-alert--error a").GetAttribute("href").ShouldBe($"admin/shifts?shift={shiftId}");
        // The hero: today's sales, already past last week's whole day (+20 %).
        page.Find(".esc-hero__value").TextContent.ShouldContain("12,000.00");
        page.Find(".esc-delta--up").TextContent.ShouldContain("+20 %");
    }
}
