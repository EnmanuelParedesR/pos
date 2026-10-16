using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Shouldly;
using Abastalia.Client.UI.Components;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-121: a customer pays on their credit at the register (F9 → Abono de cliente).</summary>
public sealed class RegisterCustomerPaymentTests
{
    private static readonly Guid Ana = Guid.NewGuid();

    private static FakeApi Customer(FakeApi api, decimal debt) => api
        .Json("GET", "/api/customers", new[] { new { id = Ana, name = "Ana Pérez", taxId = (string?)null, phone = "809-555-0102", priceLevel = "Retail", creditLimit = 5_000m, isActive = true } })
        .Json("GET", $"/api/customers/{Ana}", new { id = Ana, name = "Ana Pérez", taxId = (string?)null, fiscalCredit = false, priceLevel = "Retail", creditLimit = 5_000m, debt, creditBalance = 0m, isActive = true });

    [Fact]
    public void From_the_F9_menu_the_cashier_finds_the_customer_sees_the_debt_and_records_a_cash_payment()
    {
        var (ctx, api) = ScreenContext.Create("register.open", "register.close", "register.cash-in", "credit.collect", "customers.manage", "sales.create");
        using var _ = ctx;
        Customer(api, 1_250.40m).Json("POST", $"/api/customers/{Ana}/payments", new { customerPaymentId = Guid.NewGuid(), applied = 1_000m, remainingDebt = 250.40m });
        var menu = ctx.Render<RegisterMenu>();

        menu.FindAll("button").Single(b => b.TextContent.Contains("Abono de cliente")).Click();
        menu.Find("input[aria-label='Buscar cliente']").Input("Ana");
        menu.Find("form").Submit();
        menu.WaitForAssertion(() => menu.Find("tbody tr").Click());

        menu.WaitForAssertion(() => menu.Find(".checkout__left").TextContent.ShouldContain("1,250.40"));
        // Cash suggests all of it in whole pesos; the cashier types 1000 on the keypad instead.
        menu.Find("input[data-no-scan]").GetAttribute("value").ShouldBe("1251");
        foreach (var k in new[] { "1", "0", "0", "0" }) menu.FindAll(".esc-keypad button").Single(b => b.TextContent.Trim() == k).Click();
        menu.Find(".checkout__left").TextContent.ShouldContain("250.40"); // what will remain
        menu.Find("[role=dialog]").KeyDown(new KeyboardEventArgs { Key = "F12" });

        menu.WaitForAssertion(() => menu.Find(".receipt").TextContent.ShouldContain("QUEDA DEBIENDO250.40"));
        var body = api.Requests.Single(r => r.Method == "POST").Body;
        body.ShouldContain($"\"shiftId\":\"{ScreenContext.Shift.ShiftId}\"");
        body.ShouldContain("\"amount\":1000");
        body.ShouldContain("\"method\":\"Cash\"");
    }

    [Fact]
    public void Centavos_in_cash_or_more_than_the_debt_cannot_be_confirmed_and_an_unanswered_payment_is_retried_with_the_same_id()
    {
        var (ctx, api) = ScreenContext.Create("register.open", "register.close", "register.cash-in", "credit.collect", "customers.manage");
        using var _ = ctx;
        Customer(api, 300m).Respond("POST", $"/api/customers/{Ana}/payments", _ => throw new TaskCanceledException());
        var dialog = ctx.Render<CustomerPaymentDialog>();
        dialog.Find("input[aria-label='Buscar cliente']").Input("Ana");
        dialog.Find("form").Submit();
        dialog.WaitForAssertion(() => dialog.Find("tbody tr").Click());
        var confirm = () => dialog.Find(".esc-button--money");

        dialog.WaitForAssertion(() => dialog.Find("input[data-no-scan]").Input("150.50"));
        dialog.Find(".esc-error-help").TextContent.ShouldContain("pesos enteros");
        confirm().HasAttribute("disabled").ShouldBeTrue();
        dialog.Find("input[data-no-scan]").Input("301");
        dialog.Find(".esc-error-help").TextContent.ShouldContain("más de lo que debe");

        // A card payment may carry centavos.
        dialog.Find("[role=dialog]").KeyDown(new KeyboardEventArgs { Key = "F6" });
        dialog.Find("input[data-no-scan]").Input("150.50");
        confirm().Click();
        dialog.WaitForAssertion(() => dialog.Find("[role=dialog]").TextContent.ShouldContain("No se sabe si el abono se guardó"));
        dialog.FindAll(".esc-dialog__footer button")[0].HasAttribute("disabled").ShouldBeTrue(); // no other customer until it is known

        api.Json("POST", $"/api/customers/{Ana}/payments", new { customerPaymentId = Guid.NewGuid(), applied = 150.50m, remainingDebt = 149.50m });
        confirm().Click();
        dialog.WaitForAssertion(() => dialog.Find(".receipt").ShouldNotBeNull());
        var sent = api.Requests.Where(r => r.Method == "POST").Select(r => r.Body[..60]).ToList();
        sent.Count.ShouldBe(2);
        sent[1].ShouldBe(sent[0]);
        api.Requests.Last(r => r.Method == "POST").Body.ShouldContain("\"method\":\"Card\"");
    }

    [Fact]
    public void A_customer_who_owes_nothing_has_nothing_to_pay()
    {
        var (ctx, api) = ScreenContext.Create("register.open", "register.close", "register.cash-in", "credit.collect", "customers.manage");
        using var _ = ctx;
        Customer(api, 0m);
        var dialog = ctx.Render<CustomerPaymentDialog>();
        dialog.Find("input[aria-label='Buscar cliente']").Input("Ana");
        dialog.Find("form").Submit();
        dialog.WaitForAssertion(() => dialog.Find("tbody tr").Click());

        dialog.WaitForAssertion(() => dialog.Markup.ShouldContain("Este cliente no debe nada."));
        dialog.FindAll(".esc-button--money").ShouldBeEmpty();
    }
}
