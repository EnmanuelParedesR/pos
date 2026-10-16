using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Fiscal;
using Toki.IntegrationTests.Infra;
using Toki.IntegrationTests.Sync;
using static Toki.IntegrationTests.Purchasing.DgiiReconciliationTests;

namespace Toki.IntegrationTests.Purchasing;

/// <summary>TK-240: a purchase from the supplier's e-CF (received, uploaded as XML, or found behind an AI reading) and the original invoice kept with it.
/// Its own store: it connects to the cloud, which the TK-224 test checks is off at first.</summary>
public sealed class SupplierEcfPurchaseTests(TokiApp app) : IClassFixture<TokiApp>
{
    /// <summary>A supplier's e-CF 31 with ITBIS included in the price (IndicadorMontoGravado 1): 10 × RD$118 = 1,000 + 180 of ITBIS.</summary>
    private static string SupplierEcf(string rnc, string encf) =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><ECF><Encabezado><Version>1.0</Version><IdDoc><TipoeCF>31</TipoeCF><eNCF>{encf}</eNCF>"
        + "<IndicadorMontoGravado>1</IndicadorMontoGravado></IdDoc><Emisor><RNCEmisor>" + rnc + "</RNCEmisor><RazonSocialEmisor>Distribuidora del Cibao SRL</RazonSocialEmisor>"
        + $"<FechaEmision>{Day:dd-MM-yyyy}</FechaEmision></Emisor><Comprador><RNCComprador>130000001</RNCComprador></Comprador>"
        + "<Totales><MontoGravadoTotal>1000.00</MontoGravadoTotal><TotalITBIS>180.00</TotalITBIS><MontoTotal>1180.00</MontoTotal></Totales></Encabezado>"
        + "<DetallesItems><Item><NumeroLinea>1</NumeroLinea><IndicadorFacturacion>1</IndicadorFacturacion><NombreItem>COSTILLA DE CERDO</NombreItem>"
        + "<CantidadItem>10.00</CantidadItem><PrecioUnitarioItem>118.00</PrecioUnitarioItem><MontoItem>1180.00</MontoItem></Item>"
        + "<Item><NumeroLinea>2</NumeroLinea><IndicadorFacturacion>4</IndicadorFacturacion><NombreItem>Hielo</NombreItem>"
        + "<CantidadItem>1.00</CantidadItem><PrecioUnitarioItem>0.00</PrecioUnitarioItem><MontoItem>0.00</MontoItem></Item></DetallesItems></ECF>";

    private Task<int> ReceiveAsync(long cloudId, string rnc, string encf, string environment) => app.InScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<FiscalDbContext>();
        db.Set<ReceivedDocument>().Add(new ReceivedDocument
        {
            Id = Guid.CreateVersion7(), CloudId = cloudId, Environment = environment, IssuerRnc = rnc, IssuerName = "Distribuidora del Cibao SRL", ENcf = encf, Type = 31,
            IssuedOn = Day, Total = 1180m, Itbis = 180m, XmlGzip = EcfSubmissionQueue.Zip(SupplierEcf(rnc, encf)), ReceivedUtc = DateTime.UtcNow,
        });
        return await db.SaveChangesAsync();
    });

    [Fact]
    public async Task A_received_e_CF_fills_the_purchase_and_wins_over_the_AI_and_the_original_invoice_stays_with_the_purchase()
    {
        var c = app.Client(await app.AdminAsync());
        var product = await c.ProductAsync(await c.CategoryAsync(), "Costilla de cerdo", 200m, cost: 100m);
        var known = await SupplierAsync(c, "135000005");
        await PostedPurchaseAsync(c, known, product, "B0100000301", 100m);
        await ReceiveAsync(501, "135000005", "E310000000301", "ecf");
        await ReceiveAsync(502, "136000006", "E310000000302", "ecf");     // a supplier not registered yet
        await ReceiveAsync(503, "135000005", "E310000000303", "testecf"); // a test-environment e-CF is not a purchase
        var received = (await c.GetFromJsonAsync<JsonElement>("/api/fiscal/received")).EnumerateArray()
            .ToDictionary(r => r.GetProperty("eNcf").GetString()!, r => r.GetProperty("id").GetGuid());

        // "Registrar compra": supplier, e-NCF, date and totals from the e-CF; the unit cost without ITBIS; the line matched to the product.
        var r = await c.PostAsJsonAsync("/api/purchasing/purchases/from-ecf", new { ReceivedId = received["E310000000301"] });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var read = await r.Json();
        (read.GetProperty("supplierId").GetGuid(), read.GetProperty("ncf").GetString(), read.GetProperty("source").GetString()).ShouldBe((known, "E310000000301", "ecf"));
        (read.GetProperty("total").GetDecimal(), read.GetProperty("itbis").GetDecimal(), read.GetProperty("date").GetString()).ShouldBe((1180m, 180m, Day.ToString("yyyy-MM-dd")));
        var lines = read.GetProperty("lines").EnumerateArray().ToList();
        (lines[0].GetProperty("productId").GetGuid(), lines[0].GetProperty("unitCost").GetDecimal(), lines[0].GetProperty("quantity").GetDecimal()).ShouldBe((product, 100m, 10m));
        lines[1].GetProperty("itbisRate").GetString().ShouldBe("Exempt");

        var other = await (await c.PostAsJsonAsync("/api/purchasing/purchases/from-ecf", new { ReceivedId = received["E310000000302"] })).Json();
        other.GetProperty("supplierId").ValueKind.ShouldBe(JsonValueKind.String); // created from the RNC
        (await (await c.PostAsJsonAsync("/api/purchasing/purchases/from-ecf", new { ReceivedId = received["E310000000303"] })).ErrorCode()).ShouldBe("Purchasing.ReceivedEcfIsTest");

        // The e-CF XML uploaded instead of a photo: read here, no AI and no cloud.
        var xml = new { File = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(SupplierEcf("135000005", "E310000000399"))), ContentType = "text/xml", FileName = "e-cf.xml" };
        var fromXml = await (await c.PostAsJsonAsync("/api/purchasing/purchases/read-invoice", xml)).Json();
        (fromXml.GetProperty("source").GetString(), fromXml.GetProperty("ncf").GetString()).ShouldBe(("ecf", "E310000000399"));
        (await (await c.PostAsJsonAsync("/api/purchasing/purchases/read-invoice", xml with { File = Convert.ToBase64String("<Factura/>"u8.ToArray()) })).ErrorCode())
            .ShouldBe("Purchasing.NotAnEcf");

        // A photo of an invoice the supplier also sent as e-CF: the e-CF's data, not the AI's.
        await app.ConnectAsync();
        app.Cloud.Respond = path => path != "/ai/v1/supplier-invoice" ? null : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                supplierRnc = "135000005", ncf = "E310000000301", date = Day, lines = new object[] { new { description = "Costiya", quantity = 9m, unitCost = 99m } },
                total = 999m, confidence = 0.5m, doubtful = new[] { "lines" },
            }),
        };
        var png = new { File = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]), ContentType = "image/png", FileName = "factura.png" };
        var ai = await (await c.PostAsJsonAsync("/api/purchasing/purchases/read-invoice", png)).Json();
        (ai.GetProperty("source").GetString(), ai.GetProperty("total").GetDecimal()).ShouldBe(("ecf", 1180m));
        ai.GetProperty("lines")[0].GetProperty("quantity").GetDecimal().ShouldBe(10m);

        // The original invoice goes with the saved purchase and comes back as it was.
        var saved = await c.PostAsJsonAsync("/api/purchasing/purchases", new
        {
            SupplierId = known, Ncf = "E310000000301", InvoiceDate = Day, Terms = "Cash", Lines = new[] { new { ProductId = product, Quantity = 10m, UnitCost = 100m, ItbisRate = "General18" } },
        });
        var id = (await saved.Json()).GetProperty("id").GetGuid();
        (await (await c.GetAsync($"/api/purchasing/purchases/{id}/attachment")).ErrorCode()).ShouldBe("Purchasing.NoAttachment");
        (await (await c.PutAsJsonAsync($"/api/purchasing/purchases/{id}/attachment", png with { ContentType = "text/plain" })).ErrorCode()).ShouldBe("Purchasing.InvalidAttachment");
        (await c.PutAsJsonAsync($"/api/purchasing/purchases/{id}/attachment", png)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await c.GetFromJsonAsync<JsonElement>($"/api/purchasing/purchases/{id}")).GetProperty("attachmentName").GetString().ShouldBe("factura.png");
        var file = await c.GetAsync($"/api/purchasing/purchases/{id}/attachment");
        file.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await file.Content.ReadAsByteArrayAsync()).ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 });
    }

}
