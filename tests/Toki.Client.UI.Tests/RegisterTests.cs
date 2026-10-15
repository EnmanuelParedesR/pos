using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Client.UI.Api;
using Toki.Hardware;
using Toki.Client.UI.Components;
using Toki.Client.UI.Pages;
using Toki.Client.UI.State;

namespace Toki.Client.UI.Tests;

public sealed class RegisterTests
{
    internal static readonly ProductByCode Salami = new(Guid.NewGuid(), null, "Salami 1 lb", "Unit", "General18", 210m, null, 1m, null, null, [], 24m);

    internal static Receipt ReceiptFor(decimal total) => new("Carnicería", null, null, null, "Factura de Consumo Electrónica", "E320000000001", 1,
        DateTime.UtcNow, "Caja 1", "Yudelka M.", null, null, [], total, 0, [], total, [], 0, "abc123", "", null, false);

    [Fact]
    public void F7_opens_the_line_discount_not_the_quantity()
    {
        var (ctx, api) = ScreenContext.Create("sales.create", "register.operate");
        using var _ = ctx;
        api.Json("GET", "/api/inventory/categories", Array.Empty<object>())
           .Json("GET", $"/api/inventory/products/by-code/7460000000017", Salami);
        var sale = ctx.Render<Sale>();

        sale.Find("input.sale__scanner").Input("7460000000017");
        sale.Find("form").Submit();
        sale.WaitForAssertion(() => sale.Find(".sale__line").TextContent.ShouldContain("Salami 1 lb"));

        sale.Find(".sale").KeyDown(new KeyboardEventArgs { Key = "F7" });
        sale.Find("[role=dialog] h2").TextContent.ShouldBe("Descuento");
        sale.Find("[role=dialog] input").Input("20");
        sale.Find("[role=dialog] .esc-button--primary").Click();

        ctx.Services.GetRequiredService<Cart>().Total.ShouldBe(168m);
    }

    [Fact]
    public void When_the_server_corrects_the_total_it_is_shown_and_confirmed_again()
    {
        var (ctx, api) = ScreenContext.Create("sales.create");
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(Salami, null);
        api.Respond("POST", "/api/sales", _ => FakeApi.Problem(HttpStatusCode.Conflict, "Sales.TotalChanged", "El total cambió.", new { total = new[] { "209.99" } }));
        SaleRegistered? sold = null;
        var checkout = ctx.Render<CheckoutDialog>(p => p.Add(x => x.ShiftId, ScreenContext.Shift.ShiftId).Add(x => x.OnSold, v => sold = v));

        checkout.Find(".esc-button--money").Click();
        checkout.WaitForAssertion(() => checkout.Find(".esc-notice").TextContent.ShouldContain("RD$ 209.99"));
        sold.ShouldBeNull();

        api.Json("POST", "/api/sales", new SaleRegistered(cart.SaleId, 1, "E320000000001", 209.99m, 0m, ReceiptFor(209.99m)), HttpStatusCode.Created);
        checkout.Find(".esc-button--money").Click();
        checkout.WaitForAssertion(() => sold.ShouldNotBeNull());
        // Same idempotency key both times, and the confirmed total is the server's.
        var sent = api.Requests.Where(p => p.Route == "/api/sales").ToList();
        sent.Count.ShouldBe(2);
        sent[1].Body.ShouldContain("\"expectedTotal\":209.99");
        sent[1].Body.ShouldContain(cart.SaleId.ToString());
    }

    [Fact]
    public void A_large_discount_asks_for_a_supervisor_and_retries_with_the_approval()
    {
        var (ctx, api) = ScreenContext.Create("sales.create");
        using var _ = ctx;
        var cart = ctx.Services.GetRequiredService<Cart>();
        cart.Add(Salami, null);
        var approval = Guid.NewGuid();
        api.Respond("POST", "/api/sales", body => body.Contains(approval.ToString(), StringComparison.Ordinal)
                ? FakeApi.Response(HttpStatusCode.Created, new SaleRegistered(cart.SaleId, 1, "E320000000001", 210m, 0m, ReceiptFor(210m)))
                : FakeApi.Problem(HttpStatusCode.Forbidden, "Approval.Required", "Esta acción necesita la autorización de un supervisor.",
                    new { permission = new[] { "sales.large-discount" } }))
           .Json("GET", "/api/identity/users/active", new[] { new ActiveUser(Guid.NewGuid(), "Ramón D.", "ramon") })
           .Json("POST", "/api/identity/approvals", new ApprovalGranted(approval, "Ramón D.", DateTime.UtcNow.AddMinutes(2)));
        SaleRegistered? sold = null;
        var screen = ctx.Render<Fragment>(p => p.Add(x => x.Content, b =>
        {
            b.OpenComponent<CheckoutDialog>(0);
            b.AddAttribute(1, nameof(CheckoutDialog.ShiftId), ScreenContext.Shift.ShiftId);
            b.AddAttribute(2, nameof(CheckoutDialog.OnSold), Microsoft.AspNetCore.Components.EventCallback.Factory.Create<SaleRegistered>(this, v => sold = v));
            b.CloseComponent();
            b.OpenComponent<ApprovalDialog>(3);
            b.CloseComponent();
        }));

        screen.FindAll(".esc-button--money")[0].Click();
        screen.WaitForAssertion(() => screen.Find("h2").TextContent.ShouldBe("Cobrar"));
        screen.WaitForAssertion(() => screen.FindAll("h2").Select(h => h.TextContent).ShouldContain("Autoriza un supervisor"));
        screen.WaitForAssertion(() => screen.Find("[aria-label=Supervisor] .esc-chip").TextContent.ShouldBe("Ramón D."));
        var dialog = screen.FindAll(".esc-overlay")[^1];
        foreach (var d in "2468") dialog.KeyDown(new KeyboardEventArgs { Key = d.ToString() });
        dialog.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        screen.WaitForAssertion(() => sold.ShouldNotBeNull());
        api.Requests.Single(p => p.Route == "/api/identity/approvals").Body.ShouldContain("sales.large-discount");
        api.Requests.Last(p => p.Route == "/api/sales").Body.ShouldContain($"\"supervisorApprovals\":[\"{approval}\"]");
    }

    [Fact]
    public void A_wrong_pin_is_explained_without_leaving_the_screen()
    {
        var (ctx, api) = ScreenContext.Create();
        using var _ = ctx;
        ctx.Services.GetRequiredService<Session>().Close();
        api.Json("GET", "/api/identity/users/active", new[] { new ActiveUser(Guid.NewGuid(), "Yudelka M.", "yudelka") })
           .Respond("POST", "/api/identity/sessions", _ => FakeApi.Problem(HttpStatusCode.Unauthorized, "Identity.InvalidCredentials", "Credenciales inválidas."));
        var signIn = ctx.Render<SignIn>();

        signIn.WaitForAssertion(() => signIn.Find(".esc-tile").Click());
        foreach (var d in "1111") signIn.Find("section[aria-label=PIN]").KeyDown(new KeyboardEventArgs { Key = d.ToString() });
        signIn.Find("section[aria-label=PIN]").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        signIn.WaitForAssertion(() => signIn.Find(".esc-notice").TextContent.ShouldContain("PIN incorrecto."));
        ctx.Services.GetRequiredService<Session>().IsOpen.ShouldBeFalse();
    }
}

/// <summary>Renders several components side by side, as the layout does with a screen and the supervisor dialog.</summary>
public sealed class Fragment : Microsoft.AspNetCore.Components.ComponentBase
{
    [Microsoft.AspNetCore.Components.Parameter] public Microsoft.AspNetCore.Components.RenderFragment? Content { get; set; }
    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, Content);
}
