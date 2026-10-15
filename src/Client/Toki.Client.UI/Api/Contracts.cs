namespace Toki.Client.UI.Api;

// The local API's wire contracts (camelCase JSON, enums by name). Only what the screens use.
// shortcut: hand-written instead of generated from OpenAPI (Kiota, per the architecture doc); generate them once the API stops moving.

public enum PaymentMethod { Cash, Card, Transfer, OnAccount, CreditBalance }
public enum EcfType { FiscalCredit31 = 31, Consumer32 = 32 }
public enum PriceLevel { Retail, Wholesale }
public enum UnitOfMeasure { Unit, Pound }
public enum ItbisRate { General18, Reduced16, Zero, Exempt }
public enum CashMovementKind { CashIn, Withdrawal, Expense }
public enum MovementReason { ExtraChange, CashPickup, BankDeposit, SupplierPayment, PettyExpense, Other }

// Identity
public sealed record SetupRequest(string AdministratorName, string AdministratorCode, string Pin, string DeviceName);
public sealed record SetupResult(Guid UserId, Guid DeviceId, string DeviceToken);
public sealed record Paired(Guid DeviceId, string Name, string Token);
public sealed record PairingCode(string Code, DateTime ExpiresUtc);
public sealed record ActiveUser(Guid Id, string Name, string Code);
public sealed record OpenedSession(string Token, Guid UserId, string Name, DateTime ExpiresUtc);
public sealed record Me(Guid UserId, string Name, Guid DeviceId, string[] Permissions);
public sealed record ApprovalGranted(Guid ApprovalId, string Supervisor, DateTime ExpiresUtc);
public sealed record Device(Guid Id, string Name, bool IsRevoked, DateTime CreatedUtc);
public sealed record User(Guid Id, string Name, string Code, Guid RoleId, string Role, bool IsActive, bool IsLocked);
public sealed record Role(Guid Id, string Name, string[] Permissions);
public sealed record RoleList(Role[] Roles, string[] AvailablePermissions);

// Settings
public sealed record BusinessProfile(string Name, string? Rnc, string? Address, string? Phone, string? ReceiptFooter, decimal DiscountCapPercent);
public sealed record LicenseFeatures(string Status, string? Plan, DateTime? ExpiresUtc, string[] Enabled, int Registers);
public sealed record BackupFile(string FileName, long Bytes, DateTime DateUtc);

// Inventory
public sealed record Category(Guid Id, string Name, string Color, string[] Notes, bool IsActive);
public sealed record ProductSummary(Guid Id, string Name, string Category, string Unit, string ItbisRate, decimal Price, decimal? WholesalePrice, decimal? Stock, bool IsActive);
public sealed record ProductDetail(Guid Id, int Version, string Name, Guid CategoryId, UnitOfMeasure Unit, string? Plu, ItbisRate ItbisRate,
    decimal Price, decimal? WholesalePrice, decimal Cost, bool SellWithoutStock, decimal MinimumStock, bool IsActive);
public sealed record CreateProduct(string Name, Guid CategoryId, UnitOfMeasure Unit, string? Plu, ItbisRate ItbisRate, decimal Price,
    decimal? WholesalePrice, decimal Cost, bool SellWithoutStock, decimal MinimumStock, string[]? Barcodes, decimal InitialStock);
public sealed record UpdateProduct(int Version, string Name, Guid CategoryId, UnitOfMeasure Unit, string? Plu, ItbisRate ItbisRate,
    decimal Price, decimal? WholesalePrice, bool SellWithoutStock, decimal MinimumStock, bool IsActive);
public sealed record ProductByCode(Guid ProductId, Guid? PresentationId, string Name, string Unit, string ItbisRate, decimal Price,
    decimal? WholesalePrice, decimal Factor, decimal? LabelQuantity, decimal? LabelAmount, string[] Notes, decimal? Stock);
public sealed record StockItem(Guid ProductId, string Name, string Category, UnitOfMeasure Unit, decimal Quantity, bool BelowMinimum);
public sealed record Created(Guid Id);

// Cash register
public sealed record RegisterInfo(Guid Id, string Name, bool IsActive, Guid? OpenShiftId, string? Cashier);
public sealed record CurrentShift(Guid ShiftId, Guid RegisterId, string Register, decimal OpeningFloat, DateTime OpenedUtc);
public sealed record ShiftOpened(Guid ShiftId);
public sealed record DenominationCount(decimal Denomination, int Quantity);
public sealed record MethodTotal(PaymentMethod Method, decimal SalePayments, decimal Voids, decimal Refunds, decimal CustomerPayments, decimal Net);
public sealed record ZReport(Guid ShiftId, long? ZNumber, string Register, string Cashier, DateTime OpenedUtc, DateTime? ClosedUtc, decimal OpeningFloat,
    MethodTotal[] ByMethod, int SaleCount, int VoidedCount, decimal CustomerPayments, decimal Rounding, decimal CashIn, decimal Withdrawals, decimal Expenses,
    decimal ExpectedCash, decimal? CountedCash, decimal? Difference);

// Sales
public sealed record Discount(bool IsPercentage, decimal Value);
public sealed record SaleLine(Guid ProductId, Guid? PresentationId, decimal Quantity, decimal? UnitPrice = null, Discount? Discount = null,
    string? Note = null, string? ScaleLabel = null);
public sealed record Payment(PaymentMethod Method, decimal Amount, string? Reference = null);
public sealed record RegisterSale(Guid SaleId, Guid ShiftId, List<SaleLine> Lines, List<Payment> Payments, decimal ExpectedTotal,
    EcfType DocumentType, Guid? CustomerId, string? CustomerRnc, string? CustomerName, PriceLevel PriceLevel, Discount? GlobalDiscount, List<Guid> SupervisorApprovals);
public sealed record ReceiptLine(string Description, decimal Quantity, string Unit, decimal Price, decimal Discount, decimal Total, string? Note);
public sealed record ReceiptItbis(string Rate, decimal Base, decimal Itbis);
/// <param name="Method">Already in Spanish, as printed.</param>
public sealed record ReceiptPayment(string Method, decimal Amount, decimal? Received);
public sealed record Receipt(string BusinessName, string? Rnc, string? Address, string? Phone, string DocumentType, string ENcf, long Number,
    DateTime LocalDateTime, string Register, string Cashier, string? Customer, string? CustomerRnc, ReceiptLine[] Lines, decimal Subtotal, decimal Discount,
    ReceiptItbis[] Itbis, decimal Total, ReceiptPayment[] Payments, decimal Change, string SecurityCode, string QrUrl, string? Footer, bool IsVoided);
public sealed record SaleRegistered(Guid SaleId, long Number, string ENcf, decimal Total, decimal Change, Receipt Receipt);
public sealed record SaleSummary(Guid Id, long Number, string ENcf, DateTime DateUtc, string Status, string Cashier, string? Customer, decimal Total);
public sealed record SaleDetailLine(Guid Id, string Description, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total, decimal Itbis,
    decimal ReturnedQuantity, string? Note, string Unit = "Unit");
public sealed record SaleDetailPayment(PaymentMethod Method, decimal Amount, decimal? Received, string? Reference);
public sealed record SaleDetailReturn(Guid Id, DateTime DateUtc, decimal Total, PaymentMethod Method, string ENcf, string Reason);
public sealed record SaleDetail(Guid Id, long Number, string ENcf, string DocumentType, DateTime DateUtc, string Status, string Register, string Cashier,
    Guid? CustomerId, string? Customer, decimal Discount, decimal Itbis, decimal Total, decimal Change, string? VoidReason,
    SaleDetailLine[] Lines, SaleDetailPayment[] Payments, SaleDetailReturn[] Returns);
public sealed record ReturnLine(Guid SaleLineId, decimal Quantity, bool Restock = true);
public sealed record ReturnRecorded(Guid ReturnId, string CreditNoteENcf, decimal Total, decimal Itbis, PaymentMethod Method);
public sealed record HeldSale(Guid Id, string Label, decimal Total, DateTime CreatedUtc, System.Text.Json.JsonElement Content);

// Customers
public sealed record CustomerSummary(Guid Id, string Name, string? TaxId, string? Phone, string PriceLevel, decimal CreditLimit, bool IsActive);
public sealed record CustomerDetail(Guid Id, string Name, string? TaxId, bool FiscalCredit, string PriceLevel, decimal CreditLimit, decimal Debt,
    decimal CreditBalance, bool IsActive);
public sealed record NewCustomer(string Name, string? TaxId, string? Phone, string? Address, bool FiscalCredit);

// Reports and fiscal
public sealed record DailySales(DateOnly Day, int SaleCount, decimal SalesWithItbis, decimal Discount, decimal Returns, decimal Itbis,
    decimal NetWithoutItbis, decimal Cost, decimal Profit);
public sealed record ProductSales(Guid ProductId, string Product, decimal Quantity, decimal NetWithoutItbis, decimal Profit);
public sealed record ShiftSummary(Guid ShiftId, string Register, string Cashier, DateTime OpenedUtc, DateTime? ClosedUtc, long? ZNumber, decimal OpeningFloat,
    decimal? ExpectedCash, decimal? CountedCash, decimal? Difference);
public sealed record Receivable(Guid CustomerId, string Customer, decimal Over90, decimal Total, decimal CreditLimit);
public sealed record SequenceAlert(string Type, long Available, DateOnly? ExpiresOn, bool Alert);
