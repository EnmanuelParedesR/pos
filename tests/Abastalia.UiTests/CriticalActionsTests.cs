using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using static Abastalia.UiTests.Store;
using static Microsoft.Playwright.Assertions;

namespace Abastalia.UiTests;

/// <summary>TK-365: the main PC of a store set up once (<see cref="Store"/>), shared by the critical-action scenarios below.</summary>
public sealed class CriticalStore : IAsyncLifetime
{
    public RegisterApp App { get; } = new() { TraceName = "critical-actions" };
    public Store Store { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await App.InitializeAsync();
        Store = await Store.SetUpAsync(App);
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();
}

/// <summary>TK-365: the critical actions RegisterDayTests does not reach, in the browser against the real API. Each scenario has its own cashier
/// and register, so they run in any order on the one store. Sales by cash and card, credit within the limit, void and return with the supervisor
/// and the blind close with a difference are RegisterDayTests'.</summary>
[Collection(Till.Suite)]
public sealed class CriticalActionsTests(CriticalStore store) : IClassFixture<CriticalStore>
{
    private Store S => store.Store;
    private Till Till { get; } = new(store.App.Page);
    private IPage Page => Till.Page;

    [Fact]
    public Task Credit_over_the_limit_asks_the_supervisor_for_the_credit_and_for_the_limit() => RunAsync(async () =>
    {
        await S.CashierAndRegisterAsync("Cajera Crédito", "cred", "Caja Crédito");
        await Ok(await S.Admin.PostAsJsonAsync("/api/customers", new { Name = "Colmado Vecino", CreditLimit = 100m }));
        await Till.OpenShiftAsync("Cajera Crédito", CashierPin, "Caja Crédito");
        await Till.ChooseCustomerAsync("Colmado Vecino");
        await Till.ScanAsync(Soda, lines: 1);
        await Till.Keys.PressAsync("F3");
        await Till.TypeInDialogAsync("Cantidad", "2");
        await Expect(Till.Lines.Nth(0)).ToContainTextAsync("118.00");
        var checkout = await Till.OpenCheckoutAsync();
        await Till.Keys.PressAsync("F8");
        await Expect(checkout.GetByLabel("Monto")).ToHaveValueAsync("118.00");
        await Till.Keys.PressAsync("Enter");
        // The cashier has neither: selling on account (R11 A6) and going 18.00 over the 100.00 limit are approved one by one.
        await Till.ApproveAsync(OwnerName, OwnerPin, "Vender a crédito o con saldo a favor", "Vender a crédito por encima del límite");
        await Expect(Till.Saved).ToBeVisibleAsync();
    });

    [Fact]
    public Task The_blind_close_never_shows_the_cashier_what_is_expected() => RunAsync(async () =>
    {
        await S.CashierAndRegisterAsync("Cajero Cierre", "cier", "Caja Cierre");
        await Till.OpenShiftAsync("Cajero Cierre", CashierPin, "Caja Cierre");
        await Till.ScanAsync(Soda, lines: 1);
        await Till.ChargeCashAsync();
        await CloseRegisterFromMenuAsync();
        (await S.ShiftZAsync("Caja Cierre")).GetProperty("expectedCash").GetDecimal().ShouldBe(1059m);

        var main = Page.GetByRole(AriaRole.Main);
        await Expect(Page.GetByText("El monto esperado no se muestra aquí")).ToBeVisibleAsync();
        await Expect(main).Not.ToContainTextAsync("1,059");
        await Till.CountAsync(1000, 1);
        await Till.CountAsync(1, 59);
        await Till.Keys.PressAsync("F12");
        await Expect(Page.GetByText("¿Cerrar con RD$ 1,059.00 contado?")).ToBeVisibleAsync();
        await Expect(main.GetByText("Esperado", new() { Exact = true })).ToHaveCountAsync(0); // up to the last step: only what was counted
        await Till.Keys.PressAsync("F12");
        await Expect(Page.GetByText("La caja cuadró.")).ToBeVisibleAsync(); // no difference: no supervisor
        await Expect(main.GetByText("Esperado", new() { Exact = true })).ToBeVisibleAsync();
    });

    [Fact]
    public Task A_cashier_without_a_permission_is_refused_until_someone_who_has_it_approves() => RunAsync(async () =>
    {
        const string Cashier = "0199e000-0000-7000-8000-000000000003"; // "Cajero": sells and closes, does not open the register
        await Ok(await S.Admin.PostAsJsonAsync("/api/identity/users", new { Name = "Cajero Sin Apertura", Code = "sinap", Pin = CashierPin, RoleId = Guid.Parse(Cashier) }));
        await Ok(await S.Admin.PostAsJsonAsync("/api/identity/users", new { Name = "Cajero Ayudante", Code = "ayud", Pin = "8642", RoleId = Guid.Parse(Cashier) }));
        await Ok(await S.Admin.PostAsJsonAsync("/api/cash/registers", new { Name = "Caja Permisos" }));

        await Till.SignInAsync("Cajero Sin Apertura", CashierPin);
        await Expect(Till.Heading("Abrir caja")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Caja Permisos", Exact = true }).ClickAsync();
        await Till.CountAsync(1000, 1);
        await Till.Keys.PressAsync("F12");
        var approval = Page.GetByRole(AriaRole.Dialog, new() { Name = "Autoriza un supervisor" });
        await Expect(approval).ToContainTextAsync("Abrir turno");

        // A fellow cashier cannot authorize what they do not have themselves.
        await Till.ChooseSupervisorAsync(approval, "Cajero Ayudante");
        await Till.Keys.TypeAsync("8642");
        await Till.Keys.PressAsync("Enter");
        await Expect(approval.GetByText(new Regex("no puede autorizar"))).ToBeVisibleAsync();
        await Till.Keys.PressAsync("Escape");
        await Expect(approval).ToBeHiddenAsync();
        await Expect(Page.GetByText("Se canceló la autorización.")).ToBeVisibleAsync();
        await Expect(Till.Heading("Abrir caja")).ToBeVisibleAsync();
        var registers = await Ok(await S.Admin.GetAsync("/api/cash/registers"));
        registers.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Caja Permisos").GetProperty("openShiftId").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        // The owner has it: the same opening goes through, and the shift is the cashier's.
        await Till.Keys.PressAsync("F12");
        await Till.ApproveAsync(OwnerName, OwnerPin, "Abrir turno");
        await Expect(Till.Scanner).ToBeFocusedAsync();
    });

    [Fact]
    public Task A_blocked_store_cannot_sell_but_closes_the_shift_and_exports() => RunAsync(async () =>
    {
        await S.CashierAndRegisterAsync("Cajera Licencia", "lic", "Caja Licencia");
        await Till.OpenShiftAsync("Cajera Licencia", CashierPin, "Caja Licencia");
        await Till.ScanAsync(Soda, lines: 1);
        await Till.ChargeCashAsync();

        await Ok(await S.Admin.PutAsJsonAsync("/api/settings/license", new { License = SignedLicense(store.App.Api.LicenseKey, blocked: true) }));
        try
        {
            // TK-279: the register learns it at the next screen; no sale, but the open shift can be closed.
            await Till.SignInAsync("Cajera Licencia", CashierPin);
            var blocked = Page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "no se pueden hacer ventas nuevas ni abrir turnos" });
            await Expect(blocked).ToBeVisibleAsync();
            await Expect(Till.Scanner).ToBeHiddenAsync();
            await Till.PressOnAsync(blocked.GetByRole(AriaRole.Button, new() { Name = "Cerrar el turno" }));
            await Expect(Till.Heading("Cerrar caja")).ToBeVisibleAsync();
            await Till.CountAsync(1000, 1);
            await Till.CountAsync(1, 59);
            await Till.Keys.PressAsync("F12");
            await Till.Keys.PressAsync("F12");
            await Expect(Page.GetByText("La caja cuadró.")).ToBeVisibleAsync();

            // Without a shift, the blocked screen offers no way to open one.
            await Till.SignInAsync("Cajera Licencia", CashierPin);
            await Expect(blocked).ToBeVisibleAsync();
            await Expect(blocked.GetByRole(AriaRole.Button, new() { Name = "Cerrar el turno" })).ToHaveCountAsync(0);

            // The owner still exports for the accountant.
            await Till.SignInAsync(OwnerName, OwnerPin);
            await Page.GetByRole(AriaRole.Link, new() { Name = "Reportes", Exact = true }).ClickAsync();
            var download = await Page.RunAndWaitForDownloadAsync(() =>
                Page.GetByRole(AriaRole.Button, new() { Name = "Descargar Formato 607 · ventas en CSV" }).ClickAsync());
            download.SuggestedFilename.ShouldContain("607");
        }
        finally
        {
            await Ok(await S.Admin.PutAsJsonAsync("/api/settings/license", new { License = SignedLicense(store.App.Api.LicenseKey, blocked: false) }));
        }
    });

    [Fact]
    public Task A_register_paired_to_the_main_PC_sells_through_it() => RunAsync(async () =>
    {
        await S.CashierAndRegisterAsync("Cajero Caja 2", "caj2", "Caja 2");
        var code = (await Ok(await S.Admin.PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja 2" }))).GetProperty("code").GetString()!;

        var other = new Till(await store.App.AnotherPcAsync("caja2"));
        await other.Page.GotoAsync("/");
        await other.PressOnAsync(other.Page.GetByRole(AriaRole.Tab, new() { Name = "Unir a una tienda" }));
        await other.Page.GetByLabel("Código de 8 dígitos").FillAsync(code);
        await other.PressOnAsync(other.Page.GetByRole(AriaRole.Button, new() { Name = "Unir este equipo" }));
        await Expect(other.Heading("¿Quién entra?")).ToBeVisibleAsync();

        await other.OpenShiftAsync("Cajero Caja 2", CashierPin, "Caja 2");
        await other.ScanAsync(Soda, lines: 1);
        await other.ChargeCashAsync();
        await Expect(other.Page.GetByText("En línea")).ToBeVisibleAsync();

        // The sale is on the main PC's server, in Caja 2's shift, from the paired device.
        var z = await S.ShiftZAsync("Caja 2");
        z.GetProperty("saleCount").GetInt32().ShouldBe(1);
        var devices = await Ok(await S.Admin.GetAsync("/api/identity/devices"));
        devices.EnumerateArray().ShouldContain(d => d.GetProperty("name").GetString() == "Caja 2");
    });

    private async Task CloseRegisterFromMenuAsync()
    {
        await Expect(Till.Scanner).ToBeFocusedAsync();
        await Till.Keys.PressAsync("F9");
        var menu = Page.GetByRole(AriaRole.Dialog, new() { Name = "Caja" });
        await Till.PressOnAsync(menu.GetByRole(AriaRole.Button, new() { Name = "Cerrar caja" }));
        await Expect(Till.Heading("Cerrar caja")).ToBeVisibleAsync();
    }

    private async Task RunAsync(Func<Task> scenario)
    {
        try
        {
            await scenario();
        }
        catch (Exception e) when (e is PlaywrightException or ShouldAssertException or TimeoutException)
        {
            throw new InvalidOperationException($"{e.Message}\n{store.App.Logs()}", e);
        }
        store.App.Api.Errors500.ShouldBeEmpty();
    }
}
