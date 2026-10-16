using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Abastalia.E2ETests;

/// <summary>TK-095: a whole store day against the real process over HTTP. Three registers sell at the same time, the server
/// restarts in the middle of the day, and at the end every total must add up: drawers, kardex, e-NCF numbers, documents,
/// customer and supplier balances, and the audit chain. No request may answer 500.</summary>
public sealed class StoreDayTests
{
    private const string AdminPin = "1234", SupervisorPin = "2468", CashierPin = "4321";
    private static readonly Guid SupervisorRole = new("0199e000-0000-7000-8000-000000000002"), CashierRole = new("0199e000-0000-7000-8000-000000000004"); // Cajero con apertura
    private static readonly decimal[] Denominations = [2000, 1000, 500, 200, 100, 50, 25, 10, 5, 1];

    /// <summary>A register: its device, cashier session, shift, and the cash the test expects in its drawer (an independent model of the server's).</summary>
    private sealed class Register(string name, HttpClient client)
    {
        public string Name { get; } = name;
        public HttpClient Client { get; } = client;
        public Guid RegisterId { get; set; }
        public Guid ShiftId { get; set; }
        public decimal Cash { get; set; }
        public List<(Guid SaleId, decimal Total, decimal CashKept)> Sales { get; } = [];
    }

    [Fact]
    public async Task A_full_store_day()
    {
        await using var server = await AbastaliaServer.StartAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-4));

        // ── Setup: owner PC, three paired registers, users ──
        var setup = await Ok(await server.Client().PostAsJsonAsync("/api/identity/setup",
            new { AdministratorName = "Dueña", AdministratorCode = "admin", Pin = AdminPin, DeviceName = "PC de la oficina" }));
        var officeDevice = setup.GetProperty("deviceToken").GetString()!;
        var admin = server.Client(officeDevice, await SessionAsync(server, officeDevice, "admin", AdminPin));
        await Ok(await admin.PostAsJsonAsync("/api/identity/users", new { Name = "Supervisora", Code = "sup", Pin = SupervisorPin, RoleId = SupervisorRole }));
        await Ok(await admin.PutAsJsonAsync("/api/settings/business", new { Name = "Carnicería La Esquina", Rnc = "131245678", DiscountCapPercent = 10m }));
        // Three registers need a license (a trial allows two).
        await Ok(await admin.PutAsJsonAsync("/api/settings/license", new { License = SignedLicense(server.LicenseKey, registers: 3) }));
        foreach (var type in new[] { 31, 32, 34 })
            await Ok(await admin.PostAsJsonAsync("/api/fiscal/sequences", new { Type = type, From = 1, To = 10_000, ExpiresOn = today.AddYears(1) }));

        var registers = new List<Register>();
        for (var i = 1; i <= 3; i++)
        {
            var code = (await Ok(await admin.PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = $"Caja {i}" }))).GetProperty("code").GetString();
            var device = (await Ok(await server.Client().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code }))).GetProperty("token").GetString()!;
            await Ok(await admin.PostAsJsonAsync("/api/identity/users", new { Name = $"Cajero {i}", Code = $"caj{i}", Pin = CashierPin, RoleId = CashierRole }));
            var register = new Register($"Caja {i}", server.Client(device, await SessionAsync(server, device, $"caj{i}", CashierPin)));
            register.RegisterId = (await Ok(await admin.PostAsJsonAsync("/api/cash/registers", new { Name = register.Name }))).GetProperty("id").GetGuid();
            registers.Add(register);
        }

        // ── Catalog and the morning's purchases (the day sells at the new average cost) ──
        var meats = (await Ok(await admin.PostAsJsonAsync("/api/inventory/categories", new { Name = "Carnes", Color = "#B42318", Notes = Array.Empty<string>() }))).GetProperty("id").GetGuid();
        var grocery = (await Ok(await admin.PostAsJsonAsync("/api/inventory/categories", new { Name = "Colmado", Color = "#0B5CAD", Notes = Array.Empty<string>() }))).GetProperty("id").GetGuid();
        async Task<Guid> Product(string name, Guid category, decimal price, string unit, string rate, decimal? wholesale = null, string? plu = null) =>
            (await Ok(await admin.PostAsJsonAsync("/api/inventory/products", new
            {
                Name = name, CategoryId = category, Unit = unit, Plu = plu, ItbisRate = rate, Price = price, WholesalePrice = wholesale, Cost = 0m,
                SellWithoutStock = false, MinimumStock = 0, Barcodes = Array.Empty<string>(), InitialStock = 0m,
            }))).GetProperty("id").GetGuid();
        var porkChop = await Product("Chuleta", meats, 165m, "Pound", "Exempt", plu: "1234");
        var soda = await Product("Refresco 2L", grocery, 59m, "Unit", "General18");
        var rice = await Product("Arroz 1 lb", grocery, 50m, "Unit", "Exempt", wholesale: 45m);
        var cheese = await Product("Queso de freír", grocery, 50m, "Unit", "General18");
        var pack = (await Ok(await admin.PostAsJsonAsync($"/api/inventory/products/{soda}/presentations", new { Name = "Fardo 8", Factor = 8m, Price = 440m }))).GetProperty("id").GetGuid();
        var products = new[] { porkChop, soda, rice, cheese };

        var supplier = (await Ok(await admin.PostAsJsonAsync("/api/purchasing/suppliers", new { Name = "Distribuidora del Cibao", Rnc = "101234567", CreditDays = 15 }))).GetProperty("id").GetGuid();
        async Task<Guid> Purchase(string terms, params object[] lines)
        {
            var id = (await Ok(await admin.PostAsJsonAsync("/api/purchasing/purchases", new
            {
                Id = Guid.CreateVersion7(), SupplierId = supplier, Ncf = "B01" + Random.Shared.Next(10_000_000, 99_999_999), InvoiceDate = today, Terms = terms, Lines = lines,
            }))).GetProperty("id").GetGuid();
            await Ok(await admin.PostAsync($"/api/purchasing/purchases/{id}/post", null));
            return id;
        }
        await Purchase("Credit", new { ProductId = porkChop, Quantity = 300m, UnitCost = 110m });
        await Purchase("Cash", new { ProductId = soda, PresentationId = pack, Quantity = 40m, UnitCost = 280m }, new { ProductId = rice, Quantity = 500m, UnitCost = 30m },
            new { ProductId = cheese, Quantity = 100m, UnitCost = 25m });
        // A typo (250 instead of 25), voided: the cost must come back to 25.
        var typo = await Purchase("Cash", new { ProductId = cheese, Quantity = 50m, UnitCost = 250m });
        await Ok(await admin.PostAsJsonAsync($"/api/purchasing/purchases/{typo}/void", new { Reason = "costo mal escrito" }));
        (await Ok(await admin.GetAsync($"/api/inventory/products/{cheese}"))).GetProperty("cost").GetDecimal().ShouldBe(25m);

        var neighbor = (await Ok(await admin.PostAsJsonAsync("/api/customers", new { Name = "Colmado Vecino", CreditLimit = 3000m }))).GetProperty("id").GetGuid();
        var wholesaler = (await Ok(await admin.PostAsJsonAsync("/api/customers", new { Name = "Hotel Playa SRL", TaxId = "130987654", PriceLevel = "Wholesale", FiscalCredit = true }))).GetProperty("id").GetGuid();

        // ── Opening: each cashier on their own register ──
        foreach (var register in registers)
        {
            register.ShiftId = (await Ok(await register.Client.PostAsJsonAsync("/api/cash/shifts", new { RegisterId = register.RegisterId, OpeningFloat = 2000m }))).GetProperty("shiftId").GetGuid();
            register.Cash = 2000m;
        }

        // ── Rush hour: the three registers sell the same products at the same time, in opposite scan orders, with mixed payments and price labels ──
        await Task.WhenAll(registers.Select((register, n) => Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                object[] lines = (i + n) % 2 == 0
                    ? [new { ProductId = porkChop, Quantity = 1.35m + i / 10m }, new { ProductId = soda, Quantity = 2m }]
                    : [new { ProductId = soda, Quantity = 1m }, new { ProductId = rice, Quantity = 3m }, new { ProductId = porkChop, Quantity = 2.10m }];
                if (i == 3) lines = [.. lines, new { ProductId = porkChop, Quantity = 1m, ScaleLabel = PriceLabel("1234", 412.50m) }];
                await SellAsync(register, lines, method: (i % 3) switch { 0 => "Cash", 1 => "Card", _ => "Mixed" });
            }
        })));

        // ── The server restarts in the middle of the day (power cut): sessions and data survive ──
        await server.RestartAsync();
        var (r1, r2, r3) = (registers[0], registers[1], registers[2]);

        // Credit fiscal sale with the customer's RNC, and a wholesale sale (the customer is wholesale: no supervisor).
        (await SellAsync(r1, [new { ProductId = rice, Quantity = 40m }], extra: new() { ["CustomerId"] = wholesaler, ["PriceLevel"] = "Wholesale", ["DocumentType"] = "FiscalCredit31" }))
            .GetProperty("eNcf").GetString()!.ShouldStartWith("E31");

        // A held cart on register 2, resumed on the same register.
        var cart = Guid.CreateVersion7();
        await Ok(await r2.Client.PutAsJsonAsync("/api/sales/held", new { Id = cart, ShiftId = r2.ShiftId, Label = "Señora del pollo", Total = 177m, Content = new { lines = 1 } }));
        (await Ok(await r2.Client.GetAsync($"/api/sales/held?shiftId={r2.ShiftId}"))).GetArrayLength().ShouldBe(1);
        await Ok(await r2.Client.DeleteAsync($"/api/sales/held/{cart}?shiftId={r2.ShiftId}"));
        await SellAsync(r2, [new { ProductId = soda, Quantity = 3m }]);

        // A discount over the cap needs the supervisor.
        var discounted = Guid.CreateVersion7();
        await SellAsync(r3, [new { ProductId = soda, Quantity = 4m, Discount = new { IsPercentage = true, Value = 20m } }], saleId: discounted);

        // A double-tapped void: one goes through, the other finds it voided.
        var toVoid = r1.Sales.First(v => v.CashKept > 0);
        var voids = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => await r1.Client.PostAsJsonAsync($"/api/sales/{toVoid.SaleId}/void",
            new { Reason = "cliente se arrepintió", SupervisorApprovalId = await ApproveAsync(r1, "sales.void", toVoid.SaleId) })));
        voids.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        r1.Cash -= toVoid.CashKept;
        r1.Sales.Remove(toVoid);

        // A return on register 2's shift of a sale made on register 1 (one refresco back to the shelf, cash). The owner finds the sale (R11 A13:
        // a cashier reads only their own).
        var fromRegister1 = r1.Sales.First(v => v.CashKept >= 60m); // the refund cannot exceed the cash this sale took
        var sodaLine = (await Ok(await admin.GetAsync($"/api/sales/{fromRegister1.SaleId}"))).GetProperty("lines").EnumerateArray()
            .First(l => l.GetProperty("description").GetString() == "Refresco 2L").GetProperty("id").GetGuid();
        await ReturnAsync(r2, fromRegister1.SaleId, sodaLine, 1m, "Cash");
        r2.Cash -= 59m;

        // The last piece: 3 cheeses for 100.00 by card, returned 1 + 1 + 1 to the card.
        var cheeses = Guid.CreateVersion7();
        await SellAsync(r3, [new { ProductId = cheese, Quantity = 3m, Discount = new { IsPercentage = false, Value = 50m } }], method: "Card", saleId: cheeses);
        var cheeseLine = (await Ok(await r3.Client.GetAsync($"/api/sales/{cheeses}"))).GetProperty("lines")[0].GetProperty("id").GetGuid();
        var returned = new List<decimal>();
        for (var i = 0; i < 3; i++) returned.Add((await ReturnAsync(r3, cheeses, cheeseLine, 1m, "Card")).GetProperty("total").GetDecimal());
        returned.ShouldBe([33.33m, 33.33m, 33.34m]);

        // On account (fiado): within the limit, a pound returned to the account, then over the limit with the supervisor;
        // payments in cash and by card; a voided on-account sale.
        var onAccount = Guid.CreateVersion7();
        await SellAsync(r2, [new { ProductId = porkChop, Quantity = 10m }], method: "OnAccount", saleId: onAccount, extra: new() { ["CustomerId"] = neighbor });
        var onAccountLine = (await Ok(await r2.Client.GetAsync($"/api/sales/{onAccount}"))).GetProperty("lines")[0].GetProperty("id").GetGuid();
        (await ReturnAsync(r2, onAccount, onAccountLine, 1m, "CreditBalance")).GetProperty("total").GetDecimal().ShouldBe(165m);
        var overLimit = Guid.CreateVersion7();
        await SellAsync(r2, [new { ProductId = porkChop, Quantity = 10m }], method: "OnAccount", saleId: overLimit, extra: new() { ["CustomerId"] = neighbor });
        await Ok(await r1.Client.PostAsJsonAsync($"/api/customers/{neighbor}/payments", new { CustomerPaymentId = Guid.CreateVersion7(), ShiftId = r1.ShiftId, Amount = 1000m, Method = "Cash" }));
        r1.Cash += 1000m;
        await Ok(await r3.Client.PostAsJsonAsync($"/api/customers/{neighbor}/payments", new { CustomerPaymentId = Guid.CreateVersion7(), ShiftId = r3.ShiftId, Amount = 300m, Method = "Card" }));
        await Ok(await r2.Client.PostAsJsonAsync($"/api/sales/{overLimit}/void",
            new { Reason = "se llevó menos", SupervisorApprovalId = await ApproveAsync(r2, "sales.void", overLimit) }));
        r2.Sales.RemoveAll(v => v.SaleId == overLimit);

        // A cash drop and a petty expense, both with the supervisor; a supplier paid by transfer.
        await Ok(await SupervisedAsync(r1, r1.ShiftId, approval => r1.Client.PostAsJsonAsync($"/api/cash/shifts/{r1.ShiftId}/movements",
            new { MovementId = Guid.NewGuid(), MovementType = "Withdrawal", Amount = 1500m, Reason = "CashPickup", SupervisorApprovalId = approval })));
        r1.Cash -= 1500m;
        await Ok(await SupervisedAsync(r2, r2.ShiftId, approval => r2.Client.PostAsJsonAsync($"/api/cash/shifts/{r2.ShiftId}/movements",
            new { MovementId = Guid.NewGuid(), MovementType = "Expense", Amount = 150m, Reason = "PettyExpense", Note = "Hielo", SupervisorApprovalId = approval })));
        r2.Cash -= 150m;
        await Ok(await admin.PostAsJsonAsync("/api/purchasing/payments", new { SupplierPaymentId = Guid.CreateVersion7(), SupplierId = supplier, Amount = 20000m, Method = "Transfer", Date = today }));

        // ── Closing: the supervisor sees the X report; each drawer is counted blind and must match ──
        var takenInDrawers = 0m;
        foreach (var register in registers)
        {
            var x = await Ok(await admin.GetAsync($"/api/cash/shifts/{register.ShiftId}/z"));
            x.GetProperty("expectedCash").GetDecimal().ShouldBe(register.Cash, $"{register.Name}: expected cash does not match what went through the register");
            var z = await Ok(await SupervisedAsync(register, register.ShiftId, approval => register.Client.PostAsJsonAsync($"/api/cash/shifts/{register.ShiftId}/close",
                new { CashCount = Count(register.Cash), SupervisorApprovalId = approval })));
            z.GetProperty("difference").GetDecimal().ShouldBe(0m);
            z.GetProperty("auditChainHead").GetString()!.ShouldStartWith("#");
            takenInDrawers += z.GetProperty("byMethod").EnumerateArray()
                .Sum(m => m.GetProperty("salePayments").GetDecimal() - m.GetProperty("voids").GetDecimal() - m.GetProperty("refunds").GetDecimal());
        }

        // ── End of day: everything must add up ──
        (await Ok(await admin.GetAsync("/api/audit/verify"))).GetProperty("isIntact").GetBoolean().ShouldBeTrue();

        // Kardex: the movements of each product add up to its stock.
        var stock = (await Ok(await admin.GetAsync("/api/inventory/stock"))).EnumerateArray().ToDictionary(e => e.GetProperty("productId").GetGuid(), e => e.GetProperty("quantity").GetDecimal());
        foreach (var p in products)
            (await Ok(await admin.GetAsync($"/api/inventory/products/{p}/movements?last=1000"))).EnumerateArray().Sum(m => m.GetProperty("quantity").GetDecimal()).ShouldBe(stock[p]);

        // e-NCF: per type, 1..N with no gap and no repeat; each sale document carries the sale's total.
        var documents = (await Ok(await admin.GetAsync("/api/fiscal/documents?last=1000"))).EnumerateArray().ToList();
        foreach (var type in documents.GroupBy(d => d.GetProperty("eNcf").GetString()![..3]))
            type.Select(d => long.Parse(d.GetProperty("eNcf").GetString()![3..], CultureInfo.InvariantCulture)).Order().ShouldBe(Enumerable.Range(1, type.Count()).Select(n => (long)n));
        foreach (var d in documents.Where(d => !d.GetProperty("eNcf").GetString()!.StartsWith("E34", StringComparison.Ordinal)))
        {
            var sale = await Ok(await admin.GetAsync($"/api/sales/{d.GetProperty("referenceId").GetGuid()}"));
            sale.GetProperty("total").GetDecimal().ShouldBe(d.GetProperty("total").GetDecimal());
        }

        // Customer: card debt = statement's last running debt = receivables aging.
        var card = await Ok(await admin.GetAsync($"/api/customers/{neighbor}"));
        var statement = await Ok(await admin.GetAsync($"/api/customers/{neighbor}/statement"));
        var receivable = (await Ok(await admin.GetAsync("/api/reports/receivables"))).EnumerateArray().Single(c => c.GetProperty("customerId").GetGuid() == neighbor);
        card.GetProperty("debt").GetDecimal().ShouldBe(10 * 165m - 165m - 1300m);
        statement.GetProperty("lines").EnumerateArray().Last().GetProperty("debt").GetDecimal().ShouldBe(card.GetProperty("debt").GetDecimal());
        receivable.GetProperty("total").GetDecimal().ShouldBe(card.GetProperty("debt").GetDecimal());

        // Supplier: statement balance = payables aging.
        var supplierBalance = (await Ok(await admin.GetAsync($"/api/purchasing/suppliers/{supplier}/statement"))).GetProperty("balance").GetDecimal();
        supplierBalance.ShouldBe(300 * 110m - 20000m);
        (await Ok(await admin.GetAsync("/api/reports/payables"))).EnumerateArray().Single(s => s.GetProperty("supplierId").GetGuid() == supplier)
            .GetProperty("total").GetDecimal().ShouldBe(supplierBalance);

        // Reports reconcile with the closings, the fiscal documents and the stock. (Tomorrow is included: a run near midnight splits the day.)
        var period = $"from={today:yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}";
        var days = (await Ok(await admin.GetAsync($"/api/reports/sales/by-day?{period}"))).EnumerateArray().ToList();
        days.Sum(d => d.GetProperty("saleCount").GetInt32()).ShouldBe(registers.Sum(c => c.Sales.Count));
        // The drawers took every sale and paid every return except the one credited to the account (165).
        (days.Sum(d => d.GetProperty("salesWithItbis").GetDecimal() - d.GetProperty("returns").GetDecimal()) + 165m).ShouldBe(takenInDrawers);

        var rows607 = (await Ok(await admin.GetAsync($"/api/reports/dgii/607?{period}"))).EnumerateArray().ToList();
        rows607.Count.ShouldBe(documents.Count);
        var byNcf = documents.ToDictionary(d => d.GetProperty("eNcf").GetString()!);
        foreach (var f in rows607)
        {
            var doc = byNcf[f.GetProperty("ncf").GetString()!];
            f.GetProperty("itbisInvoiced").GetDecimal().ShouldBe(doc.GetProperty("itbis").GetDecimal());
            (f.GetProperty("invoicedAmount").GetDecimal() + f.GetProperty("itbisInvoiced").GetDecimal()).ShouldBe(doc.GetProperty("total").GetDecimal());
        }

        foreach (var v in (await Ok(await admin.GetAsync("/api/reports/inventory/value"))).EnumerateArray())
            v.GetProperty("stock").GetDecimal().ShouldBe(stock[v.GetProperty("productId").GetGuid()]);

        server.Errors500.ShouldBeEmpty(string.Join("\n", server.Errors500));

        // ── helpers (local functions keep the day readable top to bottom) ──
        async Task<JsonElement> ReturnAsync(Register register, Guid saleId, Guid lineId, decimal quantity, string method) =>
            await Ok(await SupervisedAsync(register, saleId, approval => register.Client.PostAsJsonAsync($"/api/sales/{saleId}/returns", new
            {
                ReturnId = Guid.CreateVersion7(), ShiftId = register.ShiftId, Method = method, Reason = "devolución",
                Lines = new[] { new { SaleLineId = lineId, Quantity = quantity } }, SupervisorApprovalId = approval,
            })));
    }

    private static async Task<Guid> ApproveAsync(Register register, string permission, Guid resource, string summary = "") =>
        (await Ok(await register.Client.PostAsJsonAsync("/api/identity/approvals",
            new { Code = "sup", Pin = SupervisorPin, Permission = permission, ResourceId = resource, Summary = summary })))
        .GetProperty("approvalId").GetGuid();

    /// <summary>The server's Approval.Required (permission and what is approved), or null.</summary>
    private static async Task<(string Permission, string Summary)?> RequiredAsync(HttpResponseMessage r)
    {
        if (r.StatusCode != HttpStatusCode.Forbidden) return null;
        var problem = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        if (problem.GetProperty("code").GetString() != "Approval.Required") return null;
        var errors = problem.GetProperty("errors");
        return (errors.GetProperty("permission")[0].GetString()!, errors.GetProperty("summary")[0].GetString()!);
    }

    /// <summary>Like the register: the supervisor approves exactly what the server asks for (R11 A1), then it goes again.</summary>
    private static async Task<HttpResponseMessage> SupervisedAsync(Register register, Guid resource, Func<Guid?, Task<HttpResponseMessage>> send)
    {
        var r = await send(null);
        return await RequiredAsync(r) is { } required ? await send(await ApproveAsync(register, required.Permission, resource, required.Summary)) : r;
    }

    /// <summary>Sells like a register: it shows the server's total first (TotalChanged), then charges it. Records the cash the drawer keeps.</summary>
    private static async Task<JsonElement> SellAsync(Register register, object[] lines, string method = "Cash", Guid? saleId = null, Dictionary<string, object>? extra = null)
    {
        var id = saleId ?? Guid.CreateVersion7();
        Dictionary<string, object> Body(decimal total, object[] payments)
        {
            var c = new Dictionary<string, object> { ["SaleId"] = id, ["ShiftId"] = register.ShiftId, ["Lines"] = lines, ["Payments"] = payments, ["ExpectedTotal"] = total };
            foreach (var (k, v) in extra ?? []) c[k] = v;
            return c;
        }

        var probe = await WithRetryAsync(() => register.Client.PostAsJsonAsync("/api/sales", Body(-1m, [new { Method = "Cash", Amount = 1m }])));
        var problem = await probe.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().ShouldBe("Sales.TotalChanged", problem.ToString());
        var total = decimal.Parse(problem.GetProperty("errors").GetProperty("total")[0].GetString()!, CultureInfo.InvariantCulture);

        var cash = method switch { "Cash" => Math.Ceiling(total) + (total > 300 ? 100 : 0), "Mixed" => Math.Floor(total / 2), _ => 0m };
        object[] payments = method switch
        {
            "Cash" => [new { Method = "Cash", Amount = cash }],
            "Mixed" => [new { Method = "Card", Amount = total - cash, Reference = "AUT" }, new { Method = "Cash", Amount = cash }],
            _ => [new { Method = method, Amount = total, Reference = "AUT" }],
        };
        // Each permission the sale needs (discount, credit, over the limit) is approved by the supervisor as the server summarized it.
        List<Guid> approvals = [];
        HttpResponseMessage charged;
        while (true)
        {
            var body = Body(total, payments);
            if (approvals.Count > 0) body["SupervisorApprovals"] = approvals.ToArray();
            charged = await WithRetryAsync(() => register.Client.PostAsJsonAsync("/api/sales", body));
            if (approvals.Count > 4 || await RequiredAsync(charged) is not { } required) break;
            approvals.Add(await ApproveAsync(register, required.Permission, id, required.Summary));
        }
        var sale = await Ok(charged);
        var kept = cash - sale.GetProperty("change").GetDecimal();
        lock (register)
        {
            register.Cash += kept;
            register.Sales.Add((id, total, kept));
        }
        return sale;
    }

    /// <summary>A register retries a deadlock victim (409 Busy) with the same request: the same SaleId makes the retry safe.</summary>
    private static async Task<HttpResponseMessage> WithRetryAsync(Func<Task<HttpResponseMessage>> send)
    {
        for (var attempt = 1; ; attempt++)
        {
            var r = await send();
            if (r.StatusCode != HttpStatusCode.Conflict || attempt == 3) return r;
            // ReadAsStringAsync buffers the body, so the caller can still read it.
            if (!(await r.Content.ReadAsStringAsync()).Contains("\"code\":\"Busy\"", StringComparison.Ordinal)) return r;
            await Task.Delay(100 * attempt);
        }
    }

    private static async Task<string> SessionAsync(AbastaliaServer server, string device, string code, string pin) =>
        (await Ok(await server.Client(device).PostAsJsonAsync("/api/identity/sessions", new { Code = code, Pin = pin }))).GetProperty("token").GetString()!;

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.IsSuccessStatusCode.ShouldBeTrue($"{r.RequestMessage?.Method} {r.RequestMessage?.RequestUri?.PathAndQuery} → {(int)r.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>The bills and coins a cashier would count for an amount (greedy, Dominican denominations).</summary>
    private static object[] Count(decimal amount)
    {
        var count = new List<object>();
        foreach (var b in Denominations)
        {
            var n = (int)(amount / b);
            if (n > 0) count.Add(new { Denomination = b, Quantity = n });
            amount -= n * b;
        }
        amount.ShouldBe(0m, "expected cash must be countable in whole pesos");
        return [.. count];
    }

    /// <summary>A license as the vendor signs it: compact JWS, ES256.</summary>
    private static string SignedLicense(System.Security.Cryptography.ECDsa key, int registers)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64("""{"alg":"ES256","typ":"JWT"}"""u8.ToArray());
        var body = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Store = "Carnicería La Esquina", Plan = "Negocio", Features = Array.Empty<string>(), Limits = new Dictionary<string, int> { ["Registers"] = registers },
            IssuedUtc = DateTime.UtcNow, ExpiresUtc = DateTime.UtcNow.AddDays(30),
        }, JsonSerializerOptions.Web));
        return $"{header}.{body}.{B64(key.SignData(System.Text.Encoding.ASCII.GetBytes($"{header}.{body}"), System.Security.Cryptography.HashAlgorithmName.SHA256))}";
    }

    /// <summary>A scale price label: "22" + 4-digit PLU + amount in centavos (6 digits) + EAN-13 check digit.</summary>
    private static string PriceLabel(string plu, decimal amount)
    {
        var twelve = $"22{plu}{(int)(amount * 100):000000}";
        var sum = twelve.Select((d, i) => (d - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
        return twelve + (10 - sum % 10) % 10;
    }
}
