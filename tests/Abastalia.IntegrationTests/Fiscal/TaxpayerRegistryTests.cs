using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;
using Abastalia.Fiscal.Features;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Fiscal;

/// <summary>A store whose registry files are small test ones (the real one has ~700k taxpayers).</summary>
public sealed class SmallRegistryApp : AbastaliaApp
{
    protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string> { ["Fiscal:RegistryMinimumTaxpayers"] = "1" };
}

/// <summary>DGII RNC registry (TK-053). Own app: the import replaces the whole table.</summary>
public sealed class TaxpayerRegistryTests(SmallRegistryApp app) : IClassFixture<SmallRegistryApp>
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
        // R12: who neither adds customers nor buys nor manages fiscal does not spend the store's DGII lookups.
        (await app.Client(await app.UserAsync(Roles.Accountant)).GetAsync("/api/fiscal/taxpayers/40212345678")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
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

/// <summary>A store that rejects registry files under 50 taxpayers (the real minimum, 100,000, would make the test slow).</summary>
public sealed class RegistryGuardApp : AbastaliaApp
{
    protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string> { ["Fiscal:RegistryMinimumTaxpayers"] = "50" };
}

/// <summary>R8 #8: a wrong file never empties the registry, and the .zip as DGII publishes it is accepted. Own app.</summary>
public sealed class TaxpayerRegistryGuardTests(RegistryGuardApp app) : IClassFixture<RegistryGuardApp>
{
    private static async Task<JsonElement> ImportAsync(HttpClient admin, byte[] file)
    {
        (await admin.PostAsync("/api/fiscal/taxpayer-registry", new ByteArrayContent(file))).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonElement status;
        while ((status = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/taxpayer-registry/import")).GetProperty("running").GetBoolean()) await Task.Delay(50);
        return status;
    }

    [Fact]
    public async Task A_file_too_small_to_be_the_registry_leaves_the_previous_one_and_the_DGII_zip_is_read()
    {
        var admin = app.Client(await app.AdminAsync());
        (await (await admin.GetAsync("/api/fiscal/taxpayers/131245678")).ErrorCode()).ShouldBe("Fiscal.RegistryNotLoaded"); // TK-239: nothing loaded yet
        var text = new StringBuilder("RNC|RAZÓN SOCIAL\r\n");
        for (var i = 0; i < 60; i++) text.Append($"{100_000_000 + i}|CONTRIBUYENTE {i}||||||||ACTIVO|\r\n");
        using var zipped = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipped, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        await using (var entry = zip.CreateEntry("TMP/DGII_RNC.TXT").Open())
            await entry.WriteAsync(Encoding.Latin1.GetBytes(text.ToString()));

        var loaded = await ImportAsync(admin, zipped.ToArray());
        (loaded.GetProperty("error").ValueKind, loaded.GetProperty("taxpayers").GetInt32()).ShouldBe((JsonValueKind.Null, 60));
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/taxpayers/100000007")).GetProperty("name").GetString().ShouldBe("CONTRIBUYENTE 7");

        // Some other file: rejected before the swap.
        var wrong = await ImportAsync(admin, Encoding.Latin1.GetBytes("131245678|COLMADO EL SOL SRL||||||||ACTIVO|\r\n"));
        wrong.GetProperty("error").GetString().ShouldStartWith("El archivo no parece el padrón de la DGII");
        (await admin.GetAsync("/api/fiscal/taxpayers/100000007")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await admin.GetAsync("/api/fiscal/taxpayers/131245678")).ErrorCode()).ShouldBe("Fiscal.RncNotFound");
    }
}
