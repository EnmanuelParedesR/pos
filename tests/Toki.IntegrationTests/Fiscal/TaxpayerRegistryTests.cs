using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;
using Toki.Fiscal.Features;
using Toki.IntegrationTests.Infra;

namespace Toki.IntegrationTests.Fiscal;

/// <summary>DGII RNC registry (TK-053). Own app: the import replaces the whole table.</summary>
public sealed class TaxpayerRegistryTests(TokiApp app) : IClassFixture<TokiApp>
{
    private const string RegistryFile =
        "RNC|RAZÓN SOCIAL|NOMBRE COMERCIAL|ACTIVIDAD|||||FECHA|ESTADO|REGIMEN\r\n" +
        "131245678|COLMADO EL SOL SRL|EL SOL|VENTA AL POR MENOR|||||01/02/2010|ACTIVO|NORMAL\r\n" +
        "00112345678|JUAN PÉREZ||||||||SUSPENDIDO|\r\n" +
        "131245678|DUPLICADO|||||||||\r\n" +
        "123|MAL||||||||ACTIVO|\r\n";

    [Fact]
    public void A_registry_line_is_read_and_bad_ones_are_skipped()
    {
        var c = TaxpayerRegistry.Read("131-24567-8|COLMADO EL SOL SRL|EL SOL|x|||||f|ACTIVO|NORMAL")!;
        (c.Rnc, c.Name, c.TradeName, c.IsActive).ShouldBe(("131245678", "COLMADO EL SOL SRL", "EL SOL", true));
        TaxpayerRegistry.Read("RNC|RAZÓN SOCIAL").ShouldBeNull();
        TaxpayerRegistry.Read("").ShouldBeNull();
        TaxpayerRegistry.Read("131245678|").ShouldBeNull();
    }

    [Fact]
    public async Task Importing_the_registry_replaces_the_previous_one_and_it_is_queried_by_RNC_or_cedula()
    {
        var admin = app.Client(await app.AdminAsync());
        // TK-162: the upload answers 202 at once and the import runs in the background; the admin screen polls its status.
        async Task<int> Import(byte[] file)
        {
            var r = await admin.PostAsync("/api/fiscal/taxpayer-registry", new ByteArrayContent(file));
            r.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            JsonElement status;
            while ((status = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/taxpayer-registry/import")).GetProperty("running").GetBoolean())
            {
                DateTime.UtcNow.ShouldBeLessThan(deadline);
                await Task.Delay(50);
            }
            status.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
            status.GetProperty("percent").GetInt32().ShouldBe(100);
            return status.GetProperty("taxpayers").GetInt32();
        }

        (await Import(Encoding.Latin1.GetBytes(RegistryFile))).ShouldBe(2);
        var sol = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/taxpayers/131-24567-8");
        sol.GetProperty("name").GetString().ShouldBe("COLMADO EL SOL SRL");
        var juan = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/taxpayers/001-1234567-8");
        juan.GetProperty("name").GetString().ShouldBe("JUAN PÉREZ"); // ANSI accents survive
        juan.GetProperty("isActive").GetBoolean().ShouldBeFalse();

        // A UTF-8 file is detected and read as such (accents intact either way).
        (await Import(Encoding.UTF8.GetBytes("40212345678|ANA LÓPEZ||||||||ACTIVO|\r\n"))).ShouldBe(1);
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/taxpayers/40212345678")).GetProperty("name").GetString().ShouldBe("ANA LÓPEZ");
        (await admin.GetAsync("/api/fiscal/taxpayers/131245678")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.GetAsync("/api/fiscal/taxpayers/40212345678")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cashier.PostAsync("/api/fiscal/taxpayer-registry", new StringContent(RegistryFile))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.GetAsync("/api/fiscal/taxpayer-registry/import")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_large_registry_is_imported_in_batches_and_only_one_import_runs_at_a_time()
    {
        // More than one batch (5000), so the progress and the batch boundary are exercised.
        var file = new StringBuilder("RNC|RAZÓN SOCIAL\r\n");
        for (var i = 0; i < 12_345; i++) file.Append($"{100_000_000 + i}|CONTRIBUYENTE {i}||||||||ACTIVO|\r\n");
        var admin = app.Client(await app.AdminAsync());
        var first = await admin.PostAsync("/api/fiscal/taxpayer-registry", new ByteArrayContent(Encoding.Latin1.GetBytes(file.ToString())));
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var second = await admin.PostAsync("/api/fiscal/taxpayer-registry", new ByteArrayContent(Encoding.Latin1.GetBytes(RegistryFile)));
        JsonElement status;
        while ((status = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/taxpayer-registry/import")).GetProperty("running").GetBoolean()) await Task.Delay(50);
        // The second upload either found the first running (409) or ran after it finished (202): never two at once.
        if (second.StatusCode == HttpStatusCode.Conflict) (await second.ErrorCode()).ShouldBe("Fiscal.RegistryImportRunning");
        else second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        status.GetProperty("taxpayers").GetInt32().ShouldBe(second.StatusCode == HttpStatusCode.Conflict ? 12_345 : 2);
    }
}
