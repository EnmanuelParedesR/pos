using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.IntegrationTests.Infra;
using Abastalia.SharedKernel;
using Abastalia.Settings.Contracts;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Fiscal;

/// <summary>A store that signs with a certificate (none imported yet) and the simulated DGII: what TK-157 starts from.</summary>
public sealed class NoCertificateAbastaliaApp : AbastaliaApp
{
    protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string> { ["Fiscal:Signer"] = "Certificate" };
}

/// <summary>A store in the certification environment (CerteCF) against <see cref="FakeDgii"/>: TK-155.</summary>
public sealed class CerteCfAbastaliaApp : AbastaliaApp
{
    private static readonly Lazy<string> Pfx = new(() =>
    {
        using var cert = TestCertificates.Create(DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(Path.GetTempPath(), $"abastalia_certecf_{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, "prueba"));
        return path;
    });

    protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
    {
        ["Fiscal:Signer"] = "Certificate", ["Fiscal:Certificate:Path"] = Pfx.Value, ["Fiscal:Certificate:Password"] = "prueba",
        ["Fiscal:Provider"] = "Dgii", ["Fiscal:Dgii:Environment"] = "CerteCF",
    };
}

internal static class OperationsStore
{
    /// <summary>RNC and address (the real signer validates the XML, which needs the address).</summary>
    public static async Task<HttpClient> WithAddressAsync(this AbastaliaApp app)
    {
        var admin = app.Client(await app.AdminAsync());
        var business = await admin.GetFromJsonAsync<JsonElement>("/api/settings/business");
        (await admin.PutAsJsonAsync("/api/settings/business", new
        {
            Name = "Carnicería de prueba", Rnc = "131245678", Address = "Av. Duarte 25, Santo Domingo",
            DiscountCapPercent = business.GetProperty("discountCapPercent").GetDecimal(),
        })).EnsureSuccessStatusCode();
        return admin;
    }

    public static async Task<JsonElement> HealthAsync(this HttpClient admin) => await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/health");

    public static IEnumerable<(string Level, string Code, string Title)> Items(this JsonElement health) =>
        health.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("level").GetString()!, i.GetProperty("code").GetString()!, i.GetProperty("title").GetString()!));
}

/// <summary>TK-157: the store cannot sign → the owner starts the contingency → the register sells with paper NCF of series B → the certificate is
/// back (from then on, e-CF again) → the contingency ends → every B receipt is replaced by an e-CF with CodigoModificacion 4. The 15 and 30 days
/// are counted with the fake clock.</summary>
public sealed class PaperContingencyTests(NoCertificateAbastaliaApp app) : IClassFixture<NoCertificateAbastaliaApp>
{
    [Fact]
    public async Task Series_B_is_used_only_while_the_store_cannot_sign_and_is_regularized_with_e_CF_after()
    {
        var cashier = await app.CashierWithShiftAsync();
        var admin = await app.WithAddressAsync();
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 50);
        object Sale() => Store.Sale(cashier.ShiftId, 59m, 59m, Line(soda, 1));

        // No certificate and no contingency: the sale says why and what to do.
        (await (await cashier.SellAsync(Sale())).ErrorCode()).ShouldBe("Fiscal.CannotSign");
        (await cashier.Client.PostAsJsonAsync("/api/fiscal/contingency/start", new { Reason = "Sin certificado" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var started = await admin.PostAsJsonAsync("/api/fiscal/contingency/start", new { Reason = "Certificado no entregado por la entidad" });
        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await admin.PostAsJsonAsync("/api/fiscal/contingency/start", new { Reason = "otra vez" })).ErrorCode()).ShouldBe("Fiscal.ContingencyActive");

        // Contingency without B ranges, then with one.
        (await (await cashier.SellAsync(Sale())).ErrorCode()).ShouldBe("Fiscal.NoPaperSequence");
        (await admin.PostAsJsonAsync("/api/fiscal/contingency/sequences", new { Type = "Consumer32", From = 1, To = 50 })).StatusCode.ShouldBe(HttpStatusCode.Created);
        var paper = await cashier.SellAsync(Sale());
        paper.StatusCode.ShouldBe(HttpStatusCode.Created);
        var sale = await paper.Json();
        sale.GetProperty("eNcf").GetString().ShouldBe("B0200000001");
        var receipt = await admin.GetFromJsonAsync<JsonElement>($"/api/sales/{sale.GetProperty("saleId").GetGuid()}/receipt");
        (receipt.GetProperty("documentType").GetString(), receipt.GetProperty("securityCode").GetString(), receipt.GetProperty("qrUrl").GetString())
            .ShouldBe(("Factura de Consumo", "", ""));

        // The register (any signed-in user) sees it; the owner sees the days counted.
        var status = await cashier.Client.GetFromJsonAsync<JsonElement>("/api/fiscal/contingency");
        (status.GetProperty("active").GetBoolean(), status.GetProperty("day").GetInt32(), status.GetProperty("canSign").GetBoolean()).ShouldBe((true, 1, false));
        (await admin.HealthAsync()).Items().ShouldContain(i => i.Code == "contingency" && i.Level == "error" && i.Title.Contains("día 1 de 15"));
        (await admin.HealthAsync()).Items().ShouldContain(i => i.Code == "certificate" && i.Level == "error");
        (await (await admin.PostAsync("/api/fiscal/contingency/regularize", null)).ErrorCode()).ShouldBe("Fiscal.ContingencyStillActive");

        // The certificate is back: from the next sale, e-CF again, even before the contingency is ended (never B while it can sign).
        using var cert = TestCertificates.Create(app.Clock.GetUtcNow().AddYears(1), app.Clock.GetUtcNow().AddDays(-1));
        (await admin.PostAsJsonAsync("/api/fiscal/certificate", new { File = Convert.ToBase64String(cert.Export(X509ContentType.Pkcs12, "c")), Password = "c" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await cashier.SellAsync(Sale())).Json()).GetProperty("eNcf").GetString()!.ShouldStartWith("E32");

        // Past the 15 days the contingency is flagged; sessions last 12 hours, so the owner signs in again after each jump.
        async Task<HttpClient> Owner() => app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, (await app.AdminAsync()).Device));
        app.Clock.Advance(TimeSpan.FromDays(16));
        admin = await Owner();
        (await admin.HealthAsync()).Items().ShouldContain(i => i.Code == "contingency" && i.Title.Contains("pasó el límite"));
        (await admin.PostAsync("/api/fiscal/contingency/end", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // 30 days to regularize: amber, then red.
        (await admin.HealthAsync()).Items().ShouldContain(i => i.Code == "regularize" && i.Level == "warning");
        app.Clock.Advance(TimeSpan.FromDays(31));
        admin = await Owner();
        (await admin.HealthAsync()).Items().ShouldContain(i => i.Code == "regularize" && i.Level == "error");

        var regularized = await admin.PostAsync("/api/fiscal/contingency/regularize", null);
        regularized.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await regularized.Json()).GetProperty("regularized").GetInt32().ShouldBe(1);
        var replacement = await app.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<FiscalDbContext>().Documents.Single(d => d.ReplacesENcf == "B0200000001")));
        var xml = XDocument.Parse(EcfSubmissionQueue.Unzip(replacement.SignedXml));
        EcfXml.Validate(xml, EcfType.Consumer32).ShouldBeEmpty();
        var reference = xml.Descendants("InformacionReferencia").Single();
        (reference.Element("NCFModificado")!.Value, reference.Element("CodigoModificacion")!.Value).ShouldBe(("B0200000001", "4"));
        (replacement.Total, replacement.ReferenceId).ShouldBe((59m, sale.GetProperty("saleId").GetGuid()));
        (await admin.HealthAsync()).Items().ShouldNotContain(i => i.Code == "regularize" || i.Code == "contingency");
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/contingency")).GetProperty("regularized").GetInt32().ShouldBe(1);
        (await app.VerifyAuditAsync()).ShouldBeNull();
    }
}

/// <summary>R9 #11 and #18: the 15 days, the 607 with B receipts pending, and B03/B04 left for the accountant. Own app: it moves the clock.</summary>
public sealed class PaperContingencyLimitsTests(NoCertificateAbastaliaApp app) : IClassFixture<NoCertificateAbastaliaApp>
{
    [Fact]
    public async Task Series_B_stops_after_15_days_holds_the_607_and_credit_notes_are_not_regularized_automatically()
    {
        var cashier = await app.CashierWithShiftAsync();
        var admin = await app.WithAddressAsync();
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 50);
        object Sale() => Store.Sale(cashier.ShiftId, 59m, 59m, Line(soda, 1));
        (await admin.PostAsJsonAsync("/api/fiscal/contingency/start", new { Reason = "Certificado vencido" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/fiscal/contingency/sequences", new { Type = "Consumer32", From = 1, To = 50 })).EnsureSuccessStatusCode();
        var sale = await (await cashier.SellAsync(Sale())).Json();
        // A B04 that corrects the B02 (a return during the contingency).
        await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            var contingency = await db.Set<EcfContingency>().Select(c => c.Id).SingleAsync();
            var note = new DocumentToIssue(EcfType.CreditNote34, Guid.NewGuid(), null, null, [], 0m, 59m, "B0200000001", ModificationCode.AmountCorrection);
            db.Set<PaperDocument>().Add(new PaperDocument
            {
                Id = Guid.CreateVersion7(), Ncf = "B0400000001", Type = EcfType.CreditNote34, ReferenceId = note.ReferenceId, ContingencyId = contingency,
                IssuedUtc = app.Clock.GetUtcNow().UtcDateTime, Total = 59m, Data = JsonSerializer.Serialize(note),
            });
            return await db.SaveChangesAsync();
        });

        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        var blocked = await admin.GetAsync($"/api/reports/dgii/607?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        (await blocked.ErrorCode()).ShouldBe("Reports.DocumentsToReplace");

        // Day 16: no more B receipts. Sessions last 12 hours, so the cashier and the owner sign in again.
        app.Clock.Advance(TimeSpan.FromDays(15));
        cashier = cashier with { Client = app.Client(await app.SignInAsync(await CodeOfAsync(cashier.Session.UserId), "4321", cashier.Session.Device)) };
        admin = app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, (await app.AdminAsync()).Device));
        (await (await cashier.SellAsync(Sale())).ErrorCode()).ShouldBe("Fiscal.ContingencyOverLimit");

        using var cert = TestCertificates.Create(app.Clock.GetUtcNow().AddYears(1), app.Clock.GetUtcNow().AddDays(-1));
        (await admin.PostAsJsonAsync("/api/fiscal/certificate", new { File = Convert.ToBase64String(cert.Export(X509ContentType.Pkcs12, "c")), Password = "c" }))
            .EnsureSuccessStatusCode();
        (await admin.PostAsync("/api/fiscal/contingency/end", null)).EnsureSuccessStatusCode();
        var regularized = await (await admin.PostAsync("/api/fiscal/contingency/regularize", null)).Json();
        (regularized.GetProperty("regularized").GetInt32(), regularized.GetProperty("manual").GetInt32()).ShouldBe((1, 1));
        (await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Set<PaperDocument>().SingleAsync(d => d.Ncf == "B0400000001")))
            .RegularizedByENcf.ShouldBeNull();
        sale.GetProperty("eNcf").GetString().ShouldBe("B0200000001");
    }

    private Task<string> CodeOfAsync(Guid userId) => app.InScopeAsync(sp =>
        sp.GetRequiredService<Abastalia.Identity.Persistence.IdentityDbContext>().Users.Where(u => u.Id == userId).Select(u => u.Code).SingleAsync());
}

/// <summary>TK-156 with the DGII simulated: the queue's age, the last contact and the on-demand DGII check.</summary>
public sealed class EcfHealthTests(DgiiAbastaliaApp app) : IClassFixture<DgiiAbastaliaApp>
{
    [Fact]
    public async Task The_health_list_follows_the_queue_and_the_DGII_check_says_whether_it_answers()
    {
        var admin = await app.SetUpAsync();
        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 50);
        await app.DrainAsync();

        app.Dgii.Down = true;
        await cashier.SellAsync(soda, 59m);
        await app.DrainAsync();
        var health = await admin.HealthAsync();
        health.Items().ShouldContain(i => i.Code == "dgii" && i.Level == "warning");
        (await cashier.Client.GetAsync("/api/fiscal/health")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Sessions last 12 hours: the owner signs in again after each jump.
        async Task<HttpClient> Owner() => app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, (await app.AdminAsync()).Device));
        app.Clock.Advance(TimeSpan.FromHours(25));
        admin = await Owner();
        (await admin.HealthAsync()).Items().ShouldContain(i => i.Code == "queue" && i.Level == "warning");
        app.Clock.Advance(TimeSpan.FromHours(36));
        admin = await Owner();
        (await admin.HealthAsync()).Items().ShouldContain(i => i.Code == "queue" && i.Level == "error");
        (await (await admin.PostAsync("/api/fiscal/dgii-status", null)).Json()).GetProperty("state").GetString().ShouldBe("Down");

        app.Dgii.Down = false;
        (await (await admin.PostAsync("/api/fiscal/dgii-status", null)).Json()).GetProperty("state").GetString().ShouldBe("Up");
        await app.DrainAsync();
        health = await admin.HealthAsync();
        health.Items().ShouldNotContain(i => i.Code == "queue" || i.Code == "dgii");
        health.GetProperty("lastDgiiContactUtc").GetDateTime().ShouldBeGreaterThan(DateTime.UtcNow.AddHours(50));
    }
}

/// <summary>TK-155 in CerteCF: the test set (an Excel or CSV with one column per XML field) is generated in the XSD order, signed and sent; the
/// assistant keeps the owner's steps; "Firmar archivo de la DGII" signs the portal's XML and refuses fiscal documents.</summary>
public sealed class CertificationTests(CerteCfAbastaliaApp app) : IClassFixture<CerteCfAbastaliaApp>
{
    private static readonly FiscalLine[] Lines =
    [
        new("Res para guisar", 2, 210m, 20m, ItbisRate.General18, Itbis.Included(400m, ItbisRate.General18), 400m),
        new("Arroz selecto 1 lb", 1, 45m, 0m, ItbisRate.Exempt, 0m, 45m),
    ];

    /// <summary>A row of the set as the DGII Excel has it: one column per leaf, numbered within items and payment forms.</summary>
    private static Dictionary<string, string> Flatten(XDocument xml)
    {
        var row = new Dictionary<string, string>();
        foreach (var leaf in xml.Descendants().Where(e => !e.HasElements))
        {
            var index = string.Concat(leaf.Ancestors().Reverse().Where(a => a.Name.LocalName is "Item" or "FormaDePago")
                .Select(a => $"[{a.ElementsBeforeSelf(a.Name).Count() + 1}]"));
            row[leaf.Name.LocalName + index] = leaf.Value;
        }
        return row;
    }

    private static string Csv(IReadOnlyList<Dictionary<string, string>> rows)
    {
        var columns = rows.SelectMany(r => r.Keys).Distinct().ToList();
        var csv = new StringBuilder(string.Join(';', columns)).Append('\n');
        foreach (var r in rows) csv.Append(string.Join(';', columns.Select(c => r.TryGetValue(c, out var v) ? v : "#e"))).Append('\n');
        return csv.ToString();
    }

    private static XDocument Ecf(EcfType type, string encf, string? buyer) =>
        EcfXml.Build(new DocumentToIssue(type, Guid.NewGuid(), buyer, buyer is null ? null : "Colmado El Sol SRL", Lines, Lines.Sum(l => l.Itbis), 445m,
                Payments: [new FiscalPayment(EcfPaymentForm.Cash, 400m), new FiscalPayment(EcfPaymentForm.Card, 45m)]), encf,
            new BusinessProfile("Carnicería de prueba", "131245678", "Av. Duarte 25", null, null, 0m), DateTime.UtcNow,
            type == EcfType.FiscalCredit31 ? new DateOnly(2028, 12, 31) : null, null);

    [Fact]
    public async Task The_test_set_is_generated_in_schema_order_signed_and_sent_to_CerteCF()
    {
        var admin = await app.WithAddressAsync();
        var credit = Ecf(EcfType.FiscalCredit31, "E310000000001", "101672919");
        var consumer = Ecf(EcfType.Consumer32, "E320000000001", null);
        var file = Convert.ToBase64String(Encoding.UTF8.GetBytes(Csv([Flatten(credit), Flatten(consumer)])));

        // A row of a type Abastalia cannot generate stops the whole import.
        var bad = Flatten(consumer);
        bad["TipoeCF"] = "41";
        var refused = await (await admin.PostAsJsonAsync("/api/fiscal/certification/test-set", new { File = Convert.ToBase64String(Encoding.UTF8.GetBytes(Csv([bad]))), Name = "set.csv" })).Json();
        (refused.GetProperty("imported").GetInt32(), refused.GetProperty("errors").GetArrayLength()).ShouldBe((0, 1));

        var imported = await admin.PostAsJsonAsync("/api/fiscal/certification/test-set", new { File = file, Name = "set.csv" });
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
        var importedJson = await imported.Json();
        importedJson.GetProperty("imported").GetInt32().ShouldBe(2, importedJson.ToString());

        JsonElement cases = default;
        for (var i = 0; i < 4; i++) cases = await (await admin.PostAsync("/api/fiscal/certification/send", null)).Json();
        cases.EnumerateArray().Select(c => (c.GetProperty("eNcf").GetString(), c.GetProperty("channel").GetString(), c.GetProperty("status").GetString()))
            .ShouldBe([("E310000000001", "Ecf", "Accepted"), ("E320000000001", "Rfce", "Accepted")]);

        // What reached the DGII is the same XML, field by field and in the same order, signed, and valid against the XSD.
        var sent = XDocument.Parse(app.Dgii.To("/recepcion/api/facturaselectronicas").Single().Xml!);
        EcfXml.Validate(sent, EcfType.FiscalCredit31).ShouldBeEmpty();
        sent.Root!.Elements().Where(e => e.Name.LocalName != "Signature").Select(e => e.ToString(SaveOptions.DisableFormatting))
            .ShouldBe(credit.Root!.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting)));
        XDocument.Parse(app.Dgii.To("/recepcionfc/api/recepcion/ecf").Single().Xml!).Root!.Name.LocalName.ShouldBe("RFCE");

        // The owner's steps.
        (await admin.PutAsJsonAsync("/api/fiscal/certification/steps/3", new { Done = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var state = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/certification");
        state.GetProperty("environment").GetString().ShouldBe("CerteCF");
        state.GetProperty("steps").EnumerateArray().Single().GetProperty("number").GetInt32().ShouldBe(3);
        // TK-225: the progress goes up to the cloud.
        var progressEvents = await app.InScopeAsync(sp => sp.GetRequiredService<Abastalia.BuildingBlocks.Persistence.InfraDbContext>().Outbox
            .Where(m => m.Type == typeof(CertificationProgressedV1).FullName).OrderBy(m => m.Sequence).Select(m => m.Payload).ToListAsync());
        var progress = JsonDocument.Parse(progressEvents[^1]).RootElement;
        progress.GetProperty("doneSteps").EnumerateArray().Select(s => s.GetInt32()).ShouldBe([3]);
        progress.GetProperty("totalSteps").GetInt32().ShouldBe(CertificationSteps.Count);
        state.GetProperty("cases").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task The_portal_XML_is_signed_with_the_store_certificate_and_fiscal_documents_are_refused()
    {
        var admin = await app.WithAddressAsync();
        var signed = await admin.PostAsJsonAsync("/api/fiscal/certification/sign", new { Xml = "<?xml version=\"1.0\"?><Postulacion><RNC>131245678</RNC></Postulacion>" });
        signed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var xml = (await signed.Json()).GetProperty("signedXml").GetString()!;
        var doc = new System.Xml.XmlDocument { PreserveWhitespace = false };
        doc.LoadXml(xml);
        var check = new System.Security.Cryptography.Xml.SignedXml(doc);
        check.LoadXml((System.Xml.XmlElement)doc.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#")[0]!);
        check.CheckSignature().ShouldBeTrue();

        (await (await admin.PostAsJsonAsync("/api/fiscal/certification/sign", new { Xml = "<ECF><Encabezado/></ECF>" })).ErrorCode()).ShouldBe("Fiscal.SignNotAllowed");
        (await (await admin.PostAsJsonAsync("/api/fiscal/certification/sign", new { Xml = "<!DOCTYPE x [<!ENTITY a \"b\">]><x>&a;</x>" })).ErrorCode()).ShouldBe("Fiscal.UnreadableFile");
    }
}

/// <summary>Outside CerteCF the set is never sent; the DGII check says the store is simulated.</summary>
public sealed class CertificationOutsideCerteCfTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task The_set_is_sent_only_in_CerteCF_and_a_simulated_store_does_not_ask_the_DGII()
    {
        var admin = app.Client(await app.AdminAsync());
        (await (await admin.PostAsync("/api/fiscal/certification/send", null)).ErrorCode()).ShouldBe("Fiscal.NotCerteCF");
        (await (await admin.PostAsync("/api/fiscal/dgii-status", null)).Json()).GetProperty("state").GetString().ShouldBe("Simulated");
        (await (await admin.PostAsJsonAsync("/api/fiscal/certification/sign", new { Xml = "<Postulacion/>" })).ErrorCode()).ShouldBe("Fiscal.NoCertificate");
        (await (await admin.PostAsJsonAsync("/api/fiscal/contingency/start", new { Reason = "x" })).ErrorCode()).ShouldBe("Fiscal.CanStillSign");
    }
}

/// <summary>R9 #20: a certificate imported before the RNC was set is checked when the RNC is saved. Own app: no RNC at first.</summary>
public sealed class CertificateRncOnSaveTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task Saving_an_RNC_that_is_not_the_certificates_is_refused()
    {
        var admin = app.Client(await app.AdminAsync());
        using var certificate = TestCertificates.Create(DateTimeOffset.UtcNow.AddYears(1), serial: "RNC-131245678");
        (await admin.PostAsJsonAsync("/api/fiscal/certificate",
            new { File = Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "k")), Password = "k" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var wrong = await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Carnicería", Rnc = "101672919", DiscountCapPercent = 10m });
        (await wrong.ErrorCode()).ShouldBe("Settings.CertificateWrongRnc");
        (await admin.GetFromJsonAsync<JsonElement>("/api/settings/business")).GetProperty("rnc").ValueKind.ShouldBe(JsonValueKind.Null);
        (await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Carnicería", Rnc = "131-24567-8", DiscountCapPercent = 10m })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
    }
}

/// <summary>R8 #4 (a certificate of another RNC, or not valid yet, is refused) and R8 #20 (a failed load is retried, not kept until a restart).</summary>
public sealed class CertificateRulesTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static object Body(X509Certificate2 c) => new { File = Convert.ToBase64String(c.Export(X509ContentType.Pkcs12, "k")), Password = "k" };

    [Fact]
    public async Task A_certificate_of_another_RNC_or_not_valid_yet_is_refused()
    {
        await app.SetUpBusinessAsync(); // RNC 131245678
        var admin = app.Client(await app.AdminAsync());
        using var other = TestCertificates.Create(DateTimeOffset.UtcNow.AddYears(1), serial: "RNC-101672919");
        (await (await admin.PostAsJsonAsync("/api/fiscal/certificate", Body(other))).ErrorCode()).ShouldBe("Fiscal.CertificateWrongRnc");
        using var future = TestCertificates.Create(DateTimeOffset.UtcNow.AddYears(2), DateTimeOffset.UtcNow.AddDays(10), "RNC-131245678");
        (await (await admin.PostAsJsonAsync("/api/fiscal/certificate", Body(future))).ErrorCode()).ShouldBe("Fiscal.CertificateNotYetValid");
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/certificate")).GetProperty("installed").GetBoolean().ShouldBeFalse();

        // The company's own RNC, or the cédula of its delegated signer, is accepted.
        using var own = TestCertificates.Create(DateTimeOffset.UtcNow.AddYears(1), serial: "RNC-131245678");
        (await admin.PostAsJsonAsync("/api/fiscal/certificate", Body(own))).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var signer = TestCertificates.Create(DateTimeOffset.UtcNow.AddYears(1));
        (await admin.PostAsJsonAsync("/api/fiscal/certificate", Body(signer))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void A_certificate_that_failed_to_load_is_tried_again_after_a_minute()
    {
        var path = Path.Combine(Path.GetTempPath(), $"abastalia_retry_{Guid.NewGuid():N}.pfx");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Abastalia:KeysFolder"] = Path.Combine(Path.GetTempPath(), $"abastalia_keys_{Guid.NewGuid():N}"),
            ["Fiscal:Certificate:Path"] = path, ["Fiscal:Certificate:Password"] = "k",
        }).Build();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var certificate = new EcfCertificate(config, DataProtectionProvider.Create("abastalia-tests"), app.Services.GetRequiredService<IHostEnvironment>(), clock);

        certificate.Current.ShouldBeNull();
        certificate.Problem.ShouldNotBeNull();
        using var cert = TestCertificates.Create(DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, "k"));
        clock.Advance(TimeSpan.FromSeconds(30));
        certificate.Current.ShouldBeNull();
        clock.Advance(EcfCertificate.RetryAfter);
        certificate.Current!.Thumbprint.ShouldBe(cert.Thumbprint);
        certificate.Problem.ShouldBeNull();
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void R11_C8_on_the_store_PC_the_key_is_non_exportable_and_only_this_account_and_SYSTEM_may_use_it()
    {
        var keys = Path.Combine(Path.GetTempPath(), $"abastalia_keys_{Guid.NewGuid():N}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Abastalia:KeysFolder"] = keys, ["Fiscal:Certificate:ImportTo"] = "Store",
        }).Build();
        var env = app.Services.GetRequiredService<IHostEnvironment>();
        using var cert = TestCertificates.Create(DateTimeOffset.UtcNow.AddYears(1));

        var imported = new EcfCertificate(config, DataProtectionProvider.Create("abastalia-tests"), env, TimeProvider.System)
            .Import(cert.Export(X509ContentType.Pkcs12, "k"), "k", DateTime.UtcNow, null);
        var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(keys, EcfCertificate.FileName))).RootElement.GetProperty("Fiscal").GetProperty("Certificate");
        var provider = new CngProvider(saved.GetProperty("Provider").GetString()!);
        try
        {
            imported.Value.Store.ShouldBeOneOf("Tpm", "CurrentUser");
            File.Exists(Path.Combine(keys, EcfCertificate.ProtectedFileName)).ShouldBeFalse(); // nothing a copy of the folder could open

            // After a restart: loaded from the machine key, it signs, and the key never leaves.
            var reloaded = new EcfCertificate(config, DataProtectionProvider.Create("abastalia-tests"), env, TimeProvider.System).Current!;
            reloaded.Thumbprint.ShouldBe(cert.Thumbprint);
            using var key = reloaded.GetRSAPrivateKey()!;
            var signature = key.SignData("e-CF"u8.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            cert.GetRSAPublicKey()!.VerifyData("e-CF"u8.ToArray(), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).ShouldBeTrue();
            Should.Throw<CryptographicException>(() => key.ExportParameters(includePrivateParameters: true));
            using var accountKey = CngKey.Open(saved.GetProperty("KeyName").GetString()!, provider);
            var acl = new System.Security.AccessControl.RawSecurityDescriptor(accountKey.GetProperty("Security Descr", (CngPropertyOptions)4).GetValue()!, 0);
            acl.DiscretionaryAcl!.Cast<System.Security.AccessControl.CommonAce>().Select(a => a.SecurityIdentifier.Value)
                .ShouldBe(["S-1-5-18", System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value], ignoreOrder: true); // no Administrators
        }
        finally
        {
            CngKey.Open(saved.GetProperty("KeyName").GetString()!, provider).Delete();
            Directory.Delete(keys, recursive: true);
        }
    }
}
