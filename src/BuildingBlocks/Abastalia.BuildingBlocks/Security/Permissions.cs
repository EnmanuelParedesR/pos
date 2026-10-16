namespace Abastalia.BuildingBlocks.Security;

/// <summary>Every action that can be granted to a role. Checked on the server, never only in the UI. Segmented in M16 (docs/permisos.md).</summary>
public static class Permissions
{
    public const string UsersManage = "users.manage";
    public const string DevicesManage = "devices.manage";
    /// <summary>Business data, receipt, approval policy, cloud and license. Registers, backups and audit have their own (TK-213).</summary>
    public const string SettingsManage = "settings.manage";
    /// <summary>Create and rename registers and assign their device.</summary>
    public const string RegistersManage = "registers.manage";
    public const string BackupsManage = "backups.manage";
    public const string AuditView = "audit.view";

    public const string ProductsManage = "products.manage";
    public const string PricesChange = "prices.change";
    public const string InventoryView = "inventory.view";
    public const string InventoryAdjust = "inventory.adjust";
    public const string InventoryCount = "inventory.count";
    public const string InventoryTransfer = "inventory.transfer";

    /// <summary>TK-212: open a shift with its float. Without it a supervisor who has it authorizes the opening.</summary>
    public const string RegisterOpen = "register.open";
    /// <summary>Close one's own shift with a blind count. A difference asks for register.approve-close when the store's policy says so (TK-210).</summary>
    public const string RegisterClose = "register.close";
    public const string RegisterCashIn = "register.cash-in";
    public const string RegisterWithdraw = "register.withdraw";
    /// <summary>Approve closings with a difference and use other registers' shifts.</summary>
    public const string RegisterApproveClose = "register.approve-close";
    public const string RegisterOpenDrawer = "register.open-drawer";

    public const string SalesCreate = "sales.create";
    public const string SalesVoid = "sales.void";
    public const string SalesReturn = "sales.return";
    public const string SalesLargeDiscount = "sales.large-discount";
    public const string SalesPriceOverride = "sales.price-override";
    /// <summary>Remove lines or void the sale in progress (nothing charged yet). Asked only when the store's approval policy says so (TK-210).</summary>
    public const string SalesRemoveLine = "sales.remove-line";

    /// <summary>Suppliers, purchases, suggested order, receiving merchandise. Paying is <see cref="PayablesPay"/> (TK-214).</summary>
    public const string PurchasingManage = "purchasing.manage";
    /// <summary>Pay suppliers and record expenses with NCF: money leaves the business.</summary>
    public const string PayablesPay = "payables.pay";
    public const string CustomersManage = "customers.manage";
    /// <summary>R11 A9: add a customer at the register (name, RNC or cédula, phone), without credit limit, wholesale prices or editing others.</summary>
    public const string CustomersQuickAdd = "customers.quick-add";
    /// <summary>Collect customer payments (fiado) at the register.</summary>
    public const string CreditCollect = "credit.collect";
    /// <summary>R11 A6: sell on credit (fiado) or with the customer's credit balance. Without it a supervisor authorizes the sale.</summary>
    public const string CreditSell = "credit.sell";
    public const string CreditManage = "credit.manage";
    public const string CreditExceedLimit = "credit.exceed-limit";
    /// <summary>Certificate, ranges, voids, taxpayer registry.</summary>
    public const string FiscalManage = "fiscal.manage";
    /// <summary>DGII 606, 607 and 608, without touching the certificate or the ranges.</summary>
    public const string FiscalReports = "fiscal.reports";
    /// <summary>Sales and inventory reports.</summary>
    public const string ReportsView = "reports.view";
    /// <summary>Turnos y cuadres: every register's shifts and Z reports.</summary>
    public const string ShiftsView = "shifts.view";
    /// <summary>TK-201: cost, average cost, profit, margin and stock value. Without it the API answers those fields as null.</summary>
    public const string CostsView = "costs.view";

    public static readonly IReadOnlyList<string> All =
    [
        UsersManage, DevicesManage, SettingsManage, RegistersManage, BackupsManage, AuditView,
        ProductsManage, PricesChange, InventoryView, InventoryAdjust, InventoryCount, InventoryTransfer,
        RegisterOpen, RegisterClose, RegisterCashIn, RegisterWithdraw, RegisterApproveClose, RegisterOpenDrawer,
        SalesCreate, SalesVoid, SalesReturn, SalesLargeDiscount, SalesPriceOverride, SalesRemoveLine,
        PurchasingManage, PayablesPay, CustomersManage, CustomersQuickAdd, CreditCollect, CreditSell, CreditManage, CreditExceedLimit,
        FiscalManage, FiscalReports, ReportsView, ShiftsView, CostsView,
    ];
}
