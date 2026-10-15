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
        async Task<int> Import(string text)
        {
            var r = await admin.PostAsync("/api/fiscal/taxpayer-registry", new ByteArrayContent(Encoding.Latin1.GetBytes(text)));
            r.EnsureSuccessStatusCode();
            return (await r.Json()).GetProperty("taxpayers").GetInt32();
        }

        (await Import(RegistryFile)).ShouldBe(2);
        var sol = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/taxpayers/131-24567-8");
        sol.GetProperty("name").GetString().ShouldBe("COLMADO EL SOL SRL");
        var juan = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/taxpayers/001-1234567-8");
        juan.GetProperty("name").GetString().ShouldBe("JUAN PÉREZ"); // ANSI accents survive
        juan.GetProperty("isActive").GetBoolean().ShouldBeFalse();

        // A UTF-8 file is detected and read as such (accents intact either way).
        var utf8 = await admin.PostAsync("/api/fiscal/taxpayer-registry", new ByteArrayContent(Encoding.UTF8.GetBytes("40212345678|ANA LÓPEZ||||||||ACTIVO|\r\n")));
        (await utf8.Json()).GetProperty("taxpayers").GetInt32().ShouldBe(1);
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/taxpayers/40212345678")).GetProperty("name").GetString().ShouldBe("ANA LÓPEZ");
        (await admin.GetAsync("/api/fiscal/taxpayers/131245678")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.GetAsync("/api/fiscal/taxpayers/40212345678")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cashier.PostAsync("/api/fiscal/taxpayer-registry", new StringContent(RegistryFile))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
