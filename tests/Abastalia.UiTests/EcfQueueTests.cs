using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using static Abastalia.UiTests.Store;
using static Microsoft.Playwright.Assertions;

namespace Abastalia.UiTests;

/// <summary>TK-365: a store in TesteCF with a (self-signed) certificate, its API talking to <see cref="DgiiServer"/> instead of the DGII.</summary>
public sealed class DgiiStore : IAsyncLifetime
{
    private readonly string _pfx = Path.Combine(Path.GetTempPath(), $"abastalia_ui_ecf_{Guid.NewGuid():N}.pfx");

    public DgiiServer Dgii { get; private set; } = null!;
    public RegisterApp App { get; private set; } = null!;
    public Store Store { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        using (var rsa = RSA.Create(2048))
        using (var cert = new CertificateRequest("CN=Firmante de Prueba, SERIALNUMBER=00112345678", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                   .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1)))
            await File.WriteAllBytesAsync(_pfx, cert.Export(X509ContentType.Pkcs12, "prueba"));
        Dgii = await DgiiServer.StartAsync();
        Dgii.Dgii.PollsInProcess = 0;
        App = new RegisterApp
        {
            TraceName = "ecf-queue",
            ApiSettings = new()
            {
                ["Fiscal__Signer"] = "Certificate", ["Fiscal__Certificate__Path"] = _pfx, ["Fiscal__Certificate__Password"] = "prueba",
                ["Fiscal__Provider"] = "Dgii", ["Fiscal__Dgii__Environment"] = "TesteCF", ["Fiscal__Dgii__TestServer"] = Dgii.Base.ToString(),
                ["Fiscal__AutoSubmit"] = "true", ["Fiscal__SubmitIntervalSeconds"] = "1", ["Fiscal__Dgii__TimeoutSeconds"] = "120", // the test holds the DGII's answer for a while
            },
        };
        await App.InitializeAsync();
        Store = await Store.SetUpAsync(App);
    }

    public async ValueTask DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
        if (Dgii is not null) await Dgii.DisposeAsync();
        File.Delete(_pfx);
    }
}

/// <summary>TK-365: the e-CF queue as the owner sees it in Comprobantes e-CF: waiting to go out, accepted, rejected by the DGII and its replacement
/// accepted. The page loads once (no live refresh), so the test waits for the server's state and then opens it again.</summary>
[Collection(Till.Suite)]
public sealed class EcfQueueTests(DgiiStore store) : IClassFixture<DgiiStore>
{
    private Till Till { get; } = new(store.App.Page);
    private IPage Page => Till.Page;

    [Fact]
    public async Task Pending_accepted_rejected_and_replaced()
    {
        try
        {
            await DayAsync();
        }
        catch (Exception e) when (e is PlaywrightException or ShouldAssertException or TimeoutException)
        {
            throw new InvalidOperationException($"{e.Message}\n{store.App.Logs()}", e);
        }
        store.App.Api.Errors500.ShouldBeEmpty();
    }

    private async Task DayAsync()
    {
        const string First = "E320000000001", Second = "E320000000002";
        store.Dgii.Dgii.Result = encf => encf == Second ? (2, true, "El valor del campo MontoTotal no corresponde") : (1, true, null);
        store.Dgii.Hold();

        await store.Store.CashierAndRegisterAsync("Cajera", "caja", "Caja 1");
        await Till.OpenShiftAsync("Cajera", CashierPin, "Caja 1");
        for (var i = 0; i < 2; i++)
        {
            await Till.ScanAsync(Soda, lines: 1);
            await Till.ChargeCashAsync();
        }

        // ── While the DGII has not answered: both wait to go out ──
        await Till.SignInAsync(OwnerName, OwnerPin);
        await OpenEcfAsync();
        await Expect(Row(First)).ToContainTextAsync("Por enviar");
        await Expect(Row(Second)).ToContainTextAsync("Por enviar");

        // ── The DGII answers: the first accepted, the second rejected with its reason ──
        store.Dgii.Release();
        await StatusAsync(First, "Accepted");
        await StatusAsync(Second, "Rejected");
        await OpenEcfAsync();
        await Expect(Row(First)).ToContainTextAsync("Aceptado");
        var rejected = Page.GetByRole(AriaRole.Region, new() { Name = "Rechazados por la DGII" });
        var row = rejected.GetByRole(AriaRole.Row).Filter(new() { HasText = Second });
        await Expect(row).ToContainTextAsync("El valor del campo MontoTotal no corresponde");

        // ── Replaced (a second tap confirms): a new e-NCF with the same data, accepted ──
        await row.GetByRole(AriaRole.Button, new() { Name = "Reemplazar" }).ClickAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = "¿Reemplazar? Toque otra vez" }).ClickAsync();
        await Expect(Page.GetByText($"en reemplazo de {Second}")).ToBeVisibleAsync();
        var replacement = (await DocumentsAsync()).Single(d => d.GetProperty("replacesENcf").GetString() == Second).GetProperty("eNcf").GetString()!;
        await StatusAsync(replacement, "Accepted");
        await OpenEcfAsync();
        await Expect(Row(replacement)).ToContainTextAsync("Aceptado");
        await Expect(Row(replacement)).ToContainTextAsync($"reemplaza {Second}");
        await Expect(Row(Second)).ToContainTextAsync($"Reemplazado por {replacement}");
        await Expect(rejected.GetByText("Ningún comprobante rechazado")).ToBeVisibleAsync();
    }

    private ILocator Row(string encf) =>
        Page.GetByRole(AriaRole.Region, new() { Name = "Últimos comprobantes" }).GetByRole(AriaRole.Row, new() { NameRegex = new($"^{encf}") }); // not the row that says "reemplaza" it

    /// <summary>From any back-office page (Resumen first, so a second visit loads the page again).</summary>
    private async Task OpenEcfAsync()
    {
        await Page.GetByRole(AriaRole.Link, new() { Name = "Resumen", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Comprobantes e-CF", Exact = true }).ClickAsync();
        await Expect(Till.Heading("Comprobantes electrónicos")).ToBeVisibleAsync();
    }

    private async Task<List<JsonElement>> DocumentsAsync() =>
        [.. (await Ok(await store.Store.Admin.GetAsync("/api/fiscal/documents?last=50"))).EnumerateArray()];

    /// <summary>The queue works in the background (every second here): waits up to 60 s for the server to record the DGII's answer.</summary>
    private async Task StatusAsync(string encf, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = (await DocumentsAsync()).SingleOrDefault(d => d.GetProperty("eNcf").GetString() == encf) is { ValueKind: JsonValueKind.Object } d
                ? d.GetProperty("status").GetString() : null;
            if (last == status) return;
            await Task.Delay(250); // polling the server's state: the page has nothing to wait on
        }
        var asked = store.Dgii.Requests();
        throw new ShouldAssertException($"{encf} is {last ?? "missing"}, not {status}, after 60 s. The DGII was asked: {string.Join(", ", asked)}\n"
            + string.Join("\n", await DocumentsAsync()));
    }
}
