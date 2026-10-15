namespace Toki.BuildingBlocks.Security;

/// <summary>Every action that can be granted to a role. Checked on the server, never only in the UI.</summary>
public static class Permissions
{
    public const string UsersManage = "users.manage";
    public const string DevicesManage = "devices.manage";
    public const string SettingsManage = "settings.manage";

    public const string ProductsManage = "products.manage";
    public const string PricesChange = "prices.change";
    public const string InventoryView = "inventory.view";
    public const string InventoryAdjust = "inventory.adjust";
    public const string InventoryCount = "inventory.count";
    public const string InventoryTransfer = "inventory.transfer";

    public const string RegisterOperate = "register.operate";
    public const string RegisterWithdraw = "register.withdraw";
    public const string RegisterApproveClose = "register.approve-close";
    public const string RegisterOpenDrawer = "register.open-drawer";

    public const string SalesCreate = "sales.create";
    public const string SalesVoid = "sales.void";
    public const string SalesReturn = "sales.return";
    public const string SalesLargeDiscount = "sales.large-discount";
    public const string SalesPriceOverride = "sales.price-override";

    public const string PurchasingManage = "purchasing.manage";
    public const string CustomersManage = "customers.manage";
    public const string CreditManage = "credit.manage";
    public const string CreditExceedLimit = "credit.exceed-limit";
    public const string FiscalManage = "fiscal.manage";
    public const string ReportsView = "reports.view";

    public static readonly IReadOnlyList<string> All =
    [
        UsersManage, DevicesManage, SettingsManage,
        ProductsManage, PricesChange, InventoryView, InventoryAdjust, InventoryCount, InventoryTransfer,
        RegisterOperate, RegisterWithdraw, RegisterApproveClose, RegisterOpenDrawer,
        SalesCreate, SalesVoid, SalesReturn, SalesLargeDiscount, SalesPriceOverride,
        PurchasingManage, CustomersManage, CreditManage, CreditExceedLimit, FiscalManage, ReportsView,
    ];
}
