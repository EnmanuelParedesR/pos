using System.Net.Http.Json;
using Microsoft.Playwright;
using Shouldly;
using static Abastalia.UiTests.Store;
using static Microsoft.Playwright.Assertions;

namespace Abastalia.UiTests;

/// <summary>TK-365: the owner backs up and restores from Configuración → Respaldos. A store of its own: restoring replaces every piece of data.</summary>
[Collection(Till.Suite)]
public sealed class BackupRestoreTests(BackupRestoreTests.Fixture fixture) : IClassFixture<BackupRestoreTests.Fixture>
{
    public sealed class Fixture : IAsyncLifetime
    {
        public RegisterApp App { get; } = new() { TraceName = "backup-restore" };
        public ValueTask InitializeAsync() => App.InitializeAsync();
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private const string Passphrase = "frase de prueba larga";
    private RegisterApp App => fixture.App;

    private Till Till { get; } = new(fixture.App.Page);
    private IPage Page => Till.Page;

    [Fact]
    public async Task Back_up_then_restore_brings_the_data_back()
    {
        try
        {
            await DayAsync();
        }
        catch (Exception e) when (e is PlaywrightException or ShouldAssertException or TimeoutException)
        {
            throw new InvalidOperationException($"{e.Message}\n{App.Logs()}", e);
        }
        App.Api.Errors500.ShouldBeEmpty();
    }

    private async Task DayAsync()
    {
        var store = await SetUpAsync(App);
        await Till.SignInAsync(OwnerName, OwnerPin);
        await Page.GetByRole(AriaRole.Link, new() { Name = "Configuración", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Respaldos" }).ClickAsync();

        await Page.GetByLabel("Frase", new() { Exact = false }).First.FillAsync(Passphrase);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Guardar frase" }).ClickAsync();
        await Expect(Page.GetByText("Frase guardada.")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Respaldar ahora" }).ClickAsync();
        await Expect(Page.GetByText("Respaldo creado.")).ToBeVisibleAsync();
        var restore = Page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Restaurar ") });
        await Expect(restore).ToHaveCountAsync(1);

        // After the backup: a customer the restore must take away.
        await Ok(await store.Admin.PostAsJsonAsync("/api/customers", new { Name = "Cliente después del respaldo", CreditLimit = 0m }));

        await restore.ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Restaurar respaldo" });
        var phrase = dialog.GetByLabel("Frase de los respaldos");
        await phrase.FillAsync("otra frase cualquiera");
        await dialog.GetByLabel("Las cajas están cerradas").CheckAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Continuar" }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Sí, reemplazar los datos" }).ClickAsync();
        await Expect(dialog.GetByText("Esa frase no abre este respaldo.")).ToBeVisibleAsync();

        await phrase.FillAsync(Passphrase);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Continuar" }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Sí, reemplazar los datos" }).ClickAsync();
        await Expect(dialog.GetByText("Respaldo restaurado.")).ToBeVisibleAsync();
        await Expect(dialog).ToContainTextAsync("antes-de-restaurar"); // the data of before, kept in a backup of its own
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Volver a entrar" }).ClickAsync();
        await Expect(Till.Heading("¿Quién entra?")).ToBeVisibleAsync();

        // The restored data is the backup's: the owner signs in again and the later customer is gone.
        await Till.SignInAsync(OwnerName, OwnerPin);
        var token = (await Ok(await App.Api.Client(store.Device).PostAsJsonAsync("/api/identity/sessions", new { Code = "admin", Pin = OwnerPin }))).GetProperty("token").GetString();
        var customers = await Ok(await App.Api.Client(store.Device, token).GetAsync("/api/customers"));
        customers.ToString().ShouldNotContain("Cliente después del respaldo");
    }
}
