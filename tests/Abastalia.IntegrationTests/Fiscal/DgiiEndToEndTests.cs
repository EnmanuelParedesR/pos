using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.IntegrationTests.Infra;
using Abastalia.SharedKernel;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Fiscal;

/// <summary>The real configuration of a certified store in pre-certification: XMLDSig with a (self-signed) certificate and the DGII provider in
/// TesteCF, against <see cref="FakeDgii"/>.</summary>
public class DgiiAbastaliaApp : AbastaliaApp
{
    private static readonly Lazy<string> Pfx = new(() =>
    {
        using var rsa = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=Firmante de Prueba, SERIALNUMBER=00112345678", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(Path.GetTempPath(), $"abastalia_ecf_{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pkcs12, "prueba"));
        return path;
    });

    protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
    {
        ["Fiscal:Signer"] = "Certificate", ["Fiscal:Certificate:Path"] = Pfx.Value, ["Fiscal:Certificate:Password"] = "prueba",
        ["Fiscal:Provider"] = "Dgii", ["Fiscal:Dgii:Environment"] = "TesteCF",
    };
}

internal static class DgiiStore
{
    public const string Rnc = "131245678";

    /// <summary>The real signer needs the address (it is required in the XML).</summary>
    public static async Task<HttpClient> SetUpAsync(this DgiiAbastaliaApp app)
    {
        var admin = app.Client(await app.AdminAsync());
        var business = await admin.GetFromJsonAsync<JsonElement>("/api/settings/business");
        (await admin.PutAsJsonAsync("/api/settings/business", new
        {
            Name = "Carnicería de prueba", Rnc, Address = "Av. Duarte 25, Santo Domingo", DiscountCapPercent = business.GetProperty("discountCapPercent").GetDecimal(),
        })).EnsureSuccessStatusCode();
        return admin;
    }

    /// <summary>Runs the queue as the worker would, a minute apart, until nothing is left to do.</summary>
    public static async Task DrainAsync(this AbastaliaApp app)
    {
        for (var i = 0; i < 10; i++)
        {
            await app.InScopeAsync(sp => sp.GetRequiredService<EcfSubmissionQueue>().ProcessAsync(CancellationToken.None));
            app.Clock.Advance(TimeSpan.FromMinutes(1));
        }
    }

    public static Task<FiscalDocument> DocumentAsync(this AbastaliaApp app, string encf) =>
        app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().SingleAsync(d => d.ENcf == encf));

    public static async Task<JsonElement> SellAsync(this Cashier cashier, Guid product, decimal price, string type = "Consumer32", string? rnc = null)
    {
        // An E31 to an RNC typed at the register asks a supervisor (R11 A8), as the register would.
        var saleId = Guid.CreateVersion7();
        var r = await cashier.SellSupervisedAsync(saleId, approvals => new
        {
            SaleId = saleId, ShiftId = cashier.ShiftId, Lines = new[] { Line(product, 1m) }, Payments = new[] { new { Method = "Cash", Amount = price } },
            ExpectedTotal = price, DocumentType = type, CustomerRnc = rnc, CustomerName = rnc is null ? null : "Colmado El Sol SRL", SupervisorApprovals = approvals,
        });
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
        return await r.Json();
    }
}

/// <summary>TK-144 to TK-150 end to end with the fake DGII: sale → queue → DGII → result, RFCE, rejection → replacement, archive, ANECF.</summary>
public sealed class DgiiEndToEndTests(DgiiAbastaliaApp app) : IClassFixture<DgiiAbastaliaApp>
{
    [Fact]
    public async Task Sales_reach_the_DGII_by_the_right_channel_and_a_rejected_one_is_replaced()
    {
        var admin = await app.SetUpAsync();
        await Abastalia.IntegrationTests.Sync.SyncApp.ConnectAsync(app);
        var cashier = await app.CashierWithShiftAsync(loadSequences: false); // TesteCF loads the DGII test range on first use
        var category = await admin.CategoryAsync();
        var soda = await admin.ProductAsync(category, "Refresco", 59m, stock: 50);
        var carcass = await admin.ProductAsync(category, "Res en canal", 300_000m, stock: 5);

        var small = await cashier.SellAsync(soda, 59m);
        var credit = await cashier.SellAsync(soda, 59m, "FiscalCredit31", "101672919");
        var big = await cashier.SellAsync(carcass, 300_000m, rnc: "101672919");
        string Encf(JsonElement sale) => sale.GetProperty("eNcf").GetString()!;
        (Encf(small), Encf(credit), Encf(big)).ShouldBe(("E320000000001", "E310000000001", "E320000000002"));

        // TK-148: the QR per channel on the TesteCF host, the signing time, the range expiry on the 31, no legend while the DGII is reachable.
        var receipt = small.GetProperty("receipt");
        receipt.GetProperty("qrUrl").GetString()!.ShouldStartWith(
            $"https://fc.dgii.gov.do/testecf/consultatimbrefc?RncEmisor={DgiiStore.Rnc}&ENCF=E320000000001&MontoTotal=59.00&CodigoSeguridad=");
        receipt.GetProperty("signedAt").ValueKind.ShouldBe(JsonValueKind.String);
        receipt.GetProperty("contingencyLegend").ValueKind.ShouldBe(JsonValueKind.Null);
        receipt.GetProperty("sequenceExpiresOn").ValueKind.ShouldBe(JsonValueKind.Null);
        var creditReceipt = credit.GetProperty("receipt");
        creditReceipt.GetProperty("qrUrl").GetString()!.ShouldStartWith(
            $"https://ecf.dgii.gov.do/testecf/consultatimbre?RncEmisor={DgiiStore.Rnc}&RncComprador=101672919&ENCF=E310000000001&FechaEmision=");
        creditReceipt.GetProperty("sequenceExpiresOn").GetString().ShouldBe("2028-12-31");
        big.GetProperty("receipt").GetProperty("qrUrl").GetString()!.ShouldContain("/testecf/consultatimbre?");

        app.Dgii.Result = encf => encf == "E310000000001" ? (2, true, "El RNC del comprador no corresponde") : (1, true, null);
        await app.DrainAsync();

        // The 32 under RD$250,000 went as a signed RFCE to recepcionfc, the others whole to recepción with a TrackId.
        var rfce = app.Dgii.To("/recepcionfc/api/recepcion/ecf").Single();
        (rfce.Host, rfce.Field, rfce.FileName).ShouldBe(("fc.dgii.gov.do", "xml", $"{DgiiStore.Rnc}E320000000001.xml"));
        var summary = XDocument.Parse(rfce.Xml!);
        EcfXml.Validate(summary, EcfXml.RfceSchema).ShouldBeEmpty();
        summary.Descendants("CodigoSeguridadeCF").Single().Value.ShouldBe(receipt.GetProperty("securityCode").GetString());
        app.Dgii.To("/recepcion/api/facturaselectronicas").Select(r => r.FileName).ShouldBe([$"{DgiiStore.Rnc}E310000000001.xml", $"{DgiiStore.Rnc}E320000000002.xml"],
            ignoreOrder: true);
        app.Dgii.Tokens.Count.ShouldBe(1); // one seed for every call: the token is cached until shortly before it expires

        var smallDoc = await app.DocumentAsync("E320000000001");
        (smallDoc.Status, smallDoc.Channel, smallDoc.Environment).ShouldBe((SubmissionStatus.Accepted, EcfChannel.Rfce, FiscalEnvironment.TesteCF));
        var bigDoc = await app.DocumentAsync("E320000000002");
        (bigDoc.Status, bigDoc.Channel).ShouldBe((SubmissionStatus.Accepted, EcfChannel.Ecf));
        bigDoc.TrackId.ShouldStartWith("track-"); // both whole e-CFs share one deadline (fake clock), so SQL Server may send either first
        var rejected = await app.DocumentAsync("E310000000001");
        (rejected.Status, rejected.DgiiCode, rejected.DgiiMessages).ShouldBe((SubmissionStatus.Rejected, 2, "145: El RNC del comprador no corresponde"));

        // The cloud only archives: XML, RFCE and result of every final document.
        var archived = app.Cloud.To(EcfSubmissionQueue.ArchivePath).ToList();
        archived.Select(a => a.Body.GetProperty("encf").GetString()).ShouldBe(["E320000000001", "E320000000002", "E310000000001"], ignoreOrder: true);
        archived.Single(a => a.Body.GetProperty("encf").GetString() == "E320000000001").Body.GetProperty("rfceGzip").GetString().ShouldNotBeNullOrEmpty();
        (await app.DocumentAsync("E310000000001")).ArchivedUtc.ShouldNotBeNull();

        // TK-147: "Rechazados por la DGII" → a new e-CF with the same data replaces it; the sale follows it.
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/documents?rejectedOnly=true");
        list.EnumerateArray().Select(d => d.GetProperty("eNcf").GetString()).ShouldBe(["E310000000001"]);
        var replace = await admin.PostAsync($"/api/fiscal/documents/{rejected.Id}/replace", null);
        replace.StatusCode.ShouldBe(HttpStatusCode.Created);
        var replacement = await replace.Json();
        replacement.GetProperty("eNcf").GetString().ShouldBe("E310000000002");
        (await admin.PostAsync($"/api/fiscal/documents/{rejected.Id}/replace", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await (await admin.PostAsync($"/api/fiscal/documents/{bigDoc.Id}/replace", null)).ErrorCode()).ShouldBe("Fiscal.NotRejected");

        var newDoc = await app.DocumentAsync("E310000000002");
        newDoc.ReplacesENcf.ShouldBe("E310000000001");
        (await app.DocumentAsync("E310000000001")).ReplacedByENcf.ShouldBe("E310000000002");
        var newXml = XDocument.Parse(EcfSubmissionQueue.Unzip(newDoc.SignedXml));
        EcfXml.Validate(newXml, EcfType.FiscalCredit31).ShouldBeEmpty();
        newXml.Descendants("RNCComprador").Single().Value.ShouldBe("101672919");
        newXml.Descendants("MontoTotal").Single().Value.ShouldBe("59.00");

        await app.InScopeAsync(sp => sp.GetRequiredService<OutboxProcessor>().ProcessAsync(CancellationToken.None));
        var saleId = credit.GetProperty("saleId").GetGuid();
        var reprint = await admin.GetFromJsonAsync<JsonElement>($"/api/sales/{saleId}/receipt");
        reprint.GetProperty("eNcf").GetString().ShouldBe("E310000000002");
        reprint.GetProperty("qrUrl").GetString()!.ShouldContain("ENCF=E310000000002");
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/documents?rejectedOnly=true")).GetArrayLength().ShouldBe(0);

        await app.DrainAsync();
        (await app.DocumentAsync("E310000000002")).Status.ShouldBe(SubmissionStatus.Accepted);

        // TK-149: what was issued in TesteCF is not fiscal: the 607 leaves it out.
        var today = BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime);
        var period = $"from={today.AddDays(-1):yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        (await admin.GetFromJsonAsync<JsonElement>($"/api/reports/dgii/607?{period}")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Unused_numbers_are_voided_with_a_signed_ANECF_and_never_reserved_again()
    {
        var admin = await app.SetUpAsync();
        // TK-149: a 34 range without expiry is allowed; a 31 one is not.
        (await (await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = 31, From = 900, To = 950 })).ErrorCode()).ShouldBe("Fiscal.ExpiryRequired");
        var load = await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = 34, From = 500_000, To = 500_099 });
        load.StatusCode.ShouldBe(HttpStatusCode.Created);
        var sequence = (await load.Json()).GetProperty("id").GetGuid();
        var listed = (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/sequences")).EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == sequence);
        (listed.GetProperty("environment").GetString(), listed.GetProperty("current").GetBoolean()).ShouldBe(("TesteCF", true));
        listed.GetProperty("expiresOn").ValueKind.ShouldBe(JsonValueKind.Null);

        var voided = await admin.PostAsJsonAsync($"/api/fiscal/sequences/{sequence}/void", new { To = 500_049 });
        voided.StatusCode.ShouldBe(HttpStatusCode.Created);
        var v = await voided.Json();
        (v.GetProperty("from").GetString(), v.GetProperty("to").GetString(), v.GetProperty("count").GetInt64()).ShouldBe(("E340000500000", "E340000500049", 50));

        await app.DrainAsync();
        var sent = app.Dgii.To("/anulacionrangos/api/operaciones/anularrango").Single();
        var anecf = XDocument.Parse(sent.Xml!);
        EcfXml.Validate(anecf, EcfXml.AnecfSchema).ShouldBeEmpty();
        anecf.Descendants("SecuenciaeNCFDesde").Single().Value.ShouldBe("E340000500000");
        var voids = await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/voids");
        voids.EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("Accepted");

        // The rest of the range is voided too; nothing is left to void or to reserve.
        (await admin.PostAsync($"/api/fiscal/sequences/{sequence}/void", JsonContent.Create(new { }))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await admin.PostAsync($"/api/fiscal/sequences/{sequence}/void", JsonContent.Create(new { }))).ErrorCode()).ShouldBe("Fiscal.NothingToVoid");
        listed = (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/sequences")).EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == sequence);
        listed.GetProperty("available").GetInt64().ShouldBe(0);
    }
}

/// <summary>TK-146/148: without the DGII the store keeps selling, receipts carry the contingency legend and the queue catches up. Own app.</summary>
public sealed class DgiiContingencyTests(DgiiAbastaliaApp app) : IClassFixture<DgiiAbastaliaApp>
{
    [Fact]
    public async Task Without_the_DGII_receipts_carry_the_legend_and_everything_is_sent_when_it_is_back()
    {
        var admin = await app.SetUpAsync();
        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 50);

        app.Dgii.Down = true;
        var first = await cashier.SellAsync(soda, 59m);
        first.GetProperty("receipt").GetProperty("contingencyLegend").ValueKind.ShouldBe(JsonValueKind.Null); // nothing failed yet
        await app.InScopeAsync(sp => sp.GetRequiredService<EcfSubmissionQueue>().ProcessAsync(CancellationToken.None));
        (await app.DocumentAsync("E320000000001")).Attempts.ShouldBe(1);

        var second = await cashier.SellAsync(soda, 59m);
        second.GetProperty("receipt").GetProperty("contingencyLegend").GetString().ShouldBe(EcfReceiptTexts.ContingencyLegend);
        var reprint = await admin.GetFromJsonAsync<JsonElement>($"/api/sales/{second.GetProperty("saleId").GetGuid()}/receipt");
        reprint.GetProperty("contingencyLegend").GetString().ShouldBe(EcfReceiptTexts.ContingencyLegend);

        app.Dgii.Down = false;
        await app.DrainAsync();
        (await app.DocumentAsync("E320000000001")).Status.ShouldBe(SubmissionStatus.Accepted);
        (await app.DocumentAsync("E320000000002")).Status.ShouldBe(SubmissionStatus.Accepted);
        var third = await cashier.SellAsync(soda, 59m);
        third.GetProperty("receipt").GetProperty("contingencyLegend").ValueKind.ShouldBe(JsonValueKind.Null);

        // A day without the DGII: the health check warns well before the 72-hour deadline.
        app.Dgii.Down = true;
        await app.DrainAsync();
        app.Clock.Advance(TimeSpan.FromHours(25));
        var health = await app.InScopeAsync(sp => ActivatorUtilities.CreateInstance<EcfQueueHealthCheck>(sp).CheckHealthAsync(new HealthCheckContext()));
        health.Status.ShouldBe(HealthStatus.Degraded);
    }
}
