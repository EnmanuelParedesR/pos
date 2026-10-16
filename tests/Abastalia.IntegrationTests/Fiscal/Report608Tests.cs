using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Domain;
using Abastalia.IntegrationTests.Infra;
using static Abastalia.IntegrationTests.Infra.Store;

namespace Abastalia.IntegrationTests.Fiscal;

/// <summary>TK-192: the 608 lists the e-NCF voided with an ANECF and the ones the DGII rejected; with the 607 it accounts for every number used,
/// and a sale voided in its shift stays in the 607 (with its credit note), not in the 608. Own app: the e-NCF numbers are exact.</summary>
public sealed class Report608Tests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    [Fact]
    public async Task The_607_and_the_608_together_cover_every_number_used()
    {
        var admin = app.Client(await app.AdminAsync());
        var cashier = await app.CashierWithShiftAsync();
        var soda = await admin.ProductAsync(await admin.CategoryAsync("Bebidas"), "Refresco", 59m, stock: 10);
        var sales = new List<Guid>();
        for (var i = 0; i < 3; i++)
            sales.Add((await (await cashier.SellAsync(Sale(cashier.ShiftId, 59m, 59m, Line(soda, 1m)))).Json()).GetProperty("saleId").GetGuid());
        (await cashier.Client.PostAsJsonAsync($"/api/sales/{sales[1]}/void",
            new { Reason = "Error", SupervisorApprovalId = await cashier.Client.ApproveAsync("sales.void", sales[1]) })).EnsureSuccessStatusCode();
        var consumer = (await admin.GetFromJsonAsync<JsonElement>("/api/fiscal/sequences")).EnumerateArray()
            .Single(s => s.GetProperty("type").GetString() == "Consumer32" && s.GetProperty("current").GetBoolean()).GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync($"/api/fiscal/sequences/{consumer}/void", new { To = 6 })).EnsureSuccessStatusCode();

        var today = DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime.AddHours(-4));
        var period = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        var r608 = (await admin.GetFromJsonAsync<JsonElement>($"/api/reports/dgii/608?{period}")).EnumerateArray().ToList();
        r608.Select(r => r.GetProperty("ncf").GetString()).ShouldBe(["E320000000004", "E320000000005", "E320000000006"]);
        r608.ShouldAllBe(r => r.GetProperty("voidType").GetString() == "08" && r.GetProperty("documentDate").GetString() == today.ToString("yyyy-MM-dd"));
        var r607 = (await admin.GetFromJsonAsync<JsonElement>($"/api/reports/dgii/607?{period}")).EnumerateArray().Select(r => r.GetProperty("ncf").GetString()).ToList();
        r607.Order().ShouldBe(["E320000000001", "E320000000002", "E320000000003", "E340000000001"]);
        r607.Concat(r608.Select(r => r.GetProperty("ncf").GetString())).Where(n => n!.StartsWith("E32", StringComparison.Ordinal)).Order()
            .ShouldBe(Enumerable.Range(1, 6).Select(n => $"E32{n:0000000000}")); // every 32 consumed so far, once

        var csv = Encoding.UTF8.GetString(await admin.GetByteArrayAsync($"/api/reports/dgii/608?format=csv&{period}")).TrimStart('﻿').Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        (csv[0], csv[1]).ShouldBe(("Ncf,FechaComprobante,TipoAnulacion", $"E320000000004,{today:yyyy-MM-dd},08"));

        // A document the DGII rejected consumed its number: it goes in the 608 as a correction (04).
        await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.Where(d => d.ENcf == "E320000000003")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, SubmissionStatus.Rejected)));
        var rejected = (await admin.GetFromJsonAsync<JsonElement>($"/api/reports/dgii/608?{period}")).EnumerateArray().Single(r => r.GetProperty("ncf").GetString() == "E320000000003");
        rejected.GetProperty("voidType").GetString().ShouldBe("04");

        // R8 #3: not in both. Until it is replaced the 607 is not exported (its sale would be missing), and the rows leave it out.
        var blocked = await admin.GetAsync($"/api/reports/dgii/607?{period}");
        (await blocked.ErrorCode()).ShouldBe("Reports.DocumentsToReplace");
        (await blocked.Content.ReadAsStringAsync()).ShouldContain("Hay 1 comprobante anulado, rechazado o serie B por reemplazar");
        (await app.InScopeAsync(sp => sp.GetRequiredService<Abastalia.Sales.Contracts.ISalesReports>().Report607Async(today, today, CancellationToken.None)))
            .Select(r => r.Ncf).ShouldNotContain("E320000000003");

        var id = await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.Where(d => d.ENcf == "E320000000003").Select(d => d.Id).SingleAsync());
        (await admin.PostAsync($"/api/fiscal/documents/{id}/replace", null)).EnsureSuccessStatusCode();
        await app.InScopeAsync(sp => sp.GetRequiredService<Abastalia.BuildingBlocks.Messaging.OutboxProcessor>().ProcessAsync(CancellationToken.None));
        r607 = (await admin.GetFromJsonAsync<JsonElement>($"/api/reports/dgii/607?{period}")).EnumerateArray().Select(r => r.GetProperty("ncf").GetString()).ToList();
        r607.ShouldNotContain("E320000000003");
        r607.ShouldContain("E320000000007"); // the replacement, after the voided 4–6
    }
}
