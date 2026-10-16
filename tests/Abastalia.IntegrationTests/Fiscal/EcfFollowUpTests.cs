using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Domain;
using Abastalia.IntegrationTests.Infra;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Fiscal;

/// <summary>TK-240: deliveries to buyers with "Reintentar", the DGII's conditional acceptance in the list and in "Para revisar", and the printed
/// representation (RI) of each e-CF.</summary>
public sealed class EcfFollowUpTests(ExchangeAbastaliaApp app) : IClassFixture<ExchangeAbastaliaApp>
{
    private const string Buyer = "101672919";

    [Fact]
    public async Task A_failed_delivery_is_listed_and_retried_a_conditional_acceptance_is_explained_and_each_e_CF_prints_its_RI()
    {
        var admin = await app.SetUpAsync();
        var cashier = await app.CashierWithShiftAsync(loadSequences: false);
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 50);
        var encf = (await cashier.SellAsync(soda, 59m, "FiscalCredit31", Buyer)).GetProperty("eNcf").GetString()!;
        await app.DrainAsync();
        var doc = await app.DocumentAsync(encf);

        // Deliveries: the list carries attempts and the last error; "Reintentar" puts it back in the queue now.
        await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            db.Set<EcfDelivery>().Add(new EcfDelivery
            {
                DocumentId = doc.Id, ENcf = encf, BuyerRnc = Buyer, Status = DeliveryStatus.Failed, Attempts = 9, CreatedUtc = DateTime.UtcNow.AddDays(-4),
                Message = "Se dejó de intentar después de 72 horas: sin respuesta",
            });
            await db.Documents.Where(d => d.Id == doc.Id).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, SubmissionStatus.AcceptedConditional).SetProperty(d => d.DgiiMessages, "El campo DireccionEmisor está incompleto."));
            return await db.SaveChangesAsync();
        });
        var row = (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/deliveries")).EnumerateArray().Single(d => d.GetProperty("eNcf").GetString() == encf);
        (row.GetProperty("status").GetString(), row.GetProperty("attempts").GetInt32()).ShouldBe(("Failed", 9));
        row.GetProperty("message").GetString()!.ShouldContain("72 horas");

        var retried = await admin.PostAsync($"/api/fiscal/deliveries/{doc.Id}/retry", null);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await retried.Json();
        (after.GetProperty("status").GetString(), after.GetProperty("attempts").GetInt32()).ShouldBe(("Pending", 0));
        (await (await admin.PostAsync($"/api/fiscal/deliveries/{Guid.NewGuid()}/retry", null)).ErrorCode()).ShouldBe("Fiscal.DeliveryNotFound");

        // Conditional acceptance: in the list with the DGII's message, and in "Para revisar" (not a health problem).
        var conditional = (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/documents?conditionalOnly=true")).EnumerateArray().ShouldHaveSingleItem();
        conditional.GetProperty("dgiiMessages").GetString().ShouldBe("El campo DireccionEmisor está incompleto.");
        var item = (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/health")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("code").GetString() == "conditional");
        item.GetProperty("title").GetString()!.ShouldContain(encf);
        item.GetProperty("detail").GetString()!.ShouldContain("DireccionEmisor");

        // The RI: an A4 page with the type in words, the e-NCF, the buyer, the QR and the security code with the signing date.
        var printed = await admin.GetAsync($"/api/fiscal/documents/{doc.Id}/printed");
        printed.StatusCode.ShouldBe(HttpStatusCode.OK);
        printed.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        printed.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe($"{DgiiStore.Rnc}{encf}.html");
        var html = await printed.Content.ReadAsStringAsync();
        html.ShouldContain("Factura de Crédito Fiscal Electrónica");
        html.ShouldContain(encf);
        html.ShouldContain(Buyer);
        html.ShouldContain("Válida hasta");
        html.ShouldContain("<svg");
        html.ShouldContain($"Código de seguridad: <b>{doc.SecurityCode}</b>");
        html.ShouldContain("Fecha de firma digital");
        (await (await admin.GetAsync($"/api/fiscal/documents/{Guid.NewGuid()}/printed")).ErrorCode()).ShouldBe("Fiscal.NotFound");
        (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/documents?environment=TesteCF")).EnumerateArray()
            .ShouldContain(d => d.GetProperty("eNcf").GetString() == encf);
    }
}
