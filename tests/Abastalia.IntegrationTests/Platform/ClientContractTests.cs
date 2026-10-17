using System.Collections;
using System.Net.Http.Json;
using System.Reflection;
using Shouldly;
using Abastalia.IntegrationTests.Infra;
using Abastalia.SharedKernel;
using C = Abastalia.Client.UI.Api;

namespace Abastalia.IntegrationTests.Platform;

/// <summary>TK-124 (R7 #13b): the register app's own records and JSON options against the real local API, so a renamed or dropped field fails here
/// instead of showing an empty screen. Every non-nullable field must arrive (no null, no empty Guid, no default date), nested records too.
/// Own app: it does the first-run setup itself, the way the register does.</summary>
public sealed class ClientContractTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private static readonly NullabilityInfoContext Nullability = new();

    [Fact]
    public async Task The_register_app_reads_every_response_it_uses_with_its_own_records()
    {
        var anonymous = app.Anonymous();
        var setup = await Send<C.SetupResult>(anonymous, HttpMethod.Post, "/api/identity/setup", new C.SetupRequest("Dueña", "admin", AbastaliaApp.AdminPin, "PC principal"));
        var device = app.Anonymous(setup.DeviceToken);
        var cashiers = await Send<List<C.ActiveUser>>(device, HttpMethod.Get, "/api/identity/users/active");
        cashiers.ShouldHaveSingleItem().Name.ShouldBe("Dueña");
        var session = await Send<C.OpenedSession>(device, HttpMethod.Post, "/api/identity/sessions", new { Code = "admin", Pin = AbastaliaApp.AdminPin });
        var api = app.Client(new UserSession(session.Token, setup.DeviceToken!, session.UserId));
        (await Send<C.Me>(api, HttpMethod.Get, "/api/identity/me")).Permissions.ShouldNotBeEmpty();

        await Send<C.Empty>(api, HttpMethod.Put, "/api/settings/business", new C.BusinessProfile("Carnicería La Esquina", "131245678", "Calle 1", "809-555-0101", "Gracias", 10));
        (await Send<C.BusinessProfile>(api, HttpMethod.Get, "/api/settings/business")).Rnc.ShouldBe("131245678");
        foreach (var type in new[] { 32, 34 })
            await Send<C.Created>(api, HttpMethod.Post, "/api/fiscal/sequences", new { Type = type, From = 1, To = 1000, ExpiresOn = "2030-12-31" });
        (await Send<List<C.SequenceAlert>>(api, HttpMethod.Get, "/api/fiscal/alerts")).ShouldNotBeEmpty();
        (await Send<C.FiscalQueue>(api, HttpMethod.Get, "/api/fiscal/queue")).Pending.ShouldBe(0);
        await Send<C.LicenseFeatures>(api, HttpMethod.Get, "/api/settings/features");
        (await Send<C.SampleDataStatus>(api, HttpMethod.Post, "/api/inventory/sample-data")).Products.ShouldBeGreaterThan(0);
        (await Send<C.SampleDataStatus>(api, HttpMethod.Delete, "/api/inventory/sample-data")).ShouldBe(new C.SampleDataStatus(0, 0));
        (await Send<C.SampleDataStatus>(api, HttpMethod.Get, "/api/inventory/sample-data")).Products.ShouldBe(0);
        await Send<C.Empty>(api, HttpMethod.Put, "/api/settings/approvals", new C.ApprovalPolicy(["sales.remove-line", "sales.void"]));
        (await Send<List<C.ApprovalRule>>(api, HttpMethod.Get, "/api/settings/approvals")).Where(r => r.Required).Select(r => r.Permission).ShouldBe(["sales.remove-line", "sales.void"]);
        (await Send<List<C.User>>(api, HttpMethod.Get, "/api/identity/users")).ShouldHaveSingleItem().Role.ShouldNotBeNullOrEmpty();
        (await Send<C.RoleList>(api, HttpMethod.Get, "/api/identity/roles")).AvailablePermissions.ShouldNotBeEmpty();
        (await Send<List<C.Device>>(api, HttpMethod.Get, "/api/identity/devices")).ShouldHaveSingleItem().Name.ShouldBe("PC principal");

        // Catalog
        var category = (await Send<C.Created>(api, HttpMethod.Post, "/api/inventory/categories", new { Name = "Res", Color = "#B42318", Notes = new[] { "molida" } })).Id;
        (await Send<List<C.Category>>(api, HttpMethod.Get, "/api/inventory/categories")).ShouldHaveSingleItem().Notes.ShouldBe(["molida"]);
        var steak = (await Send<C.Created>(api, HttpMethod.Post, "/api/inventory/products", new C.CreateProduct("Bistec de res", category, C.UnitOfMeasure.Pound, null,
            C.ItbisRate.General18, 250, 230, 150, false, 5, ["7460000000017"], 100))).Id;
        var summary = (await Send<List<C.ProductSummary>>(api, HttpMethod.Get, $"/api/inventory/products?categoryId={category}")).ShouldHaveSingleItem();
        (summary.Price, summary.Stock).ShouldBe((250m, 100m));
        var detail = await Send<C.ProductDetail>(api, HttpMethod.Get, $"/api/inventory/products/{steak}");
        (detail.Cost, detail.WholesalePrice, detail.Unit).ShouldBe((150m, 230m, C.UnitOfMeasure.Pound));
        var byCode = await Send<C.ProductByCode>(api, HttpMethod.Get, "/api/inventory/products/by-code/7460000000017");
        (byCode.ProductId, byCode.Price, byCode.Factor).ShouldBe((steak, 250m, 1m));
        var prices = await Send<List<C.ProductPrice>>(api, HttpMethod.Get, $"/api/inventory/products/prices?ids={steak}");
        prices.ShouldHaveSingleItem().ShouldBe(new C.ProductPrice(steak, null, 250m, 230m));

        // Register and shift
        var register = (await Send<C.Created>(api, HttpMethod.Post, "/api/cash/registers", new { Name = "Caja 1" })).Id;
        var shift = (await Send<C.ShiftOpened>(api, HttpMethod.Post, "/api/cash/shifts", new { RegisterId = register, OpeningFloat = 2000m })).ShiftId;
        var current = await Send<C.CurrentShift>(api, HttpMethod.Get, "/api/cash/shifts/current");
        (current.ShiftId, current.Register, current.OpeningFloat).ShouldBe((shift, "Caja 1", 2000m));
        (await Send<List<C.RegisterInfo>>(api, HttpMethod.Get, "/api/cash/registers")).ShouldHaveSingleItem().OpenShiftId.ShouldBe(shift);

        // Sale, receipt, list and detail
        var sold = await Send<C.SaleRegistered>(api, HttpMethod.Post, "/api/sales", new C.RegisterSale(Guid.CreateVersion7(), shift,
            [new C.SaleLine(steak, null, 2)], [new C.Payment(C.PaymentMethod.Cash, 1000)], 500, C.EcfType.Consumer32, null, null, null, C.PriceLevel.Retail, []));
        (sold.Total, sold.Change, sold.Number).ShouldBe((500m, 500m, 1L));
        var line = sold.Receipt.Lines.ShouldHaveSingleItem();
        (line.Quantity, line.Price, line.Total).ShouldBe((2m, 250m, 500m));
        sold.Receipt.Itbis.ShouldHaveSingleItem().Itbis.ShouldBeGreaterThan(0);
        sold.Receipt.Payments.ShouldHaveSingleItem().Received.ShouldBe(1000m);
        (await Send<Abastalia.Hardware.Receipt>(api, HttpMethod.Get, $"/api/sales/{sold.SaleId}/receipt")).ENcf.ShouldBe(sold.ENcf);
        (await Send<List<C.SaleSummary>>(api, HttpMethod.Get, $"/api/sales?shiftId={shift}")).ShouldHaveSingleItem().Total.ShouldBe(500m);
        var sale = await Send<C.SaleDetail>(api, HttpMethod.Get, $"/api/sales/{sold.SaleId}");
        (sale.Lines.ShouldHaveSingleItem().Quantity, sale.Payments.ShouldHaveSingleItem().Amount, sale.Total).ShouldBe((2m, 500m, 500m));

        // Return with a supervisor's approval
        var approval = await Send<C.ApprovalGranted>(api, HttpMethod.Post, "/api/identity/approvals",
            new { Code = "admin", Pin = AbastaliaApp.AdminPin, Permission = "sales.return", ResourceId = sold.SaleId });
        var returned = await Send<C.ReturnRecorded>(api, HttpMethod.Post, $"/api/sales/{sold.SaleId}/returns", new
        {
            ReturnId = Guid.CreateVersion7(), ShiftId = shift, Lines = new[] { new C.ReturnLine(sale.Lines[0].Id, 1) }, Method = C.PaymentMethod.Cash,
            Reason = "Devolución", SupervisorApprovalId = approval.ApprovalId,
        });
        (returned.Total, returned.Method).ShouldBe((250m, C.PaymentMethod.Cash));
        var afterReturn = await Send<C.SaleDetail>(api, HttpMethod.Get, $"/api/sales/{sold.SaleId}");
        (afterReturn.Lines[0].ReturnedQuantity, afterReturn.Returns.ShouldHaveSingleItem().Total).ShouldBe((1m, 250m));

        // Z report and reports
        var z = await Send<C.ZReport>(api, HttpMethod.Get, $"/api/cash/shifts/{shift}/z");
        (z.SaleCount, z.ExpectedCash).ShouldBe((1, 2250m));
        z.ByMethod.Single(m => m.Method == C.PaymentMethod.Cash).SalePayments.ShouldBe(500m);
        var today = BusinessDay.Of(app.Clock.GetUtcNow().UtcDateTime);
        var range = $"from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        var day = (await Send<List<C.DailySales>>(api, HttpMethod.Get, $"/api/reports/sales/by-day?{range}")).ShouldHaveSingleItem();
        (day.SaleCount, day.SalesWithItbis, day.Returns).ShouldBe((1, 500m, 250m));
        var top = (await Send<List<C.ProductSales>>(api, HttpMethod.Get, $"/api/reports/sales/by-product?{range}")).ShouldHaveSingleItem();
        (top.Quantity, top.Unit).ShouldBe((1m, "Pound")); // TK-304: the summary reads the unit from here (1 lb, not "1 unidad")
        (await Send<List<C.ShiftSummary>>(api, HttpMethod.Get, $"/api/reports/shifts?{range}")).ShouldHaveSingleItem().OpeningFloat.ShouldBe(2000m);

        // Electronic receipts (TK-204, TK-205, TK-176)
        (await Send<C.EcfQueue>(api, HttpMethod.Get, "/api/fiscal/queue")).Environment.ShouldBe("Simulated");
        (await Send<List<C.EcfDocument>>(api, HttpMethod.Get, "/api/fiscal/documents?last=50")).Select(d => d.ENcf).ShouldContain(sold.ENcf);
        var notes = (await Send<List<C.EcfSequence>>(api, HttpMethod.Get, "/api/fiscal/sequences")).Single(s => s.Type == "CreditNote34");
        (await Send<C.EcfVoided>(api, HttpMethod.Post, $"/api/fiscal/sequences/{notes.Id}/void", new { To = 999 })).To.ShouldBe("E340000000999");
        (await Send<List<C.EcfVoidRow>>(api, HttpMethod.Get, "/api/fiscal/voids")).ShouldHaveSingleItem().Status.ShouldBe("Simulated");
        (await Send<C.EcfCertificateStatus>(api, HttpMethod.Get, "/api/fiscal/certificate")).Installed.ShouldBeFalse();
        (await Send<List<C.ReceivedEcf>>(api, HttpMethod.Get, "/api/fiscal/received")).ShouldBeEmpty(); // TK-154; filled from the cloud (EcfExchangeTests)

        // Customers
        var customer = (await Send<C.Created>(api, HttpMethod.Post, "/api/customers", new C.NewCustomer("Ana Pérez", null, "809-555-0102", null, false))).Id;
        (await Send<List<C.CustomerSummary>>(api, HttpMethod.Get, "/api/customers?search=Ana")).ShouldHaveSingleItem().Id.ShouldBe(customer);
        (await Send<C.CustomerDetail>(api, HttpMethod.Get, $"/api/customers/{customer}")).Name.ShouldBe("Ana Pérez");

        // Inventory back office (TK-184)
        var store = (await Send<List<C.Warehouse>>(api, HttpMethod.Get, "/api/inventory/warehouses")).ShouldHaveSingleItem();
        store.IsMain.ShouldBeTrue();
        var ribs = (await Send<C.Created>(api, HttpMethod.Post, "/api/inventory/products", new C.CreateProduct("Costilla", category, C.UnitOfMeasure.Pound, null,
            C.ItbisRate.Exempt, 180, null, 120, false, 50, null, 10))).Id;
        var low = (await Send<List<C.StockLevel>>(api, HttpMethod.Get, "/api/inventory/stock?belowMinimumOnly=true")).ShouldHaveSingleItem();
        (low.ProductId, low.Quantity, low.Value, low.BelowMinimum).ShouldBe((ribs, 10m, 1200m, true));
        (await Send<List<C.StockLevel>>(api, HttpMethod.Get, $"/api/inventory/stock?warehouseId={store.Id}")).Count.ShouldBe(2);
        var adjustment = new C.AdjustStock(ribs, null, -1.5m, C.AdjustmentReason.Damage, "se cayó", Guid.CreateVersion7());
        await Send<C.AdjustmentRecorded>(api, HttpMethod.Post, "/api/inventory/adjustments", adjustment);
        await Send<C.AdjustmentRecorded>(api, HttpMethod.Post, "/api/inventory/adjustments", adjustment); // TK-304: a retry, applied once
        var kardex = await Send<List<C.StockMovement>>(api, HttpMethod.Get, $"/api/inventory/products/{ribs}/movements?last=100");
        (kardex[0].Type, kardex[0].Quantity, kardex[0].ResultingStock).ShouldBe((C.StockMovementType.Adjustment, -1.5m, 8.5m));
        kardex.Count(k => k.Type == C.StockMovementType.Adjustment && k.Quantity == -1.5m).ShouldBe(1);
        var cold = (await Send<C.Created>(api, HttpMethod.Post, "/api/inventory/warehouses", new { Name = "Cuarto frío" })).Id;
        var transfer = new C.TransferStock(store.Id, cold, [new C.TransferLine(ribs, 2)], Guid.CreateVersion7());
        await Send<C.TransferRecorded>(api, HttpMethod.Post, "/api/inventory/transfers", transfer);
        (await Send<C.TransferRecorded>(api, HttpMethod.Post, "/api/inventory/transfers", transfer)).TransferId.ShouldBe(transfer.TransferId); // a retry
        (await Send<List<C.StockLevel>>(api, HttpMethod.Get, $"/api/inventory/stock?warehouseId={cold}")).Single(s => s.ProductId == ribs).Quantity.ShouldBe(2m);
        var started = await Send<C.StockCountStarted>(api, HttpMethod.Post, "/api/inventory/stock-counts", new C.StartStockCount(null, category));
        (await Send<List<C.StockCountSummary>>(api, HttpMethod.Get, "/api/inventory/stock-counts")).ShouldHaveSingleItem().Status.ShouldBe(C.StockCountStatus.Open);
        var sheet = await Send<C.StockCountSheet>(api, HttpMethod.Get, $"/api/inventory/stock-counts/{started.StockCountId}");
        sheet.Lines.Length.ShouldBe(2);
        await Send<C.Empty>(api, HttpMethod.Put, $"/api/inventory/stock-counts/{started.StockCountId}/lines", new C.RecordCount([new C.CountedLine(ribs, 6)]));
        var applied = await Send<C.StockCountApplied>(api, HttpMethod.Post, $"/api/inventory/stock-counts/{started.StockCountId}/apply", new C.ApplyStockCount(null));
        (applied.Differences.ShouldHaveSingleItem().Difference, applied.DifferenceValue).ShouldBe((-0.5m, -60m));

        // Purchasing back office (TK-185)
        var supplier = (await Send<C.Created>(api, HttpMethod.Post, "/api/purchasing/suppliers", new C.SaveSupplier("Granja del Cibao", "101234567", null, "Pedro", 15, true))).Id;
        await Send<C.Empty>(api, HttpMethod.Put, $"/api/purchasing/suppliers/{supplier}", new C.SaveSupplier("Granja del Cibao", "101234567", "809-555-0103", "Pedro", 30, true));
        (await Send<List<C.Supplier>>(api, HttpMethod.Get, "/api/purchasing/suppliers?search=Granja")).ShouldHaveSingleItem().CreditDays.ShouldBe(30);
        var purchase = await Send<C.PurchaseSaved>(api, HttpMethod.Post, "/api/purchasing/purchases", new C.SavePurchase(supplier, "B0100000001", today, C.PaymentTerms.Credit,
            [new C.PurchaseLineInput(steak, null, 10, 140, C.ItbisRate.General18)], null, null, null, null, null, Guid.CreateVersion7(), null));
        purchase.Total.ShouldBe(1652m);
        await Send<C.Empty>(api, HttpMethod.Post, $"/api/purchasing/purchases/{purchase.Id}/post");
        (await Send<List<C.PurchaseSummary>>(api, HttpMethod.Get, $"/api/purchasing/purchases?{range}")).ShouldHaveSingleItem().Status.ShouldBe("Posted");
        var bought = await Send<C.PurchaseDetail>(api, HttpMethod.Get, $"/api/purchasing/purchases/{purchase.Id}");
        (bought.Terms, bought.Lines.ShouldHaveSingleItem().ItbisRate, bought.DueDate).ShouldBe((C.PaymentTerms.Credit, C.ItbisRate.General18, today.AddDays(30)));
        var expense = await Send<C.ExpenseRecorded>(api, HttpMethod.Post, "/api/purchasing/expenses", new C.RecordExpense(supplier, "B0100000002", today, "Transporte",
            C.ExpenseType.WorkSuppliesAndServices, 100, 18, C.PaymentTerms.Cash, null, Guid.CreateVersion7(), C.SupplierPaymentMethod.Transfer));
        (await Send<List<C.Expense>>(api, HttpMethod.Get, $"/api/purchasing/expenses?{range}")).ShouldHaveSingleItem().Total.ShouldBe(118m);
        await Send<C.Empty>(api, HttpMethod.Post, $"/api/purchasing/expenses/{expense.Id}/void", new C.VoidReason("Repetido"));
        var supplierPaid = await Send<C.SupplierPaid>(api, HttpMethod.Post, "/api/purchasing/payments", new C.PaySupplier(supplier, 500, C.SupplierPaymentMethod.Transfer, today, "T-1", Guid.CreateVersion7()));
        supplierPaid.Allocations.ShouldHaveSingleItem().RemainingBalance.ShouldBe(1152m);
        var supplierStatement = await Send<C.SupplierStatement>(api, HttpMethod.Get, $"/api/purchasing/suppliers/{supplier}/statement");
        (supplierStatement.Balance, supplierStatement.Lines.Single(l => l.Type == "Pago").PaymentId).ShouldBe((1152m, supplierPaid.SupplierPaymentId));
        (await Send<List<C.SupplierPayable>>(api, HttpMethod.Get, "/api/reports/payables")).ShouldHaveSingleItem().NotYetDue.ShouldBe(1152m);
        await Send<C.Empty>(api, HttpMethod.Post, $"/api/purchasing/payments/{supplierPaid.SupplierPaymentId}/void", new C.VoidReason("Monto equivocado"));
        // Suggested order (TK-191): costilla is under its minimum and was never bought; "Crear compra" saves a draft without NCF.
        await Send<C.Empty>(api, HttpMethod.Put, $"/api/inventory/products/{ribs}", new C.UpdateProduct(0, "Costilla", category, C.UnitOfMeasure.Pound, null,
            C.ItbisRate.Exempt, 180, null, false, 50, true, 80));
        (await Send<C.ProductDetail>(api, HttpMethod.Get, $"/api/inventory/products/{ribs}")).MaximumStock.ShouldBe(80m);
        var reorder = (await Send<List<C.SuggestedGroup>>(api, HttpMethod.Get, "/api/purchasing/suggested-order")).ShouldHaveSingleItem();
        (reorder.SupplierId, reorder.Lines.ShouldHaveSingleItem().ProductId).ShouldBe((null, ribs));
        var suggestedDraft = await Send<C.PurchaseSaved>(api, HttpMethod.Post, "/api/purchasing/purchases", new C.SavePurchase(supplier, "", today, C.PaymentTerms.Credit,
            [new C.PurchaseLineInput(ribs, null, reorder.Lines[0].Suggested, 120, C.ItbisRate.Exempt)], null, null, "Pedido sugerido", null, null, Guid.CreateVersion7(), null));
        (await Send<C.PurchaseDetail>(api, HttpMethod.Get, $"/api/purchasing/purchases/{suggestedDraft.Id}")).Status.ShouldBe("Draft");
        // Costs (TK-201): the owner gets them; the DGII 608 (TK-192) downloads like the other reports.
        (await Send<List<C.StockLevel>>(api, HttpMethod.Get, "/api/inventory/stock")).ShouldAllBe(s => s.Cost != null && s.Value != null);
        using (var r608 = await api.GetAsync($"/api/reports/dgii/608?format=xlsx&{range}"))
            r608.Content.Headers.ContentDisposition!.FileName.ShouldNotBeNullOrEmpty();
        // Back office: customers and credit (TK-186) and the register's payment (TK-121)
        await Send<C.Empty>(api, HttpMethod.Put, $"/api/customers/{customer}",
            new C.SaveCustomer("Ana Pérez", null, "809-555-0102", "Calle 2", false, C.PriceLevel.Retail, 5_000, true));
        (await Send<List<C.CustomerRow>>(api, HttpMethod.Get, "/api/customers?includeInactive=true")).ShouldHaveSingleItem().Address.ShouldBe("Calle 2");
        var onAccount = await Send<C.SaleRegistered>(api, HttpMethod.Post, "/api/sales", new C.RegisterSale(Guid.CreateVersion7(), shift,
            [new C.SaleLine(steak, null, 1)], [new C.Payment(C.PaymentMethod.OnAccount, 250)], 250, C.EcfType.Consumer32, customer, null, null, C.PriceLevel.Retail, []));
        (await Send<List<C.ReceivableAging>>(api, HttpMethod.Get, "/api/reports/receivables")).ShouldHaveSingleItem().Days0To30.ShouldBe(250m);
        var paid = await Send<C.CustomerPaymentRecorded>(api, HttpMethod.Post, $"/api/customers/{customer}/payments",
            new C.RecordCustomerPayment(Guid.CreateVersion7(), shift, 100, C.PaymentMethod.Cash, null));
        (paid.Applied, paid.RemainingDebt).ShouldBe((100m, 150m));
        var statement = await Send<C.CustomerStatement>(api, HttpMethod.Get, $"/api/customers/{customer}/statement");
        (statement.Debt, statement.Lines.Length).ShouldBe((150m, 2));
        statement.Lines.Single(l => l.Type == "Abono").PaymentId.ShouldBe(paid.CustomerPaymentId);
        statement.Lines.Single(l => l.Type == "Fiado").Document.ShouldBe(onAccount.ENcf);
        await Send<C.Empty>(api, HttpMethod.Post, $"/api/customers/payments/{paid.CustomerPaymentId}/void", new { Reason = "Monto equivocado", SupervisorApprovalId = (Guid?)null });
        (await Send<C.CustomerStatement>(api, HttpMethod.Get, $"/api/customers/{customer}/statement")).Lines.ShouldAllBe(l => l.PaymentId == null);

        // Back office: invoices and reports (TK-187)
        (await Send<List<C.SaleSummary>>(api, HttpMethod.Get, $"/api/sales?{range}&page=1")).Count.ShouldBe(2);
        (await Send<List<C.SaleSummary>>(api, HttpMethod.Get, $"/api/sales?search={onAccount.ENcf}&page=1")).ShouldHaveSingleItem().Customer.ShouldBe("Ana Pérez");
        // TK-304: a number goes by Number, anything else by the e-NCF (typed in lowercase too).
        (await Send<List<C.SaleSummary>>(api, HttpMethod.Get, $"/api/sales?search={onAccount.Number}")).ShouldHaveSingleItem().ENcf.ShouldBe(onAccount.ENcf);
        (await Send<List<C.SaleSummary>>(api, HttpMethod.Get, $"/api/sales?search={onAccount.ENcf.ToLowerInvariant()}")).ShouldHaveSingleItem().Number.ShouldBe(onAccount.Number);
        using (var csv = await api.GetAsync($"/api/reports/sales/by-day?format=csv&{range}"))
        {
            csv.IsSuccessStatusCode.ShouldBeTrue();
            csv.Content.Headers.ContentDisposition!.FileName.ShouldNotBeNullOrEmpty(); // AbastaliaApi names the download from it
            (await csv.Content.ReadAsByteArrayAsync()).ShouldNotBeEmpty();
        }

        // Back office: roles (TK-186) and renaming a register (TK-175)
        var role = (await Send<C.Created>(api, HttpMethod.Post, "/api/identity/roles", new C.SaveRole("Encargado", ["sales.create", "reports.view"]))).Id;
        await Send<C.Empty>(api, HttpMethod.Put, $"/api/identity/roles/{role}", new C.SaveRole("Encargado", ["sales.create"]));
        (await Send<C.RoleList>(api, HttpMethod.Get, "/api/identity/roles")).Roles.Single(r => r.Id == role).Permissions.ShouldBe(["sales.create"]);
        await Send<C.Empty>(api, HttpMethod.Delete, $"/api/identity/roles/{role}"); // TK-215: nobody has it
        (await Send<C.RoleList>(api, HttpMethod.Get, "/api/identity/roles")).Roles.ShouldNotContain(r => r.Id == role);
        await Send<C.Empty>(api, HttpMethod.Put, $"/api/cash/registers/{register}", new { Name = "Caja del frente" });
        (await Send<List<C.RegisterInfo>>(api, HttpMethod.Get, "/api/cash/registers")).ShouldHaveSingleItem().Name.ShouldBe("Caja del frente");

        // Audit log (TK-213); restoring a backup is covered by RestoreBackupTests (it replaces the database).
        (await Send<List<C.AuditUser>>(api, HttpMethod.Get, "/api/audit/users")).ShouldHaveSingleItem().Id.ShouldBe(session.UserId);
        var audit = await Send<List<C.AuditRow>>(api, HttpMethod.Get, $"/api/audit?{range}&page=1");
        audit.ShouldContain(r => r.Action == "cash.rename-register" && r.User == "Dueña");
        (await Send<List<C.AuditRow>>(api, HttpMethod.Get, $"/api/audit?{range}&action=settings.business")).ShouldAllBe(r => r.ResourceId == null);

        // The back office's remaining screens (TK-243): Resumen, Pedidos, Configuración and the e-CF panels, with the records they read
        // (the receiver status needs the cloud: EcfExchangeTests).
        (await Send<List<C.HourlySales>>(api, HttpMethod.Get, $"/api/reports/sales/by-hour?{range}")).ShouldNotBeEmpty();
        (await Send<List<C.PaymentMethodSales>>(api, HttpMethod.Get, $"/api/reports/sales/by-payment-method?{range}")).ShouldContain(m => m.Method == C.PaymentMethod.Cash);
        (await Send<List<C.Receivable>>(api, HttpMethod.Get, "/api/reports/receivables")).ShouldHaveSingleItem().Customer.ShouldBe("Ana Pérez");
        (await Send<List<C.StockItem>>(api, HttpMethod.Get, "/api/inventory/stock")).ShouldContain(s => s.ProductId == ribs);
        (await Send<List<decimal>>(api, HttpMethod.Get, "/api/cash/denominations")).ShouldNotBeEmpty();
        await Send<C.EcfHealth>(api, HttpMethod.Get, "/api/fiscal/health");
        (await Send<C.EcfContingencyStatus>(api, HttpMethod.Get, "/api/fiscal/contingency")).Active.ShouldBeFalse();
        (await Send<C.CertificationState>(api, HttpMethod.Get, "/api/fiscal/certification")).Environment.ShouldNotBeNullOrEmpty();
        (await Send<C.CloudLink>(api, HttpMethod.Get, "/api/sync/status")).Connected.ShouldBeFalse();
        await Send<List<C.BackupFile>>(api, HttpMethod.Get, "/api/backups");
        await Send<C.Empty>(api, HttpMethod.Put, "/api/catalog", new C.SaveCatalog("809-555-0104", [steak]));
        (await Send<C.CatalogSettings>(api, HttpMethod.Get, "/api/catalog")).ProductIds.ShouldBe([steak]);
        (await Send<C.CatalogQr>(api, HttpMethod.Get, "/api/catalog/qr?url=https%3A%2F%2Fpanel.abastalia.test%2Fc%2F1")).Svg.ShouldStartWith("<svg");
        var quote = await Send<C.OrderCreated>(api, HttpMethod.Post, "/api/orders",
            new C.CreateOrder(Guid.CreateVersion7(), C.OrderKind.Quote, customer, [new C.OrderLineInput(steak, null, 2, null)], null, null, null, 15, null));
        (await Send<List<C.OrderItem>>(api, HttpMethod.Get, $"/api/orders?kind=Quote&{range}")).ShouldHaveSingleItem().Id.ShouldBe(quote.OrderId);
        (await Send<C.OrderItem>(api, HttpMethod.Get, $"/api/orders/{quote.OrderId}")).Lines.ShouldHaveSingleItem().ProductId.ShouldBe(steak);
        var roles = await Send<C.RoleList>(api, HttpMethod.Get, "/api/identity/roles");
        await Send<C.Created>(api, HttpMethod.Post, "/api/identity/users", new { Name = "Yudelka", Code = "yudelka", Pin = "4826", RoleId = roles.Roles[0].Id });
        (await Send<List<C.User>>(api, HttpMethod.Get, "/api/identity/users")).Count.ShouldBe(2);
        (await Send<C.PairingCode>(api, HttpMethod.Post, "/api/identity/devices/pairing-codes", new { DeviceName = "Caja 2" })).Code.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void A_missing_field_or_id_is_caught()
    {
        Should.Throw<ShouldAssertException>(() => AssertFilled(new List<C.ActiveUser> { new(Guid.CreateVersion7(), null!, "admin") }, "renamed name"));
        Should.Throw<ShouldAssertException>(() => AssertFilled(new C.Created(Guid.Empty), "renamed id"));
        AssertFilled(new C.SaleSummary(Guid.CreateVersion7(), 1, "E320000000001", DateTime.UtcNow, "Completed", "Ana", null, 100), "nullable customer");
    }

    /// <summary>Sends like <c>AbastaliaApi</c> (its JSON options both ways) and checks the answer has every non-nullable field.</summary>
    private static async Task<T> Send<T>(HttpClient client, HttpMethod method, string url, object? body = null) where T : class
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: C.AbastaliaApi.Json);
        using var r = await client.SendAsync(request);
        r.IsSuccessStatusCode.ShouldBeTrue($"{method} {url}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        if (typeof(T) == typeof(C.Empty)) return default!;
        var value = await r.Content.ReadFromJsonAsync<T>(C.AbastaliaApi.Json);
        AssertFilled(value.ShouldNotBeNull($"{url} answered nothing"), $"{url} {typeof(T).Name}");
        return value;
    }

    private static void AssertFilled(object value, string path)
    {
        if (value is IEnumerable items and not string)
        {
            var i = 0;
            foreach (var item in items) AssertFilled(item, $"{path}[{i++}]");
            return;
        }
        if (value.GetType().Namespace != typeof(C.AbastaliaApi).Namespace) return;
        foreach (var p in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var v = p.GetValue(value);
            var where = $"{path}.{p.Name}";
            if (v is null)
            {
                Nullability.Create(p).ReadState.ShouldBe(NullabilityState.Nullable, $"{where} is missing");
                continue;
            }
            (v is Guid g && g == Guid.Empty).ShouldBeFalse($"{where} is an empty id");
            (v is DateTime d && d == default).ShouldBeFalse($"{where} has no date");
            AssertFilled(v, where);
        }
    }
}
