using Bunit;
using Shouldly;
using Abastalia.Client.UI.Pages.Admin;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-373 (ADR-0016 D): Configuración → Equipos tells the owner to keep the registers off the customers' Wi-Fi.</summary>
public sealed class SharedWifiNoticeTests
{
    [Fact]
    public async Task Equipos_tells_the_owner_to_keep_the_registers_off_the_customers_WiFi()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("devices.manage");
        using var _ = ctx;
        api.Json("GET", "/api/identity/devices", Array.Empty<object>());
        var page = ctx.Render<Settings>();

        page.WaitForAssertion(() => page.Markup.ShouldContain("Unir un equipo nuevo"));
        var notice = page.Find(".esc-notice");
        notice.GetAttribute("role").ShouldBe("status"); // advice, not an alarm: screen readers do not interrupt
        notice.TextContent.ShouldContain("Las cajas, en una red aparte de los clientes");
        notice.TextContent.ShouldContain("red de invitados");
    }
}
