using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Fiscal;

/// <summary>TK-156 (D17): with this PC's clock 3 minutes off the DGII's (the fake DGII's <c>Date</c> header), the store keeps selling but signs
/// nothing: the e-CF waits unsigned, the register and the owner see why, and once the clock is right it is signed, the sale gets its code and QR,
/// and the queue sends it. Own app.</summary>
public sealed class ClockSkewTests(DgiiAbastaliaApp app) : IClassFixture<DgiiAbastaliaApp>
{
    [Fact]
    public async Task With_the_clock_off_sales_go_on_and_their_e_CF_are_signed_when_it_is_fixed()
    {
        var admin = await app.SetUpAsync();
        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 50);
        async Task<string> DgiiStatusAsync() => (await (await admin.PostAsync("/api/fiscal/dgii-status", null)).Json()).GetProperty("state").GetString()!;

        app.Dgii.AppAheadBy = TimeSpan.FromMinutes(3);
        (await DgiiStatusAsync()).ShouldBe("Up"); // the seed's Date header measures the clock

        var sale = await cashier.SellAsync(soda, 59m);
        sale.GetProperty("eNcf").GetString().ShouldBe("E320000000001");
        var receipt = sale.GetProperty("receipt");
        (receipt.GetProperty("securityCode").GetString(), receipt.GetProperty("qrUrl").GetString()).ShouldBe(("", ""));
        receipt.GetProperty("signedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        receipt.GetProperty("contingencyLegend").GetString().ShouldBe(EcfReceiptTexts.ContingencyLegend);
        var waiting = await app.DocumentAsync("E320000000001");
        (waiting.Status, waiting.AwaitingSignature).ShouldBe((SubmissionStatus.Pending, true));
        XDocument.Parse(EcfSubmissionQueue.Unzip(waiting.SignedXml)).Descendants().ShouldNotContain(e => e.Name.LocalName == "Signature");
        // R12 #6: a 31 of an earlier day (its XML says so) keeps that day in the QR when it is signed after the fix.
        var credit = await cashier.SellAsync(soda, 59m, "FiscalCredit31", "131313131");
        var creditEncf = credit.GetProperty("eNcf").GetString()!;
        await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            var d = await db.Documents.SingleAsync(x => x.ENcf == creditEncf);
            var x = XDocument.Parse(EcfSubmissionQueue.Unzip(d.SignedXml));
            x.Descendants("FechaEmision").Single().Value = "01-10-2026";
            d.SignedXml = EcfSubmissionQueue.Zip(x.ToString(SaveOptions.DisableFormatting));
            return await db.SaveChangesAsync();
        });

        // Nothing unsigned leaves the store; the register and the owner see why; the owner's signing actions wait for the clock too.
        await app.DrainAsync();
        app.Dgii.To("/recepcionfc/api/recepcion/ecf").ShouldBeEmpty();
        const string Ahead = "La hora de la PC principal está adelantada 3 minutos: corríjala en Windows";
        (await cashier.Client.GetFromJsonAsync<JsonElement>("/api/fiscal/contingency")).GetProperty("clockProblem").GetString().ShouldBe(Ahead);
        (await admin.HealthAsync()).Items().ShouldContain(i => i.Code == "clock" && i.Level == "error" && i.Title == Ahead);
        var range = await (await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = 34, From = 700_000, To = 700_009 })).Json();
        (await (await admin.PostAsJsonAsync($"/api/fiscal/sequences/{range.GetProperty("id").GetGuid()}/void", new { })).ErrorCode()).ShouldBe("Fiscal.ClockOff");
        (await SignWaitingAsync()).ShouldBe(0);

        app.Dgii.AppAheadBy = TimeSpan.FromMinutes(-3);
        (await DgiiStatusAsync()).ShouldBe("Up");
        (await admin.HealthAsync()).Items().Single(i => i.Code == "clock").Title.ShouldContain("atrasada 3 minutos");

        // The owner fixes the time: the next measure clears it, the e-CF is signed now (issue date unchanged) and goes to the DGII.
        app.Dgii.AppAheadBy = TimeSpan.Zero;
        (await DgiiStatusAsync()).ShouldBe("Up");
        (await admin.HealthAsync()).Items().ShouldNotContain(i => i.Code == "clock");
        (await SignWaitingAsync()).ShouldBe(2);
        var signed = await app.DocumentAsync("E320000000001");
        (signed.AwaitingSignature, signed.SecurityCode.Length, signed.SignedUtc).ShouldBe((false, 6, app.Clock.GetUtcNow().UtcDateTime));
        XDocument.Parse(EcfSubmissionQueue.Unzip(signed.SignedXml)).Root!.Element("FechaHoraFirma")!.Value
            .ShouldBe((signed.SignedUtc + Abastalia.SharedKernel.BusinessDay.Offset).ToString("dd-MM-yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));

        await app.InScopeAsync(sp => sp.GetRequiredService<OutboxProcessor>().ProcessAsync(CancellationToken.None));
        var reprint = await admin.GetFromJsonAsync<JsonElement>($"/api/sales/{sale.GetProperty("saleId").GetGuid()}/receipt");
        reprint.GetProperty("securityCode").GetString().ShouldBe(signed.SecurityCode);
        reprint.GetProperty("qrUrl").GetString()!.ShouldContain("ENCF=E320000000001");
        reprint.GetProperty("signedAt").ValueKind.ShouldBe(JsonValueKind.String);
        var creditQr = (await admin.GetFromJsonAsync<JsonElement>($"/api/sales/{credit.GetProperty("saleId").GetGuid()}/receipt")).GetProperty("qrUrl").GetString()!;
        creditQr.ShouldContain("FechaEmision=01-10-2026");

        await app.DrainAsync();
        (await app.DocumentAsync("E320000000001")).Status.ShouldBe(SubmissionStatus.Accepted);
    }

    [Fact]
    public async Task The_DGII_check_tells_a_DGII_outage_from_this_store_being_offline()
    {
        var admin = await app.SetUpAsync();
        async Task<string> DgiiStatusAsync() => (await (await admin.PostAsync("/api/fiscal/dgii-status", null)).Json()).GetProperty("state").GetString()!;
        app.Dgii.EcfDown = true;
        (await DgiiStatusAsync()).ShouldBe("Down"); // the e-CF service does not answer, the DGII's status host does
        app.Dgii.Down = true;
        (await DgiiStatusAsync()).ShouldBe("Unreachable");
        (app.Dgii.EcfDown, app.Dgii.Down) = (false, false);
        (await DgiiStatusAsync()).ShouldBe("Up");
    }

    private Task<int> SignWaitingAsync() => app.InScopeAsync(async sp =>
        (await sp.GetRequiredService<ICommandHandler<SignWaiting.Command, int>>().Handle(new SignWaiting.Command(), CancellationToken.None)).Value);
}
