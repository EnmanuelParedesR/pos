using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Client.UI.Pages.Admin;
using Abastalia.Client.UI.State;

namespace Abastalia.Client.UI.Tests;

/// <summary>TK-186: customers and their credit in the back office, roles and permissions; TK-175: renaming a register.</summary>
public sealed class AdminCustomersTests
{
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid PaymentId = Guid.NewGuid();
    private static readonly Guid OpenShift = Guid.NewGuid();

    private static FakeApi Customers(FakeApi api) => api
        .Json("GET", "/api/customers", new[]
        {
            new { id = Ana, name = "Ana Pérez", taxId = "00112345678", phone = "809-555-0102", priceLevel = "Retail", creditLimit = 5_000m, isActive = true, address = "Calle 1", fiscalCredit = false },
        })
        .Json("GET", "/api/reports/receivables", new[]
        {
            new { customerId = Ana, customer = "Ana Pérez", days0To30 = 1_000m, days31To60 = 200m, days61To90 = 0m, over90 = 300m, total = 1_500.50m, creditLimit = 5_000m },
        })
        .Json("GET", $"/api/customers/{Ana}/statement", new
        {
            customerId = Ana, name = "Ana Pérez", debt = 1_500.50m, creditBalance = 0m, creditLimit = 5_000m,
            lines = new object[]
            {
                new { dateUtc = DateTime.UtcNow.AddDays(-40), type = "Venta a crédito", document = "E320000000010", charge = 2_000.50m, payment = 0m, debt = 2_000.50m, paymentId = (Guid?)null },
                new { dateUtc = DateTime.UtcNow.AddHours(-1), type = "Abono", document = (string?)null, charge = 0m, payment = 500m, debt = 1_500.50m, paymentId = (Guid?)PaymentId },
            },
        })
        .Json("GET", "/api/cash/registers", new[] { new { id = Guid.NewGuid(), name = "Caja 2", isActive = true, openShiftId = (Guid?)OpenShift, cashier = "Yudelka" } });

    [Fact]
    public async Task The_list_shows_what_each_customer_owes_and_the_statement_its_running_balance()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("customers.manage", "credit.manage", "reports.view", "register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        Customers(api);
        var page = ctx.Render<Customers>();

        page.WaitForAssertion(() => page.Find("tbody tr").TextContent.ShouldContain("1,500.50"));
        page.Find(".esc-stats").TextContent.ShouldContain("RD$ 500.00"); // more than 30 days
        page.Find("tbody tr").Click();

        page.WaitForAssertion(() => page.Find("[role=dialog] h2").TextContent.ShouldBe("Ana Pérez"));
        var rows = page.FindAll("[role=dialog] tbody tr");
        rows[0].TextContent.ShouldContain("Abono"); // newest first
        rows[1].TextContent.ShouldContain("2,000.50");
        page.Find("[role=dialog]").TextContent.ShouldContain("Más de 90 días");
    }

    [Fact]
    public async Task A_payment_goes_to_the_open_register_with_one_id_even_when_retried()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("customers.manage", "credit.manage", "reports.view", "register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        Customers(api).Respond("POST", $"/api/customers/{Ana}/payments", _ => throw new TaskCanceledException());
        var page = ctx.Render<Customers>();
        page.WaitForAssertion(() => page.Find("tbody tr").Click());
        page.WaitForAssertion(() => page.FindAll("[role=dialog] button").Single(b => b.TextContent.Contains("Registrar abono")).Click());

        page.WaitForAssertion(() => page.Find("[role=dialog] select").TextContent.ShouldContain("Caja 2 · Yudelka"));
        page.Find("[role=dialog] input.esc-num").Change("500.50");
        page.FindAll("[role=dialog] .esc-dialog__footer button")[^1].Click();
        page.Find("[role=dialog]").TextContent.ShouldContain("pesos enteros"); // cash, no centavos

        page.Find("[role=dialog] input.esc-num").Change("500");
        // R12: a transfer needs its number (the store's default), as at the register.
        page.FindAll("[role=dialog] .esc-segmented button").Single(b => b.TextContent.Contains("Transferencia")).Click();
        page.Find("[role=dialog] input.esc-num").Change("500");
        page.FindAll("[role=dialog] .esc-dialog__footer button")[^1].Click();
        page.Find("[role=dialog]").TextContent.ShouldContain("Escriba el número de autorización o de la transferencia.");
        api.Requests.ShouldNotContain(r => r.Method == "POST");
        page.FindAll("[role=dialog] .esc-segmented button").Single(b => b.TextContent.Contains("Efectivo")).Click();
        page.Find("[role=dialog] input.esc-num").Change("500");
        page.FindAll("[role=dialog] .esc-dialog__footer button")[^1].Click();
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain("No se sabe si se registró"));

        api.Json("POST", $"/api/customers/{Ana}/payments", new { customerPaymentId = Guid.NewGuid(), applied = 500m, remainingDebt = 1_000.50m });
        page.FindAll("[role=dialog] .esc-dialog__footer button")[^1].Click();
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain("Queda debiendo RD$ 1,000.50"));

        var sent = api.Requests.Where(r => r.Method == "POST").Select(r => r.Body).ToList();
        sent.Count.ShouldBe(2);
        sent[0].ShouldContain($"\"shiftId\":\"{OpenShift}\"");
        sent[0].ShouldContain("\"method\":\"Cash\"");
        sent[1].Split("\"shiftId\"")[0].ShouldBe(sent[0].Split("\"shiftId\"")[0]); // same customerPaymentId
    }

    [Fact]
    public async Task Voiding_a_payment_asks_a_supervisor_when_the_user_cannot()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("customers.manage", "reports.view", "register.open", "register.close", "register.cash-in", "credit.collect");
        using var _ = ctx;
        var approval = Guid.NewGuid();
        Customers(api).Respond("POST", $"/api/customers/payments/{PaymentId}/void", body => body.Contains(approval.ToString())
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : FakeApi.Problem(HttpStatusCode.Forbidden, "Approval.Required", "Hace falta un supervisor.", new { permission = new[] { "credit.manage" } }));
        var approver = ctx.Services.GetRequiredService<Approver>();
        var page = ctx.Render<Customers>();
        page.WaitForAssertion(() => page.Find("tbody tr").Click());
        page.WaitForAssertion(() => page.FindAll("[role=dialog] button").Single(b => b.TextContent == "Anular").Click());

        page.Find("[role=dialog] form .esc-button--danger").Click();
        page.Find("[role=dialog]").TextContent.ShouldContain("Escriba por qué se anula");
        page.Find("[role=dialog] form input").Change("Monto equivocado");
        page.Find("[role=dialog] form .esc-button--danger").Click();

        page.WaitForAssertion(() => approver.Current.ShouldNotBeNull().Permission.ShouldBe("credit.manage"));
        approver.Respond(approval);
        page.WaitForAssertion(() => page.Find("[role=dialog]").TextContent.ShouldContain("Abono de RD$ 500.00 anulado"));
        api.Requests.Last(r => r.Route.EndsWith("/void")).Body.ShouldContain("\"reason\":\"Monto equivocado\"");
    }

    [Fact]
    public async Task Without_the_credit_permission_the_limit_and_price_level_cannot_change_and_the_rest_is_kept()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("customers.manage", "reports.view");
        using var _ = ctx;
        Customers(api).Respond("PUT", $"/api/customers/{Ana}", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = ctx.Render<Customers>();
        page.WaitForAssertion(() => page.Find("tbody tr").Click());
        page.WaitForAssertion(() => page.FindAll("[role=dialog] button").Single(b => b.TextContent == "Editar datos").Click());

        page.Find("input[inputmode=decimal]").HasAttribute("disabled").ShouldBeTrue();
        page.Find("[role=dialog] select").HasAttribute("disabled").ShouldBeTrue();
        page.Find("input[maxlength='30']").Change("809-555-9999");
        page.FindAll("[role=dialog] .esc-dialog__footer button")[^1].Click();

        page.WaitForAssertion(() => api.Requests.ShouldContain(r => r.Method == "PUT"));
        var body = api.Requests.Single(r => r.Method == "PUT").Body;
        body.ShouldContain("\"phone\":\"809-555-9999\"");
        body.ShouldContain("\"address\":\"Calle 1\""); // not lost
        body.ShouldContain("\"creditLimit\":5000");
    }

    [Fact]
    public async Task A_role_is_saved_with_the_checked_permissions_and_Administrador_is_not_editable()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("users.manage");
        using var _ = ctx;
        var cashier = Guid.NewGuid();
        api.Json("GET", "/api/identity/users", Array.Empty<object>())
           .Json("GET", "/api/identity/roles", new
           {
               roles = new[]
               {
                   new { id = new Guid("0199e000-0000-7000-8000-000000000001"), name = "Administrador", permissions = new[] { "sales.create", "users.manage" } },
                   new { id = cashier, name = "Cajero", permissions = new[] { "sales.create" } },
               },
               availablePermissions = new[] { "sales.create", "sales.void", "users.manage", "brand.new" },
           })
           .Respond("PUT", $"/api/identity/roles/{cashier}", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = ctx.Render<Settings>();
        page.WaitForAssertion(() => page.FindAll("[role=tab]").Single(t => t.TextContent == "Roles y permisos").Click());

        page.WaitForAssertion(() => page.FindAll("tbody tr")[0].TextContent.ShouldContain("No se edita"));
        page.FindAll("tbody tr")[0].QuerySelector("button").ShouldBeNull();
        page.Find("button[aria-label='Editar Cajero']").Click();

        var anular = page.FindAll("label.esc-check").Single(l => l.TextContent.Contains("Anular ventas"));
        anular.QuerySelector("input")!.Change(true);
        page.FindAll("label.esc-check").ShouldContain(l => l.TextContent.Contains("brand.new")); // unknown permissions still show
        page.FindAll("[role=dialog] .esc-dialog__footer button")[^1].Click();

        page.WaitForAssertion(() => api.Requests.Single(r => r.Method == "PUT").Body.ShouldBe("{\"name\":\"Cajero\",\"permissions\":[\"sales.create\",\"sales.void\"]}"));
    }

    [Fact]
    public async Task A_register_is_renamed_from_Cajas()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("registers.manage");
        using var _ = ctx;
        var register = Guid.NewGuid();
        api.Json("GET", "/api/settings/business", new { name = "Mi tienda", discountCapPercent = 10m })
           .Json("GET", "/api/settings/features", new { status = "Active", plan = "Negocio", expiresUtc = (DateTime?)null, enabled = Array.Empty<string>(), registers = 3 })
           .Json("GET", "/api/cash/registers", new[] { new { id = register, name = "Caja 1", isActive = true, openShiftId = (Guid?)null, cashier = (string?)null } })
           .Respond("PUT", $"/api/cash/registers/{register}", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = ctx.Render<Settings>();
        page.WaitForAssertion(() => page.FindAll("[role=tab]").Single(t => t.TextContent == "Cajas").Click());

        page.WaitForAssertion(() => page.Find("button[aria-label='Cambiar el nombre de Caja 1']").Click());
        page.Find("tbody input").Change("Caja del frente");
        page.Find("tbody form").Submit();

        page.WaitForAssertion(() => page.Markup.ShouldContain("Caja 1 ahora se llama Caja del frente."));
        api.Requests.Single(r => r.Method == "PUT").Body.ShouldBe("{\"name\":\"Caja del frente\"}");
    }

    [Fact]
    public async Task A_registers_standard_float_is_set_from_Cajas()
    {
        var (ctx, api) = await ScreenContext.CreateAsync("registers.manage");
        using var _ = ctx;
        var register = Guid.NewGuid();
        api.Json("GET", "/api/settings/business", new { name = "Mi tienda", discountCapPercent = 10m })
           .Json("GET", "/api/settings/features", new { status = "Active", plan = "Negocio", expiresUtc = (DateTime?)null, enabled = Array.Empty<string>(), registers = 3 })
           .Json("GET", "/api/cash/registers", new[] { new { id = register, name = "Caja 1", isActive = true, openShiftId = (Guid?)null, cashier = (string?)null, standardFloat = (decimal?)null } })
           .Respond("PUT", $"/api/cash/registers/{register}/standard-float", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var page = ctx.Render<Settings>();
        page.WaitForAssertion(() => page.FindAll("[role=tab]").Single(t => t.TextContent == "Cajas").Click());
        page.WaitForAssertion(() => page.Markup.ShouldContain("Sin fijar"));

        page.Find("button[aria-label='Fondo estándar de Caja 1']").Click();
        page.Find("tbody input").Change("2000.50");
        page.Find("tbody form").Submit();
        page.Markup.ShouldContain("Escriba el monto en pesos enteros.");
        page.Find("tbody input").Change("2000");
        page.Find("tbody form").Submit();

        page.WaitForAssertion(() => page.Markup.ShouldContain("Caja 1 abre con RD$ 2,000"));
        api.Requests.Single(r => r.Method == "PUT").Body.ShouldBe("{\"amount\":2000}");
    }
}
