using Bunit;
using Shouldly;
using Abastalia.Client.UI.Pages.Admin;
using Abastalia.Escuadra;

namespace Abastalia.Client.UI.Tests;

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
        page.Find(".esc-hero__value").TextContent.ShouldContain("12,000.00");
    }

    [Fact]
    public void Today_is_compared_with_the_same_hours_of_last_week_and_the_mix_comes_from_the_sales()
    {
        var (ctx, api) = ScreenContext.Create("reports.view");
        using var _ = ctx;
        var today = Format.Today;
        var lastWeek = today.AddDays(-7);
        api.Json("GET", "/api/reports/sales/by-day", new[]
            {
                new { day = lastWeek, saleCount = 20, salesWithItbis = 30_000m, discount = 0m, returns = 0m, itbis = 0m, netWithoutItbis = 27_000m, cost = 0m, profit = 2_000m },
                new { day = today, saleCount = 12, salesWithItbis = 12_000m, discount = 0m, returns = 0m, itbis = 0m, netWithoutItbis = 10_800m, cost = 0m, profit = 2_500m },
            })
           // Last week: 10,000 at midnight (always before now) and 20,000 at 11 p. m., which today has not reached unless it is that hour.
           .Json("GET", "/api/reports/sales/by-hour", new[]
            {
                new { day = lastWeek, hour = 0, saleCount = 8, salesWithItbis = 10_000m },
                new { day = lastWeek, hour = 23, saleCount = 12, salesWithItbis = 20_000m },
                new { day = today, hour = 0, saleCount = 12, salesWithItbis = 12_000m },
            })
           .Json("GET", "/api/reports/sales/by-payment-method", new[]
            {
                new { method = "Cash", saleCount = 10, paid = 9_000m, refunded = 500m, net = 8_500m },
                new { method = "Card", saleCount = 2, paid = 3_000m, refunded = 0m, net = 3_000m },
            });
        var page = ctx.Render<Summary>();

        page.WaitForAssertion(() => page.Find(".esc-hero__value").TextContent.ShouldContain("12,000.00"));
        if (Format.StoreTime(DateTimeOffset.UtcNow).Hour < 23)
        {
            page.Find(".esc-delta--up").TextContent.ShouldContain("+20 %"); // 12,000 vs. 10,000 by this hour, not vs. 30,000
            page.Find(".esc-hero__compare").TextContent.ShouldContain("a esta hora");
        }
        var mix = page.FindAll(".esc-barlist__row").Select(r => r.TextContent).ToList();
        mix.ShouldContain(r => r.Contains("Efectivo") && r.Contains("8,500.00"));
        mix.ShouldContain(r => r.Contains("Tarjeta") && r.Contains("3,000.00"));
        api.Requests.ShouldNotContain(r => r.Route.Contains("/z"));
        page.Find("[aria-label='Ventas por hora']").ShouldNotBeNull();
    }
}
