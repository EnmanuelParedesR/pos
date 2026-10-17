using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.IntegrationTests.Infra;
using Abastalia.IntegrationTests.Sync;
using static Abastalia.IntegrationTests.Purchasing.DgiiReconciliationTests;

namespace Abastalia.IntegrationTests.Purchasing;

/// <summary>TK-224, batch: several supplier invoices, each read by the cloud AI into its own draft. Its own store: it connects to the cloud, which the
/// single-reading test checks is off at first.</summary>
public sealed class SupplierInvoiceBatchTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static HttpResponseMessage Read(string rnc, string ncf, object[] lines, decimal total) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            supplierRnc = rnc, supplierName = "Embutidos Nuevos", ncf, date = Day, lines, subtotal = Math.Round(total / 1.18m, 2), itbis = total - Math.Round(total / 1.18m, 2), total,
            confidence = 0.9m, doubtful = Array.Empty<string>(),
        }),
    };

    private static object Pdf(string name) => new { File = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"%PDF-1.4 factura {name}")), ContentType = "application/pdf", FileName = name };

    [Fact]
    public async Task Each_invoice_becomes_a_draft_with_its_original_and_a_spent_quota_stops_the_batch()
    {
        var c = app.Client(await app.AdminAsync());
        var product = await c.ProductAsync(await c.CategoryAsync(), "Longaniza", 180m, cost: 90m);
        var supplier = await SupplierAsync(c, "137000007");
        await PostedPurchaseAsync(c, supplier, product, "B0100000700", 90m);
        await app.ConnectAsync();
        var answers = new Queue<HttpResponseMessage>([
            Read("137000007", "B0100000701", [new { description = "LONGANIZA", quantity = 4m, unitCost = 95m, itbisPercent = 18m }, new { description = "Queso de freír", quantity = 2m, unitCost = 150m }], 802.4m),
            Read("138000008", "B0100000801", [new { description = "Chorizo", quantity = 3m, unitCost = 100m }], 354m),
            new(HttpStatusCode.TooManyRequests)
            {
                Content = JsonContent.Create(new { code = "Quota.Exceeded", detail = "Este mes ya se usó todo lo incluido en su plan de lectura de facturas (20). Se renueva el 01/11/2026." }),
            },
            new(HttpStatusCode.TooManyRequests),
            Read("137000007", "B0100000701", [], 802.4m),
        ]);
        app.Cloud.Respond = path => path == "/ai/v1/supplier-invoice" ? answers.Dequeue() : null;

        // A known supplier: the matched line goes in, the other is listed to add, the original is attached and nothing is received.
        var first = await (await c.PostAsJsonAsync("/api/purchasing/purchases/draft-from-invoice", Pdf("uno.pdf"))).Json();
        var id = first.GetProperty("purchaseId").GetGuid();
        first.GetProperty("review").GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ShouldContain(w => w.StartsWith("Agregue: Queso de freír", StringComparison.Ordinal));
        var draft = await c.GetFromJsonAsync<JsonElement>($"/api/purchasing/purchases/{id}");
        (draft.GetProperty("status").GetString(), draft.GetProperty("supplierId").GetGuid(), draft.GetProperty("ncf").GetString()).ShouldBe(("Draft", supplier, "B0100000701"));
        draft.GetProperty("lines").EnumerateArray().Single().GetProperty("productId").GetGuid().ShouldBe(product);
        draft.GetProperty("attachmentName").GetString().ShouldBe("uno.pdf");
        var original = await c.GetAsync($"/api/purchasing/purchases/{id}/attachment");
        (original.Content.Headers.ContentType!.MediaType, await original.Content.ReadAsStringAsync()).ShouldBe(("application/pdf", "%PDF-1.4 factura uno.pdf"));

        // A new RNC: the supplier is created (to confirm) and the draft keeps the invoice's totals until its products are added.
        var second = await (await c.PostAsJsonAsync("/api/purchasing/purchases/draft-from-invoice", Pdf("dos.pdf"))).Json();
        second.GetProperty("review").GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ShouldContain(w => w.StartsWith("Se creó el suplidor", StringComparison.Ordinal));
        var other = await c.GetFromJsonAsync<JsonElement>($"/api/purchasing/purchases/{second.GetProperty("purchaseId").GetGuid()}");
        (other.GetProperty("status").GetString(), other.GetProperty("total").GetDecimal(), other.GetProperty("lines").GetArrayLength()).ShouldBe(("Draft", 354m, 0));

        // The month's quota stops the batch saying when it renews; the per-minute limit asks to wait.
        var spent = await c.PostAsJsonAsync("/api/purchasing/purchases/draft-from-invoice", Pdf("tres.pdf"));
        (await spent.ErrorCode()).ShouldBe("Purchasing.AiQuotaExceeded");
        (await spent.Content.ReadAsStringAsync()).ShouldContain("01/11/2026");
        (await (await c.PostAsJsonAsync("/api/purchasing/purchases/draft-from-invoice", Pdf("cuatro.pdf"))).ErrorCode()).ShouldBe("Purchasing.AiBusy");

        // The same invoice again is not a second draft. R12: the same file (a retry, the batch picked again) opens its draft without reading it again;
        // another file of the same invoice is refused by its NCF.
        var again = await (await c.PostAsJsonAsync("/api/purchasing/purchases/draft-from-invoice", Pdf("uno.pdf"))).Json();
        again.GetProperty("purchaseId").GetGuid().ShouldBe(id);
        answers.Count.ShouldBe(1); // the cloud was not asked
        (await (await c.PostAsJsonAsync("/api/purchasing/purchases/draft-from-invoice", Pdf("uno-foto.pdf"))).ErrorCode()).ShouldBe("Purchasing.DuplicateNcf");
        (await c.GetFromJsonAsync<JsonElement>("/api/purchasing/purchases")).EnumerateArray().Count(p => p.GetProperty("status").GetString() == "Draft").ShouldBe(2);

        // R12: the cloud AI down, not set up or not allowed (403/5xx) has its own code, which stops the batch.
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.ServiceUnavailable })
        {
            answers.Enqueue(new HttpResponseMessage(status) { Content = JsonContent.Create(new { code = "Ai.Unavailable", detail = "La IA no está disponible." }) });
            (await (await c.PostAsJsonAsync("/api/purchasing/purchases/draft-from-invoice", Pdf($"{status}.pdf"))).ErrorCode()).ShouldBe("Purchasing.AiUnavailable");
        }
        // R13: one unreadable invoice (the cloud's 422) is that invoice's problem only: the batch goes on.
        answers.Enqueue(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = JsonContent.Create(new { code = "Ai.Unreadable", detail = "No se pudo leer la factura." }) });
        (await (await c.PostAsJsonAsync("/api/purchasing/purchases/draft-from-invoice", Pdf("borrosa.pdf"))).ErrorCode()).ShouldBe("Purchasing.AiRefused");

        // R12 (draft spam): 20 drafts a minute per person, then it waits.
        var me = (await app.AdminAsync()).UserId;
        await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<Abastalia.Purchasing.PurchasingDbContext>();
            var now = app.Clock.GetUtcNow().UtcDateTime;
            for (var i = 0; i < Abastalia.Purchasing.Features.DraftFromInvoice.PerMinute; i++)
                db.Purchases.Add(new Abastalia.Purchasing.Domain.Purchase
                {
                    Id = Guid.CreateVersion7(), Status = Abastalia.Purchasing.Domain.PurchaseStatus.Draft, SupplierId = supplier, SupplierName = "x",
                    InvoiceDate = Day, AttachmentName = "x.pdf", CreatedUtc = now, UserId = me,
                });
            return await db.SaveChangesAsync();
        });
        (await (await c.PostAsJsonAsync("/api/purchasing/purchases/draft-from-invoice", Pdf("spam.pdf"))).ErrorCode()).ShouldBe("Purchasing.AiBusy");
    }
}
