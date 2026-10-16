using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Store;

namespace Toki.IntegrationTests.Platform;

/// <summary>R11 part A (TK-253): what a cashier or a supervisor could see or do at the store, each finding with the attack it closes.
/// Own app: some tests change the approval policy and the business settings.</summary>
public sealed class SecurityReviewTests(TokiApp app) : IClassFixture<TokiApp>
{
    private async Task<HttpClient> Admin() => app.Client(await app.AdminAsync());

    private async Task<Guid> ProductAsync(decimal price = 100m, decimal cost = 50m)
    {
        var admin = await Admin();
        return await admin.ProductAsync(await admin.CategoryAsync(), "Producto " + Guid.NewGuid().ToString()[..6], price, rate: "Exempt", cost: cost, stock: 500);
    }

    private async Task<HttpClient> WithAsync(params string[] permissions)
    {
        var role = await (await Admin()).PostAsJsonAsync("/api/identity/roles", new { Name = "Rol " + Guid.NewGuid().ToString()[..6], Permissions = permissions });
        role.EnsureSuccessStatusCode();
        return app.Client(await app.UserAsync((await role.Json()).GetProperty("id").GetGuid()));
    }

    private static async Task<JsonElement> RequiredAsync(HttpResponseMessage r)
    {
        var problem = await r.Json();
        problem.GetProperty("code").GetString().ShouldBe("Approval.Required");
        return problem.GetProperty("errors");
    }

    private static object Withdrawal(Guid id, decimal amount, Guid? approval) =>
        new { MovementId = id, MovementType = "Withdrawal", Amount = amount, Reason = "CashPickup", SupervisorApprovalId = approval };

    [Fact]
    public async Task A1_an_approval_is_for_what_the_supervisor_saw_and_for_the_cashier_who_asked()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 30_000m);
        var movements = $"/api/cash/shifts/{cashier.ShiftId}/movements";

        // The server writes what is being approved; the register shows exactly that.
        var asked = await RequiredAsync(await cashier.Client.PostAsJsonAsync(movements, Withdrawal(Guid.NewGuid(), 100m, null)));
        var summary = asked.GetProperty("summary")[0].GetString()!;
        summary.ShouldBe("Retiro de efectivo de RD$ 100.00 · recogida de efectivo");

        // "Salida de RD$100" approved, RD$20,000 taken with it: refused.
        var approval = await cashier.Client.ApproveAsync("register.withdraw", cashier.ShiftId, summary);
        (await (await cashier.Client.PostAsJsonAsync(movements, Withdrawal(Guid.NewGuid(), 20_000m, approval))).ErrorCode()).ShouldBe("Identity.InvalidApproval");
        // An approval asked for by one cashier is not another's (on the same PC).
        var other = app.Client(await app.UserAsync(Roles.Cashier));
        (await (await other.PostAsJsonAsync(movements, Withdrawal(Guid.NewGuid(), 100m, approval))).ErrorCode()).ShouldBe("Identity.InvalidApproval");
        // The amount it was granted for still works, once.
        (await cashier.Client.PostAsJsonAsync(movements, Withdrawal(Guid.NewGuid(), 100m, approval))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // A return: approved for one piece, refused for two.
        var product = await ProductAsync();
        var sale = await (await cashier.SellAsync(Sale(cashier.ShiftId, 300m, 300m, Line(product, 3m)))).Json();
        var saleId = sale.GetProperty("saleId").GetGuid();
        var line = (await cashier.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}")).GetProperty("lines")[0].GetProperty("id").GetGuid();
        object Return(decimal quantity, Guid? id) => new
        {
            ReturnId = Guid.CreateVersion7(), cashier.ShiftId, Method = "Cash", Reason = "x", Lines = new[] { new { SaleLineId = line, Quantity = quantity } },
            SupervisorApprovalId = id,
        };
        var returns = $"/api/sales/{saleId}/returns";
        var one = (await RequiredAsync(await cashier.Client.PostAsJsonAsync(returns, Return(1m, null)))).GetProperty("summary")[0].GetString()!;
        one.ShouldContain("1 × ");
        var forOne = await cashier.Client.ApproveAsync("sales.return", saleId, one);
        (await (await cashier.Client.PostAsJsonAsync(returns, Return(2m, forOne))).ErrorCode()).ShouldBe("Identity.InvalidApproval");

        // A sale: the discount approved for one cart is not good for another under the same sale id.
        var discounted = Guid.CreateVersion7();
        object Discounted(decimal quantity, Guid[] approvals) => new
        {
            SaleId = discounted, cashier.ShiftId, Lines = new[] { new { ProductId = product, Quantity = quantity, Discount = new { IsPercentage = true, Value = 50m } } },
            Payments = new[] { new { Method = "Cash", Amount = quantity * 50m } }, ExpectedTotal = quantity * 50m, SupervisorApprovals = approvals,
        };
        var forOneUnit = (await RequiredAsync(await cashier.SellAsync(Discounted(1m, [])))).GetProperty("summary")[0].GetString()!;
        forOneUnit.ShouldContain("RD$ 50.00 de descuento");
        var discountApproval = await cashier.Client.ApproveAsync("sales.large-discount", discounted, forOneUnit);
        (await (await cashier.SellAsync(Discounted(5m, [discountApproval]))).ErrorCode()).ShouldBe("Approval.Required");
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task A2_an_invented_approval_id_does_not_skip_the_count_of_blind_misses_and_the_cashier_does_not_see_payments_while_open()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 2000m);
        object Count(int thousands, Guid? approval) => new { CashCount = new[] { new { Denomination = 1000m, Quantity = thousands } }, SupervisorApprovalId = approval };
        var close = $"/api/cash/shifts/{cashier.ShiftId}/close";
        foreach (var wrong in new[] { 1, 3, 4 })
            (await (await cashier.Client.PostAsJsonAsync(close, Count(wrong, Guid.NewGuid()))).ErrorCode()).ShouldBe("Identity.InvalidApproval");
        // Three misses were counted although each carried an id: the right count no longer tells.
        (await (await cashier.Client.PostAsJsonAsync(close, Count(2, null))).ErrorCode()).ShouldBe("Approval.Required");

        // The sales list of the cashier's open shift shows no payments or change (adding them up gives the expected cash).
        var other = await app.CashierWithShiftAsync(openingFloat: 0m);
        var product = await ProductAsync();
        var saleId = (await (await other.SellAsync(Sale(other.ShiftId, 100m, 500m, Line(product, 1m)))).Json()).GetProperty("saleId").GetGuid();
        var mine = await other.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}");
        (mine.GetProperty("payments").GetArrayLength(), mine.GetProperty("change").GetDecimal()).ShouldBe((0, 0m));
        var owner = await (await Admin()).GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}");
        (owner.GetProperty("payments").GetArrayLength(), owner.GetProperty("change").GetDecimal()).ShouldBe((1, 400m));
        (await other.Client.PostAsJsonAsync($"/api/cash/shifts/{other.ShiftId}/close", new { CashCount = new[] { new { Denomination = 100m, Quantity = 1 } } }))
            .EnsureSuccessStatusCode();
        (await other.Client.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}")).GetProperty("payments").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A3_backups_manage_alone_cannot_take_over_the_backups_or_send_them_to_a_network_folder()
    {
        const string Phrase = "la frase del dueño de la tienda";
        var admin = await Admin();
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase = Phrase })).EnsureSuccessStatusCode();
        var manager = await WithAsync("backups.manage");

        var replaced = await manager.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase = "la frase del que se la quiere llevar" });
        (await replaced.ErrorCode()).ShouldBe("Backup.CurrentPassphraseRequired");
        (await (await manager.PutAsJsonAsync("/api/backups/passphrase",
            new { Passphrase = "la frase del que se la quiere llevar", CurrentPassphrase = "una que no es" })).ErrorCode()).ShouldBe("Backup.CurrentPassphraseRequired");
        (await manager.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase = "una frase nueva y larga", CurrentPassphrase = Phrase }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.PutAsJsonAsync("/api/backups/passphrase", new { Passphrase = Phrase })).StatusCode.ShouldBe(HttpStatusCode.NoContent); // the owner may

        // A copy to another computer only with an Administrador; the audit keeps before and after.
        var network = new { CopyTo = @"\\otra-pc\respaldos", Time = "23:30", Keep = 14, Automatic = true };
        (await (await manager.PutAsJsonAsync("/api/backups/settings", network)).ErrorCode()).ShouldBe("Backup.NetworkCopyAdministratorOnly");
        (await admin.PutAsJsonAsync("/api/backups/settings", network)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await manager.PutAsJsonAsync("/api/backups/settings", network with { Keep = 10 })).StatusCode.ShouldBe(HttpStatusCode.NoContent); // already approved
        var today = Toki.SharedKernel.BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime);
        var audit = await admin.GetFromJsonAsync<JsonElement>($"/api/audit?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}&action=settings.backup-schedule");
        var data = JsonDocument.Parse(audit[0].GetProperty("data").GetString()!).RootElement;
        (data.GetProperty("before").GetProperty("keep").GetInt32(), data.GetProperty("after").GetProperty("keep").GetInt32()).ShouldBe((14, 10));
        (await admin.PutAsJsonAsync("/api/backups/settings", new { CopyTo = "", Time = "23:30", Keep = 14, Automatic = true })).EnsureSuccessStatusCode();

        // The PIN hashes in the database (and so in any backup) are keyed with a secret of this server.
        var hashes = await app.InScopeAsync(sp => sp.GetRequiredService<Toki.Identity.Persistence.IdentityDbContext>().Users.Select(u => u.PinHash).ToListAsync());
        hashes.ShouldAllBe(h => h.StartsWith("p1:"));
    }

    [Fact]
    public async Task A4_repeated_wrong_pins_show_in_the_health_screen()
    {
        var admin = await app.AdminAsync();
        var victim = await app.UserAsync(Roles.Cashier, pin: "2468");
        var code = (await app.Client(admin).GetFromJsonAsync<JsonElement>("/api/identity/users")).EnumerateArray()
            .Single(u => u.GetProperty("id").GetGuid() == victim.UserId).GetProperty("code").GetString();
        for (var i = 0; i < 3; i++) await app.Anonymous(admin.Device).PostAsJsonAsync("/api/identity/sessions", new { Code = code, Pin = "1111" });

        var health = await app.Client(admin).GetFromJsonAsync<JsonElement>("/api/settings/health");
        var check = health.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "pin-failures");
        check.GetProperty("status").GetString().ShouldNotBe("Healthy");
    }

    [Fact]
    public async Task A5_removing_a_line_before_charging_is_on_the_cashiers_Z_and_asks_a_supervisor_when_the_store_says_so()
    {
        var cashier = await app.CashierWithShiftAsync(openingFloat: 1000m);
        var removals = $"/api/cash/shifts/{cashier.ShiftId}/cart-removals";
        object Removal(Guid? approval) => new
        {
            RemovalId = Guid.CreateVersion7(), RemovalType = "Line", Description = "Whisky 750 ml", Quantity = 1m, Amount = 2500m, SupervisorApprovalId = approval,
        };
        (await cashier.Client.PostAsJsonAsync(removals, Removal(null))).StatusCode.ShouldBe(HttpStatusCode.OK); // the policy does not ask by default

        var admin = await Admin();
        var policy = (await admin.GetFromJsonAsync<JsonElement>("/api/settings/approvals")).EnumerateArray()
            .Where(r => r.GetProperty("required").GetBoolean()).Select(r => r.GetProperty("permission").GetString()).Append("sales.remove-line").ToList();
        (await admin.PutAsJsonAsync("/api/settings/approvals", new { Required = policy })).EnsureSuccessStatusCode();
        var asked = await RequiredAsync(await cashier.Client.PostAsJsonAsync(removals, Removal(null)));
        asked.GetProperty("summary")[0].GetString().ShouldBe("Quitar 1 × Whisky 750 ml (RD$ 2,500.00)");
        (await cashier.Client.SupervisedAsync(cashier.ShiftId, approval => cashier.Client.PostAsJsonAsync(removals, Removal(approval))))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await app.DoNotAskSupervisorAsync("sales.remove-line");

        var x = await app.XReportAsync(cashier.ShiftId);
        (x.GetProperty("removedLines").GetInt32(), x.GetProperty("removedAmount").GetDecimal(), x.GetProperty("expectedCash").GetDecimal()).ShouldBe((2, 5000m, 1000m));
    }

    [Fact]
    public async Task A6_selling_on_credit_needs_credit_sell_or_a_supervisor_and_the_cashier_report_shows_it()
    {
        var cashier = await app.CashierWithShiftAsync();
        var admin = await Admin();
        var customer = (await (await admin.PostAsJsonAsync("/api/customers", new { Name = "Fiado " + Guid.NewGuid().ToString()[..4], CreditLimit = 5000m })).Json())
            .GetProperty("id").GetGuid();
        var product = await ProductAsync();
        var saleId = Guid.CreateVersion7();
        object OnAccount(Guid[] approvals) => new
        {
            SaleId = saleId, cashier.ShiftId, CustomerId = customer, Lines = new[] { Line(product, 3m) },
            Payments = new[] { new { Method = "OnAccount", Amount = 300m } }, ExpectedTotal = 300m, SupervisorApprovals = approvals,
        };
        (await RequiredAsync(await cashier.SellAsync(OnAccount([])))).GetProperty("permission")[0].GetString().ShouldBe("credit.sell");
        (await cashier.SellSupervisedAsync(saleId, OnAccount)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var today = Toki.SharedKernel.BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime);
        var report = await admin.GetFromJsonAsync<JsonElement>($"/api/reports/sales/by-cashier?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        report.EnumerateArray().Single(r => r.GetProperty("cashierId").GetGuid() == cashier.Session.UserId).GetProperty("soldOnCredit").GetDecimal().ShouldBe(300m);
    }

    [Fact]
    public async Task A7_a_float_other_than_the_registers_standard_one_needs_a_supervisor()
    {
        var admin = await Admin();
        var register = (await (await admin.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja fondo " + Guid.NewGuid().ToString()[..4] })).Json()).GetProperty("id").GetGuid();
        (await admin.PutAsJsonAsync($"/api/cash/registers/{register}/standard-float", new { Amount = 2000m })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));

        var asked = await RequiredAsync(await cashier.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 500m }));
        asked.GetProperty("permission")[0].GetString().ShouldBe("register.approve-close");
        asked.GetProperty("summary")[0].GetString()!.ShouldEndWith("con un fondo de RD$ 500.00 (el fondo de esta caja es RD$ 2,000.00)");
        (await cashier.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register, OpeningFloat = 2000m })).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A8_a_fiscal_credit_invoice_to_an_RNC_other_than_the_customers_needs_a_supervisor()
    {
        var cashier = await app.CashierWithShiftAsync();
        var admin = await Admin();
        var customer = (await (await admin.PostAsJsonAsync("/api/customers",
            new { Name = "Empresa " + Guid.NewGuid().ToString()[..4], TaxId = "130" + Random.Shared.Next(100000, 999999), FiscalCredit = true })).Json()).GetProperty("id").GetGuid();
        var product = await ProductAsync();
        object E31(string? rnc) => new
        {
            SaleId = Guid.CreateVersion7(), cashier.ShiftId, CustomerId = customer, DocumentType = "FiscalCredit31", CustomerRnc = rnc, Lines = new[] { Line(product, 1m) },
            Payments = new[] { new { Method = "Cash", Amount = 100m } }, ExpectedTotal = 100m,
        };
        (await cashier.SellAsync(E31(null))).StatusCode.ShouldBe(HttpStatusCode.Created); // the customer's own RNC
        var other = await RequiredAsync(await cashier.SellAsync(E31("131245678")));
        other.GetProperty("permission")[0].GetString().ShouldBe("customers.manage");
        other.GetProperty("summary")[0].GetString()!.ShouldContain("crédito fiscal a 131245678");
    }

    [Fact]
    public async Task A9_A11_the_seeded_roles_follow_docs_permisos()
    {
        var roles = (await (await Admin()).GetFromJsonAsync<JsonElement>("/api/identity/roles")).GetProperty("roles").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList());
        foreach (var cashier in new[] { Roles.Cashier, Roles.CashierWithoutOpening })
        {
            roles[cashier].ShouldContain("customers.quick-add");
            roles[cashier].ShouldNotContain("customers.manage");
            roles[cashier].ShouldNotContain("inventory.view");
        }
        roles[Roles.Supervisor].ShouldNotContain("payables.pay");
        roles[Roles.Supervisor].ShouldContain("purchasing.manage");
    }

    [Fact]
    public async Task A12_without_costs_view_the_606_posted_purchases_and_audit_records_do_not_show_costs()
    {
        var admin = await Admin();
        var supervisor = app.Client(await app.UserAsync(Roles.Supervisor)); // purchasing.manage and fiscal.reports, not costs.view
        var today = Toki.SharedKernel.BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime);
        (await (await supervisor.GetAsync($"/api/reports/dgii/606?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}")).ErrorCode()).ShouldBe("Reports.CostsRequired");

        var supplier = (await (await admin.PostAsJsonAsync("/api/purchasing/suppliers",
            new { Name = "Suplidor " + Guid.NewGuid().ToString()[..4], Rnc = "101234567", CreditDays = 0 })).Json()).GetProperty("id").GetGuid();
        var product = await ProductAsync();
        var created = await admin.PostAsJsonAsync("/api/purchasing/purchases", new
        {
            SupplierId = supplier, Ncf = "B01" + Random.Shared.Next(10000000, 99999999), InvoiceDate = today, Terms = "Cash",
            Lines = new[] { new { ProductId = product, Quantity = 2m, UnitCost = 40m, ItbisRate = "Exempt" } },
        });
        if (!created.IsSuccessStatusCode) throw new InvalidOperationException(await created.Content.ReadAsStringAsync());
        var purchase = (await created.Json()).GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/purchasing/purchases/{purchase}/post", null)).EnsureSuccessStatusCode();
        var line = (await supervisor.GetFromJsonAsync<JsonElement>($"/api/purchasing/purchases/{purchase}")).GetProperty("lines")[0];
        line.GetProperty("unitCost").ValueKind.ShouldBe(JsonValueKind.Null);
        line.GetProperty("quantity").GetDecimal().ShouldBe(2m);
        (await admin.GetFromJsonAsync<JsonElement>($"/api/purchasing/purchases/{purchase}")).GetProperty("lines")[0].GetProperty("unitCost").GetDecimal().ShouldBe(40m);

        Toki.Reports.AuditEndpoints.WithoutCosts("""{"Name":"Res","Cost":150,"Lines":[{"UnitCost":40,"Quantity":2}]}""")
            .ShouldBe("""{"Name":"Res","Cost":null,"Lines":[{"UnitCost":null,"Quantity":2}]}""");
    }

    [Fact]
    public async Task A13_a_cashier_reads_only_their_own_sales_and_parked_carts()
    {
        var ana = await app.CashierWithShiftAsync();
        var luis = await app.CashierWithShiftAsync();
        var product = await ProductAsync();
        var saleId = (await (await ana.SellAsync(Sale(ana.ShiftId, 100m, 100m, Line(product, 1m)))).Json()).GetProperty("saleId").GetGuid();

        (await luis.Client.GetAsync($"/api/sales/{saleId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await luis.Client.GetAsync($"/api/sales/{saleId}/receipt")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ana.Client.GetAsync($"/api/sales/{saleId}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ana.Client.PutAsJsonAsync("/api/sales/held", new { Id = Guid.CreateVersion7(), ana.ShiftId, Label = "Doña Rosa", Total = 10m, Content = new { } }))
            .EnsureSuccessStatusCode();
        (await luis.Client.GetFromJsonAsync<JsonElement>($"/api/sales/held?shiftId={ana.ShiftId}")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A15_users_manage_neither_resets_someone_elses_pin_nor_removes_permissions_it_lacks()
    {
        // Everything the Cajero has, so the only thing missing is being an Administrador.
        var manager = await WithAsync("users.manage", "sales.create", "register.close", "register.cash-in", "credit.collect", "customers.quick-add");
        var cashier = await app.UserAsync(Roles.CashierWithoutOpening);
        var reset = await manager.PutAsJsonAsync($"/api/identity/users/{cashier.UserId}",
            new { Name = "Cajero", RoleId = Roles.CashierWithoutOpening, IsActive = true, Pin = "9999" });
        (await reset.ErrorCode()).ShouldBe("Identity.PinResetAdministratorOnly");

        var role = await (await Admin()).PostAsJsonAsync("/api/identity/roles", new { Name = "Caja y cierre " + Guid.NewGuid().ToString()[..4],
            Permissions = new[] { "sales.create", "register.approve-close" } });
        var roleId = (await role.Json()).GetProperty("id").GetGuid();
        (await (await manager.PutAsJsonAsync($"/api/identity/roles/{roleId}", new { Name = "Caja", Permissions = new[] { "sales.create" } })).ErrorCode())
            .ShouldBe("Identity.PermissionsNotHeld");
    }

    [Fact]
    public async Task A16_card_and_transfer_payments_carry_their_reference_unless_the_store_turns_it_off()
    {
        var cashier = await app.CashierWithShiftAsync();
        var product = await ProductAsync();
        object ByCard(string? reference) => new
        {
            SaleId = Guid.CreateVersion7(), cashier.ShiftId, Lines = new[] { Line(product, 1m) },
            Payments = new[] { new { Method = "Card", Amount = 100m, Reference = reference } }, ExpectedTotal = 100m,
        };
        (await (await cashier.SellAsync(ByCard(null))).ErrorCode()).ShouldBe("Sales.ReferenceRequired");
        (await cashier.SellAsync(ByCard("AUT-123"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        var admin = await Admin();
        await app.SetUpBusinessAsync();
        var business = await admin.GetFromJsonAsync<JsonElement>("/api/settings/business");
        (await admin.PutAsJsonAsync("/api/settings/business", new
        {
            Name = business.GetProperty("name").GetString(), Rnc = business.GetProperty("rnc").GetString(), DiscountCapPercent = 10m, RequirePaymentReference = false,
        })).EnsureSuccessStatusCode();
        (await cashier.SellAsync(ByCard(null))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.PutAsJsonAsync("/api/settings/business", new
        {
            Name = business.GetProperty("name").GetString(), Rnc = business.GetProperty("rnc").GetString(), DiscountCapPercent = 10m, RequirePaymentReference = true,
        })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A19_expenses_and_payments_cannot_be_dated_in_a_month_whose_606_was_filed()
    {
        var admin = await Admin();
        var supplier = (await (await admin.PostAsJsonAsync("/api/purchasing/suppliers",
            new { Name = "Luz " + Guid.NewGuid().ToString()[..4], Rnc = "101234567", CreditDays = 0 })).Json()).GetProperty("id").GetGuid();
        var today = Toki.SharedKernel.BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime);
        var firstOpen = new DateOnly(today.Year, today.Month, 1).AddMonths(today.Day > 15 ? 0 : -1);
        object Expense(DateOnly date) => new
        {
            SupplierId = supplier, Ncf = "B01" + Random.Shared.Next(10000000, 99999999), Date = date, Description = "Luz", Type = "WorkSuppliesAndServices",
            Subtotal = 100m, Itbis = 0m, Terms = "Credit",
        };
        (await admin.PostAsJsonAsync("/api/purchasing/expenses", Expense(firstOpen.AddDays(-1)))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/purchasing/expenses", Expense(firstOpen))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await admin.PostAsJsonAsync("/api/purchasing/payments", new { SupplierId = supplier, Amount = 100m, Method = "Transfer", Date = firstOpen.AddDays(-1) }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

/// <summary>R11 A4: whoever can approve is signed out after five idle minutes; the register's own polling does not keep the session open.</summary>
public sealed class IdleLockTests(IdleLockTests.App app) : IClassFixture<IdleLockTests.App>
{
    public sealed class App : TokiApp
    {
        protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string> { ["Identity:IdleMinutes"] = "5" };
    }

    [Fact]
    public async Task An_approvers_session_closes_after_five_idle_minutes_a_cashiers_does_not()
    {
        var admin = await app.AdminAsync();
        var cashier = await app.UserAsync(Roles.Cashier);
        var owner = app.Client(admin);
        (await owner.GetAsync("/api/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        app.Clock.Advance(TimeSpan.FromMinutes(4));
        using var poll = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        poll.Headers.Add("X-Toki-Background", "1");
        (await owner.SendAsync(poll)).StatusCode.ShouldBe(HttpStatusCode.OK); // still inside the five minutes, but it does not count as activity
        app.Clock.Advance(TimeSpan.FromMinutes(2));
        (await owner.GetAsync("/api/identity/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.Client(cashier).GetAsync("/api/identity/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

/// <summary>R11 A10, A18: the audit chain is keyed (a rewrite with plain SHA-256 is caught) and a server clock behind the newest record writes
/// nothing. Own app: both tests damage the audit log on purpose.</summary>
public sealed class AuditIntegrityTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task A10_rewriting_a_record_and_recomputing_the_chain_without_the_key_is_detected()
    {
        await app.AdminAsync();
        (await app.VerifyAuditAsync()).ShouldBeNull();
        var broken = await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<InfraDbContext>();
            var records = await db.AuditRecords.OrderBy(r => r.Sequence).ToListAsync();
            var previous = new byte[32];
            foreach (var r in records)
            {
                if (r.Sequence == 1) r.Data = r.Data.Replace("Dueña", "Otra", StringComparison.Ordinal);
                r.PreviousHash = previous;
                r.Hash = previous = AuditChain.Hash(r); // what someone with only the database can compute
            }
            await db.SaveChangesAsync();
            await db.AuditHead.Where(h => h.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(h => h.Hash, previous));
            return await AuditChain.VerifyAsync(db, sp.GetRequiredService<AuditKey>(), CancellationToken.None);
        });
        broken.ShouldBe(1);
    }

    [Fact]
    public async Task A18_with_the_clock_behind_the_newest_audit_record_nothing_is_saved()
    {
        var admin = app.Client(await app.AdminAsync());
        await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.OrderByDescending(r => r.Sequence).Take(1)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DateUtc, app.Clock.GetUtcNow().UtcDateTime.AddHours(2))));
        var r = await admin.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja atrasada" });
        r.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await r.ErrorCode()).ShouldBe("Clock.Behind");
    }
}
