using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Messaging;
using Toki.Fiscal;
using Toki.Fiscal.Domain;
using Toki.IntegrationTests.Infra;
using static Toki.IntegrationTests.Infra.Store;

namespace Toki.IntegrationTests.Fiscal;

/// <summary>TK-205 (void a signed e-CF that never left), TK-206 (the on-account charge follows a replacement) and TK-176 (a renewed certificate
/// signs from the next sale), against <see cref="FakeDgii"/>. Numbers are read from the responses: the tests of this class share one store.</summary>
public sealed class EcfAdminTests(DgiiTokiApp app) : IClassFixture<DgiiTokiApp>
{
    private static string Encf(JsonElement sale) => sale.GetProperty("eNcf").GetString()!;

    [Fact]
    public async Task A_signed_e_CF_that_never_left_is_voided_with_an_ANECF_and_its_sale_can_take_a_replacement()
    {
        var admin = await app.SetUpAsync();
        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 50);

        // Signed while the DGII was down: one failed send, so it may have arrived; with the DGII still down that cannot be ruled out.
        app.Dgii.Down = true;
        var sale = await cashier.SellAsync(soda, 59m);
        await app.InScopeAsync(sp => sp.GetRequiredService<EcfSubmissionQueue>().ProcessAsync(CancellationToken.None));
        var doc = await app.DocumentAsync(Encf(sale));
        doc.Attempts.ShouldBe(1);
        (await (await admin.PostAsync($"/api/fiscal/documents/{doc.Id}/void", null)).ErrorCode()).ShouldBe("Fiscal.DgiiUnreachable");

        // The DGII is back and has nothing for that e-NCF: the same ANECF of the ranges, for that single number.
        app.Dgii.Down = false;
        var voided = await admin.PostAsync($"/api/fiscal/documents/{doc.Id}/void", null);
        voided.StatusCode.ShouldBe(HttpStatusCode.Created);
        var v = await voided.Json();
        (v.GetProperty("from").GetString(), v.GetProperty("to").GetString(), v.GetProperty("count").GetInt64()).ShouldBe((doc.ENcf, doc.ENcf, 1));
        (await app.DocumentAsync(doc.ENcf)).Status.ShouldBe(SubmissionStatus.Voided);
        (await (await admin.PostAsync($"/api/fiscal/documents/{doc.Id}/void", null)).ErrorCode()).ShouldBe("Fiscal.AlreadySent");

        await app.DrainAsync();
        var anecf = app.Dgii.To("/anulacionrangos/api/operaciones/anularrango").Select(r => XDocument.Parse(r.Xml!))
            .Single(x => x.Descendants("SecuenciaeNCFDesde").Single().Value == doc.ENcf);
        EcfXml.Validate(anecf, EcfXml.AnecfSchema).ShouldBeEmpty();
        (anecf.Descendants("SecuenciaeNCFDesde").Single().Value, anecf.Descendants("SecuenciaeNCFHasta").Single().Value).ShouldBe((doc.ENcf, doc.ENcf));
        app.Dgii.To("/recepcionfc/api/recepcion/ecf").ShouldNotContain(r => r.Xml!.Contains(doc.ENcf)); // the queue never sends a voided e-CF
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/voids")).EnumerateArray().Single(x => x.GetProperty("from").GetString() == doc.ENcf)
            .GetProperty("status").GetString().ShouldBe("Accepted");

        // The sale stands: "Reemplazar" gives it a new e-CF with the same data, as for a rejection.
        var replace = await admin.PostAsync($"/api/fiscal/documents/{doc.Id}/replace", null);
        replace.StatusCode.ShouldBe(HttpStatusCode.Created);
        var replacement = (await replace.Json()).GetProperty("eNcf").GetString()!;
        replacement.ShouldNotBe(doc.ENcf);
        await app.InScopeAsync(sp => sp.GetRequiredService<OutboxProcessor>().ProcessAsync(CancellationToken.None));
        (await admin.GetFromJsonAsync<JsonElement>($"/api/sales/{sale.GetProperty("saleId").GetGuid()}/receipt")).GetProperty("eNcf").GetString().ShouldBe(replacement);

        // What reached the DGII is corrected with a credit note, never voided.
        await app.DrainAsync();
        var accepted = await app.DocumentAsync(replacement);
        accepted.Status.ShouldBe(SubmissionStatus.Accepted);
        (await (await admin.PostAsync($"/api/fiscal/documents/{accepted.Id}/void", null)).ErrorCode()).ShouldBe("Fiscal.AlreadySent");
    }

    [Fact]
    public async Task A_send_that_timed_out_but_arrived_is_not_voided()
    {
        var admin = await app.SetUpAsync();
        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 50);
        await app.DrainAsync(); // only this sale is left for the lost answer
        var credit = await cashier.SellAsync(soda, 59m, "FiscalCredit31", "101672919");

        app.Dgii.LoseNextAnswer = true;
        await app.InScopeAsync(sp => sp.GetRequiredService<EcfSubmissionQueue>().ProcessAsync(CancellationToken.None));
        var doc = await app.DocumentAsync(Encf(credit));
        (doc.Status, doc.Attempts).ShouldBe((SubmissionStatus.Pending, 1));
        (await (await admin.PostAsync($"/api/fiscal/documents/{doc.Id}/void", null)).ErrorCode()).ShouldBe("Fiscal.AlreadySent");
        (await app.DocumentAsync(doc.ENcf)).Status.ShouldBe(SubmissionStatus.Pending);
    }

    [Fact]
    public async Task The_on_account_charge_and_the_statement_show_the_replacement_e_NCF()
    {
        var admin = await app.SetUpAsync();
        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var rice = await admin.ProductAsync(await admin.CategoryAsync(), "Arroz", 100m, rate: "Exempt", stock: 50);
        var customer = (await (await admin.PostAsJsonAsync("/api/customers", new { Name = "Colmado El Sol", TaxId = "101672919", FiscalCredit = true, CreditLimit = 5000m }))
            .Json()).GetProperty("id").GetGuid();
        var r = await cashier.SellAsync(new
        {
            SaleId = Guid.CreateVersion7(), ShiftId = cashier.ShiftId, CustomerId = customer, DocumentType = "FiscalCredit31", Lines = new[] { Line(rice, 1) },
            Payments = new[] { new { Method = "OnAccount", Amount = 100m } }, ExpectedTotal = 100m,
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        var rejected = Encf(await r.Json());

        app.Dgii.Result = encf => encf == rejected ? (2, true, "El RNC del comprador no corresponde") : (1, true, null);
        await app.DrainAsync();
        var replace = await admin.PostAsync($"/api/fiscal/documents/{(await app.DocumentAsync(rejected)).Id}/replace", null);
        replace.StatusCode.ShouldBe(HttpStatusCode.Created);
        var replacement = (await replace.Json()).GetProperty("eNcf").GetString();
        await app.InScopeAsync(sp => sp.GetRequiredService<OutboxProcessor>().ProcessAsync(CancellationToken.None));

        var statement = await admin.GetFromJsonAsync<JsonElement>($"/api/customers/{customer}/statement");
        var charge = statement.GetProperty("lines").EnumerateArray().Single();
        (charge.GetProperty("type").GetString(), charge.GetProperty("document").GetString(), charge.GetProperty("charge").GetDecimal()).ShouldBe(("Fiado", replacement, 100m));
        statement.GetProperty("debt").GetDecimal().ShouldBe(100m);
    }

    [Fact]
    public async Task A_renewed_certificate_signs_from_the_next_sale()
    {
        var admin = await app.SetUpAsync();
        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 50);
        var before = (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/certificate"));
        (before.GetProperty("installed").GetBoolean(), before.GetProperty("inUse").GetBoolean(), before.GetProperty("store").GetString()).ShouldBe((true, true, "File"));

        using var renewed = TestCertificates.Create(DateTimeOffset.UtcNow.AddYears(2));
        var import = await admin.PostAsJsonAsync("/api/fiscal/certificate", new { File = Convert.ToBase64String(renewed.Export(X509ContentType.Pkcs12, "nueva")), Password = "nueva" });
        import.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await import.Json()).GetProperty("thumbprint").GetString().ShouldBe(renewed.Thumbprint);

        var sale = await cashier.SellAsync(soda, 59m);
        var signed = XDocument.Parse(EcfSubmissionQueue.Unzip((await app.DocumentAsync(Encf(sale))).SignedXml));
        signed.Descendants().Single(e => e.Name.LocalName == "X509Certificate").Value.ShouldBe(Convert.ToBase64String(renewed.RawData));
    }
}

/// <summary>TK-176 in a store still on the simulated signer: the certificate is imported and kept ready, the owner sees who it belongs to and
/// when it expires, and is warned 30 days before. Own app (Testing: the key goes to the keys folder, encrypted).</summary>
public sealed class EcfCertificateTests(TokiApp app) : IClassFixture<TokiApp>
{
    [Fact]
    public async Task The_certificate_is_imported_once_with_its_password_and_warns_30_days_before_it_expires()
    {
        var admin = app.Client(await app.AdminAsync());
        var none = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/certificate");
        (none.GetProperty("installed").GetBoolean(), none.GetProperty("inUse").GetBoolean()).ShouldBe((false, false));

        using var expiring = TestCertificates.Create(app.Clock.GetUtcNow().AddDays(20));
        var pfx = Convert.ToBase64String(expiring.Export(X509ContentType.Pkcs12, "clave"));
        (await (await admin.PostAsJsonAsync("/api/fiscal/certificate", new { File = pfx, Password = "otra" })).ErrorCode()).ShouldBe("Fiscal.CertificateUnreadable");
        (await (await admin.PostAsJsonAsync("/api/fiscal/certificate", new { File = "no es base64", Password = "clave" })).ErrorCode()).ShouldBe("Fiscal.CertificateUnreadable");
        using var expired = TestCertificates.Create(DateTimeOffset.UtcNow.AddDays(-1));
        (await (await admin.PostAsJsonAsync("/api/fiscal/certificate", new { File = Convert.ToBase64String(expired.Export(X509ContentType.Pkcs12, "x")), Password = "x" }))
            .ErrorCode()).ShouldBe("Fiscal.CertificateExpired");
        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.PostAsJsonAsync("/api/fiscal/certificate", new { File = pfx, Password = "clave" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var imported = await admin.PostAsJsonAsync("/api/fiscal/certificate", new { File = pfx, Password = "clave" });
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
        var c = await imported.Json();
        (c.GetProperty("installed").GetBoolean(), c.GetProperty("inUse").GetBoolean(), c.GetProperty("subject").GetString(), c.GetProperty("taxId").GetString())
            .ShouldBe((true, false, "Firmante de Prueba", "00112345678"));
        (c.GetProperty("store").GetString(), c.GetProperty("expiresSoon").GetBoolean()).ShouldBe(("KeysFolder", true));
        c.GetProperty("daysLeft").GetInt32().ShouldBeInRange(19, 20); // the certificate keeps whole seconds

        // The register's status bar reads the expiry with the queue.
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/queue")).GetProperty("certificateExpiresUtc").GetDateTime().ShouldBe(expiring.NotAfter.ToUniversalTime());

        // After a restart it is still there (the keys folder holds it encrypted); the password is in neither file.
        var reloaded = await app.InScopeAsync(sp => Task.FromResult(ActivatorUtilities.CreateInstance<EcfCertificate>(sp)));
        reloaded.Current!.Thumbprint.ShouldBe(expiring.Thumbprint);
        reloaded.Current.HasPrivateKey.ShouldBeTrue();
        var folder = app.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Toki:KeysFolder"]!;
        File.ReadAllText(Path.Combine(folder, EcfCertificate.FileName)).ShouldContain(expiring.Thumbprint);
        File.ReadAllText(Path.Combine(folder, EcfCertificate.FileName)).ShouldNotContain("clave");
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }
}

internal static class TestCertificates
{
    /// <summary>A self-signed signing certificate shaped like the DGII ones: the holder's cédula in SERIALNUMBER.</summary>
    public static X509Certificate2 Create(DateTimeOffset notAfter, DateTimeOffset? notBefore = null, string serial = "IDCDO-00112345678")
    {
        using var rsa = RSA.Create(2048);
        return new CertificateRequest($"CN=Firmante de Prueba, SERIALNUMBER={serial}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(notBefore ?? (notAfter < DateTimeOffset.UtcNow ? notAfter.AddYears(-1) : DateTimeOffset.UtcNow.AddDays(-1)), notAfter);
    }
}
