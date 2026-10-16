using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.Fiscal;
using Toki.Fiscal.Domain;
using Toki.IntegrationTests.Infra;
using Toki.IntegrationTests.Sync;

namespace Toki.IntegrationTests.Purchasing;

/// <summary>TK-223 (cross with the DGII Excel of received documents) and TK-224 (purchase draft from the supplier invoice read by the cloud AI).</summary>
public sealed class DgiiReconciliationTests(TokiApp app) : IClassFixture<TokiApp>
{
    private static readonly DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);

    private static async Task<Guid> SupplierAsync(HttpClient c, string rnc) =>
        (await (await c.PostAsJsonAsync("/api/purchasing/suppliers", new { Name = "Suplidor " + rnc, Rnc = rnc, CreditDays = 0 })).Json()).GetProperty("id").GetGuid();

    private static async Task<Guid> PostedPurchaseAsync(HttpClient c, Guid supplier, Guid product, string ncf, decimal cost)
    {
        var r = await c.PostAsJsonAsync("/api/purchasing/purchases", new
        {
            SupplierId = supplier, Ncf = ncf, InvoiceDate = Day, Terms = "Cash", Lines = new[] { new { ProductId = product, Quantity = 1m, UnitCost = cost, ItbisRate = "General18" } },
        });
        r.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await r.Json()).GetProperty("id").GetGuid();
        (await c.PostAsync($"/api/purchasing/purchases/{id}/post", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        return id;
    }

    /// <summary>As the OFV might export it: a title above the header, columns in its own order and names, a voided document.</summary>
    private static string Excel(params (string Rnc, string Name, string Ncf, object Date, decimal Itbis, decimal Total, string Status)[] rows)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Comprobantes");
        sheet.Cell(1, 1).Value = "Consulta de comprobantes recibidos";
        string[] header = ["RNC/Cédula Emisor", "Razón Social Emisor", "Tipo", "e-NCF / NCF", "Fecha de Emisión", "RNC Comprador", "ITBIS Facturado", "ITBIS Retenido", "Monto Total", "Estado"];
        for (var i = 0; i < header.Length; i++) sheet.Cell(3, i + 1).Value = header[i];
        for (var r = 0; r < rows.Length; r++)
        {
            var (rnc, name, ncf, date, itbis, total, status) = rows[r];
            var row = r + 4;
            sheet.Cell(row, 1).Value = rnc;
            sheet.Cell(row, 2).Value = name;
            sheet.Cell(row, 3).Value = ncf[..3];
            sheet.Cell(row, 4).Value = ncf;
            sheet.Cell(row, 5).Value = date is DateTime dt ? dt : (string)date;
            sheet.Cell(row, 6).Value = "130000001";
            sheet.Cell(row, 7).Value = itbis;
            sheet.Cell(row, 8).Value = 0;
            sheet.Cell(row, 9).Value = total;
            sheet.Cell(row, 10).Value = status;
        }
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    private static async Task<JsonElement> CrossAsync(HttpClient c, string file)
    {
        var r = await c.PostAsJsonAsync("/api/purchasing/dgii-reconciliation", new { File = file, Name = "recibidos.xlsx" });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await r.Json();
    }

    private static string Status(JsonElement cross, string ncf, int nth = 0) =>
        cross.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("ncf").GetString() == ncf).ElementAt(nth).GetProperty("status").GetString()!;

    [Fact]
    public async Task The_DGII_Excel_shows_what_is_missing_repeated_or_different_and_a_missing_row_becomes_a_draft_purchase()
    {
        var c = app.Client(await app.AdminAsync());
        var product = await c.ProductAsync(await c.CategoryAsync(), "Costilla de cerdo", 200m, cost: 100m);
        var known = await SupplierAsync(c, "131000001");
        await PostedPurchaseAsync(c, known, product, "B0100000001", 100m);             // 100 + 18 = 118
        await PostedPurchaseAsync(c, known, product, "B0100000002", 200m);             // 236; the DGII says 300
        var onlyHere = await PostedPurchaseAsync(c, known, product, "B0100000009", 50m); // not in the Excel
        (await c.PostAsJsonAsync("/api/purchasing/expenses", new
        {
            SupplierId = known, Ncf = "E310000000001", Date = Day, Description = "Luz", Type = "WorkSuppliesAndServices", Subtotal = 1000m, Itbis = 180m, Terms = "Cash",
        })).StatusCode.ShouldBe(HttpStatusCode.Created);
        await app.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<FiscalDbContext>();
            db.Taxpayers.Add(new Taxpayer { Rnc = "132000002", Name = "GRANJA LOS PINOS SRL", IsActive = true });
            return await db.SaveChangesAsync();
        });
        var date = Day.ToDateTime(TimeOnly.MinValue);
        var file = Excel(
            ("131000001", "Suplidor", "B0100000001", date, 18m, 118m, "Aceptado"),
            ("131-00000-1", "Suplidor", "B0100000002", Day.ToString("dd/MM/yyyy"), 54m, 300m, "Aceptado"),
            ("131000001", "Suplidor", "E310000000001", date, 180m, 1180m, "Aceptado"),
            ("132000002", "Granja (nombre en la DGII)", "B0100000777", date, 90m, 590m, "Aceptado"),
            ("132000002", "Granja (nombre en la DGII)", "B0100000777", date, 90m, 590m, "Aceptado"),
            ("132000002", "Granja", "B0100000778", date, 18m, 118m, "Anulado"));

        var cross = await CrossAsync(c, file);

        cross.GetProperty("rows").GetInt32().ShouldBe(5); // the voided one is left out
        Status(cross, "B0100000001").ShouldBe("Matched");
        Status(cross, "B0100000002").ShouldBe("Difference");
        Status(cross, "E310000000001").ShouldBe("Matched");
        Status(cross, "B0100000777").ShouldBe("Missing");
        Status(cross, "B0100000777", 1).ShouldBe("Duplicate");
        var extra = cross.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("ncf").GetString() == "B0100000009");
        extra.GetProperty("status").GetString().ShouldBe("OnlyInToki");
        extra.GetProperty("documentId").GetGuid().ShouldBe(onlyHere);
        cross.GetProperty("missing").GetInt32().ShouldBe(1);

        // One click: the supplier is created with the registry's name, and the draft carries the invoice totals.
        var body = new { Rnc = "132000002", Name = "Granja (nombre en la DGII)", Ncf = "B0100000777", Date = Day, Total = 590m, Itbis = 90m };
        var created = await c.PostAsJsonAsync("/api/purchasing/dgii-reconciliation/draft", body);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var draft = await created.Json();
        draft.GetProperty("supplier").GetString().ShouldBe("GRANJA LOS PINOS SRL");
        (await (await c.PostAsJsonAsync("/api/purchasing/dgii-reconciliation/draft", body)).ErrorCode()).ShouldBe("Purchasing.DuplicateNcf");
        var id = draft.GetProperty("purchaseId").GetGuid();
        var saved = await c.GetFromJsonAsync<JsonElement>($"/api/purchasing/purchases/{id}");
        saved.GetProperty("status").GetString().ShouldBe("Draft");
        saved.GetProperty("total").GetDecimal().ShouldBe(590m);

        // Nothing moves until a person adds the products.
        (await (await c.PostAsync($"/api/purchasing/purchases/{id}/post", null)).ErrorCode()).ShouldBe("Purchasing.LinesRequired");
        (await c.PutAsJsonAsync($"/api/purchasing/purchases/{id}", new
        {
            SupplierId = draft.GetProperty("supplierId").GetGuid(), Ncf = "B0100000777", InvoiceDate = Day, Terms = "Cash", InvoiceTotal = 590m,
            Lines = new[] { new { ProductId = product, Quantity = 5m, UnitCost = 100m, ItbisRate = "General18" } },
        })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await c.PostAsync($"/api/purchasing/purchases/{id}/post", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Status(await CrossAsync(c, file), "B0100000777").ShouldBe("Matched");
    }

    [Fact]
    public async Task A_file_that_is_not_the_DGII_Excel_is_refused()
    {
        var c = app.Client(await app.AdminAsync());
        var r = await c.PostAsJsonAsync("/api/purchasing/dgii-reconciliation", new { File = Convert.ToBase64String("hola"u8.ToArray()), Name = "x.xlsx" });
        (await r.ErrorCode()).ShouldBe("Purchasing.DgiiFileUnreadable");
    }

    [Fact]
    public async Task The_supplier_invoice_is_read_by_the_cloud_and_checked_but_nothing_is_saved()
    {
        var c = app.Client(await app.AdminAsync());
        var png = new { File = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]), ContentType = "image/png", FileName = "factura.png" };

        // Not connected to the cloud: a clear message, no AI.
        (await (await c.PostAsJsonAsync("/api/purchasing/purchases/read-invoice", png)).ErrorCode()).ShouldBe("Purchasing.AiNeedsCloud");

        var product = await c.ProductAsync(await c.CategoryAsync(), "Chuleta ahumada", 250m, cost: 120m);
        var supplier = await SupplierAsync(c, "133000003");
        await PostedPurchaseAsync(c, supplier, product, "B0100000555", 120m);
        await app.ConnectAsync();
        app.Cloud.Respond = path => path != "/ai/v1/supplier-invoice" ? null : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                supplierRnc = "133-00000-3", supplierName = "Suplidor", ncf = "B01 0000 0556", date = Day,
                lines = new object[] { new { description = "CHULETA AHUMADA", quantity = 10m, unitCost = 125m, itbisPercent = 18m }, new { description = "Tocineta", quantity = 2m, unitCost = 80m } },
                subtotal = 1410m, itbis = 253.8m, total = 1663.8m, confidence = 0.9m, doubtful = Array.Empty<string>(),
            }),
        };
        var purchasesBefore = (await c.GetFromJsonAsync<JsonElement>("/api/purchasing/purchases")).GetArrayLength();

        var r = await c.PostAsJsonAsync("/api/purchasing/purchases/read-invoice", png);

        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        var read = await r.Json();
        read.GetProperty("supplierId").GetGuid().ShouldBe(supplier);
        read.GetProperty("ncf").GetString().ShouldBe("B0100000556");
        var lines = read.GetProperty("lines").EnumerateArray().ToList();
        lines[0].GetProperty("productId").GetGuid().ShouldBe(product); // bought from this supplier before
        lines[0].GetProperty("itbisRate").GetString().ShouldBe("General18");
        lines[1].GetProperty("productId").ValueKind.ShouldBe(JsonValueKind.Null);
        read.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ShouldContain("Algunos productos no se reconocieron: elija cada uno en la lista.");
        var sent = app.Cloud.To("/ai/v1/supplier-invoice").ShouldHaveSingleItem();
        sent.Authorization!.ShouldStartWith("Node ");
        sent.Body.GetProperty("contentType").GetString().ShouldBe("image/png");
        (await c.GetFromJsonAsync<JsonElement>("/api/purchasing/purchases")).GetArrayLength().ShouldBe(purchasesBefore);

        // A file that is not a PDF or a photo never leaves the store.
        (await (await c.PostAsJsonAsync("/api/purchasing/purchases/read-invoice", png with { ContentType = "text/plain" })).ErrorCode()).ShouldBe("Purchasing.AiInvalidFile");
    }
}
