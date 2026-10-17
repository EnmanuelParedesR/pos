using Abastalia.Hardware;

namespace Abastalia.Client.UI.Api;

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
public sealed record SetupRequest(string AdministratorName, string AdministratorCode, string Pin, string DeviceName, string? CertificateRequest = null);
/// <summary>A client certificate and the store CA when a CSR was sent (TK-091), else the device secret (plain http).</summary>
public sealed record SetupResult(Guid UserId, Guid DeviceId, string? DeviceToken, string? Certificate, string? StoreCa);
public sealed record Paired(Guid DeviceId, string Name, string? Token, string? Certificate, string? StoreCa);
/// <summary>R11 C5: the renewed client certificate (PEM).</summary>
public sealed record RenewedCertificate(string Certificate);
public sealed record PairingCode(string Code, DateTime ExpiresUtc);
public sealed record ActiveUser(Guid Id, string Name, string Code);
public sealed record OpenedSession(string Token, Guid UserId, string Name, DateTime ExpiresUtc);
/// <param name="IdleMinutes">TK-366: minutes without a key or a touch before the register locks back to the PIN; 0 = never (an older server).</param>
public sealed record Me(Guid UserId, string Name, Guid DeviceId, string[] Permissions, int IdleMinutes = 0);
public sealed record ApprovalGranted(Guid ApprovalId, string Supervisor, DateTime ExpiresUtc);
public sealed record Device(Guid Id, string Name, bool IsRevoked, DateTime CreatedUtc);
public sealed record User(Guid Id, string Name, string Code, Guid RoleId, string Role, bool IsActive, bool IsLocked);
public sealed record Role(Guid Id, string Name, string[] Permissions);
public sealed record RoleList(Role[] Roles, string[] AvailablePermissions);

// Settings
/// <param name="LicensedRnc">TK-368: the RNC the installed license names; the RNC field is then fixed (only support changes it).</param>
public sealed record BusinessProfile(string Name, string? Rnc, string? Address, string? Phone, string? ReceiptFooter, decimal DiscountCapPercent,
    bool RequirePaymentReference = true, string? LogoVersion = null, string? LicensedRnc = null);
/// <param name="TrialEndsUtc">TK-196: null until the first sale starts the 14 days (and once a license is installed).</param>
/// <param name="Warning">TK-220: null, "Soon" (trial day 10 on), "Last" (day 13 on), "Ended" (Restricted) or "Blocked" (TK-279).</param>
/// <param name="BlocksUtc">TK-279: when it goes Blocked by date. <paramref name="ClockBehind"/>: the PC clock is behind the latest time the store saw.</param>
/// <param name="BlockReason">R12: while Blocked, "Expired", "Trial" or "Suspended" (by Abastalia).</param>
public sealed record LicenseFeatures(string Status, string? Plan, DateTime? ExpiresUtc, string[] Enabled, int Registers,
    DateTime? TrialEndsUtc = null, int? TrialDaysLeft = null, string? Warning = null, DateTime? BlocksUtc = null, bool ClockBehind = false, string? BlockReason = null);
/// <summary>TK-196: "datos de ejemplo" still showing.</summary>
/// <summary>TK-277: cashiers enabled and the license's limit (null = no limit).</summary>
public sealed record CashierSeats(int Used, int? Limit);
/// <summary>TK-277: someone who used one PIN at two registers at once (<c>/api/identity/shared-pins</c>).</summary>
public sealed record SharedPin(Guid UserId, string Name, int Times, DateTime LastUtc);
public sealed record SampleDataStatus(int Products, int Categories);
/// <summary>One action the store may free from the supervisor's PIN (TK-210); the list comes in screen order.</summary>
public sealed record ApprovalRule(string Permission, bool Required);
public sealed record ApprovalPolicy(string[] Required);
public sealed record BackupFile(string FileName, long Bytes, DateTime DateUtc);
/// <summary>BackupBefore: the backup of the data replaced (null when the store's backup key could not be used to make it).</summary>
public sealed record BackupRestored(string? BackupBefore);
/// <summary>TK-213. Data is the JSON the action saved; ResourceId is null for store-wide records.</summary>
public sealed record AuditRow(long Sequence, DateTime DateUtc, Guid? UserId, string? User, string? Supervisor, string Action, string ResourceType, Guid? ResourceId, string Data);
public sealed record AuditUser(Guid Id, string Name);

// Inventory
public sealed record Category(Guid Id, string Name, string Color, string[] Notes, bool IsActive);
public sealed record ProductSummary(Guid Id, string Name, string Category, string Unit, string ItbisRate, decimal Price, decimal? WholesalePrice, decimal? Stock, bool IsActive);
public sealed record ProductDetail(Guid Id, int Version, string Name, Guid CategoryId, UnitOfMeasure Unit, string? Plu, ItbisRate ItbisRate,
    decimal Price, decimal? WholesalePrice, decimal? Cost, bool SellWithoutStock, decimal MinimumStock, bool IsActive, decimal MaximumStock = 0,
    ProductPresentation[]? Presentations = null, ProductBarcode[]? Barcodes = null);
public sealed record ProductPresentation(Guid Id, string Name, decimal Factor, decimal Price, decimal? WholesalePrice, bool IsActive);
public sealed record ProductBarcode(string Code, Guid? PresentationId);
public sealed record CreateProduct(string Name, Guid CategoryId, UnitOfMeasure Unit, string? Plu, ItbisRate ItbisRate, decimal Price,
    decimal? WholesalePrice, decimal Cost, bool SellWithoutStock, decimal MinimumStock, string[]? Barcodes, decimal InitialStock, decimal MaximumStock = 0);
public sealed record UpdateProduct(int Version, string Name, Guid CategoryId, UnitOfMeasure Unit, string? Plu, ItbisRate ItbisRate,
    decimal Price, decimal? WholesalePrice, bool SellWithoutStock, decimal MinimumStock, bool IsActive, decimal MaximumStock = 0);
public sealed record ProductByCode(Guid ProductId, Guid? PresentationId, string Name, string Unit, string ItbisRate, decimal Price,
    decimal? WholesalePrice, decimal Factor, decimal? LabelQuantity, decimal? LabelAmount, string[] Notes, decimal? Stock);
/// <param name="PresentationId">Null for the product itself (GET /api/inventory/products/prices, to re-price a cart).</param>
public sealed record ProductPrice(Guid ProductId, Guid? PresentationId, decimal Price, decimal? WholesalePrice);
public sealed record StockItem(Guid ProductId, string Name, string Category, UnitOfMeasure Unit, decimal Quantity, bool BelowMinimum);
public sealed record Created(Guid Id);

// Cash register
public sealed record RegisterInfo(Guid Id, string Name, bool IsActive, Guid? OpenShiftId, string? Cashier, decimal? StandardFloat = null);
public sealed record CurrentShift(Guid ShiftId, Guid RegisterId, string Register, decimal OpeningFloat, DateTime OpenedUtc);
public sealed record ShiftOpened(Guid ShiftId);
public sealed record DenominationCount(decimal Denomination, int Quantity);
public sealed record MethodTotal(PaymentMethod Method, decimal SalePayments, decimal Voids, decimal Refunds, decimal CustomerPayments, decimal Net);
public sealed record ZReport(Guid ShiftId, long? ZNumber, string Register, string Cashier, DateTime OpenedUtc, DateTime? ClosedUtc, decimal OpeningFloat,
    MethodTotal[] ByMethod, int SaleCount, int VoidedCount, decimal CustomerPayments, decimal Rounding, decimal CashIn, decimal Withdrawals, decimal Expenses,
    decimal ExpectedCash, decimal? CountedCash, decimal? Difference, Guid? ClosingSupervisorId = null, string? AuditChainHead = null, decimal OrderDeposits = 0,
    int DrawerOpenings = 0, int RemovedLines = 0, decimal RemovedAmount = 0);

// Sales
public sealed record Discount(bool IsPercentage, decimal Value);
public sealed record SaleLine(Guid ProductId, Guid? PresentationId, decimal Quantity, decimal? UnitPrice = null, Discount? Discount = null,
    string? Note = null, string? ScaleLabel = null);
public sealed record Payment(PaymentMethod Method, decimal Amount, string? Reference = null);
public sealed record RegisterSale(Guid SaleId, Guid ShiftId, List<SaleLine> Lines, List<Payment> Payments, decimal ExpectedTotal,
    EcfType DocumentType, Guid? CustomerId, string? CustomerRnc, string? CustomerName, PriceLevel PriceLevel, List<Guid> SupervisorApprovals,
    Guid? OrderId = null);
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
    decimal NetWithoutItbis, decimal? Cost, decimal? Profit);
public sealed record ProductSales(Guid ProductId, string Product, decimal Quantity, string Unit, decimal NetWithoutItbis, decimal? Profit);
public sealed record PaymentMethodSales(PaymentMethod Method, int SaleCount, decimal Paid, decimal Refunded, decimal Net);
public sealed record HourlySales(DateOnly Day, int Hour, int SaleCount, decimal SalesWithItbis);
public sealed record ShiftSummary(Guid ShiftId, string Register, string Cashier, DateTime OpenedUtc, DateTime? ClosedUtc, long? ZNumber, decimal OpeningFloat,
    decimal? ExpectedCash, decimal? CountedCash, decimal? Difference);
public sealed record Receivable(Guid CustomerId, string Customer, decimal Over90, decimal Total, decimal CreditLimit);
/// <summary>Per receipt type of the current environment (TK-376): <c>Alert</c> under the threshold or within 30 days of expiry; <c>Critical</c> none left or the last day.</summary>
public sealed record SequenceAlert(string Type, long Available, DateOnly? ExpiresOn, bool Alert, bool Critical, int ThresholdPercent);
/// <summary>e-CF waiting to be sent; each must go out before its 72-hour deadline. And when the store's certificate expires (TK-176).</summary>
public sealed record FiscalQueue(int Pending, int Overdue, DateTime? NextDeadlineUtc, int Rejected, int Simulated, DateTime? CertificateExpiresUtc = null);
/// <summary>TK-254: Configuración → Actualizaciones.</summary>
public sealed record RegisterUpdate(Guid DeviceId, string Version, string? PendingVersion, DateTime ReportedUtc, bool RestartRequested);
public sealed record UpdatesInfo(bool Configured, string ServerVersion, string? AvailableVersion, string? AllowedVersion, string Channel, bool OutsideHours,
    DateTime? RequestedUtc, string? NotesUrl, List<RegisterUpdate> Registers);
/// <summary>TK-254: the server's answer to a register's version report.</summary>
public sealed record RegisterUpdateAnswer(string Channel, string? AllowedVersion, string? PendingVersion, bool RestartRequested);

/// <summary>TK-342: Talia's answer through the store server. <paramref name="Screen"/> is a route of storeScreens.json for the "Abrir …" button.</summary>
public sealed record TaliaAnswer(string Answer, string? Screen);
public sealed record TaliaTurn(string Role, string Content);
