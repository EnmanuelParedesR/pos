using System.Globalization;
using Abastalia.Client.UI.Api;
using Abastalia.Escuadra;

namespace Abastalia.Client.UI;

/// <summary>How inventory and purchasing enums and quantities read in the back office (TK-184, TK-185).</summary>
public static class AdminNames
{
    /// <summary>"27.8 lb" or "12": the owner reads stock, not the scale's thousandths.</summary>
    public static string Stock(decimal value, UnitOfMeasure unit) =>
        value.ToString("#,##0.##", CultureInfo.InvariantCulture) + (unit == UnitOfMeasure.Pound ? " lb" : "");

    /// <summary>"+3", "−1.25 lb": the sign always shows.</summary>
    public static string Signed(decimal value, UnitOfMeasure unit) => (value > 0 ? "+" : value < 0 ? "−" : "") + Stock(Math.Abs(value), unit);

    public static string Movement(StockMovementType type) => type switch
    {
        StockMovementType.Sale => "Venta",
        StockMovementType.SaleVoid => "Venta anulada",
        StockMovementType.Return => "Devolución",
        StockMovementType.Purchase => "Compra",
        StockMovementType.PurchaseVoid => "Compra anulada",
        StockMovementType.Adjustment => "Ajuste",
        StockMovementType.Count => "Conteo físico",
        StockMovementType.TransferOut => "Transferencia (salida)",
        _ => "Transferencia (entrada)",
    };

    public static string Reason(AdjustmentReason reason) => reason switch
    {
        AdjustmentReason.Shrinkage => "Merma",
        AdjustmentReason.Damage => "Daño",
        AdjustmentReason.InternalUse => "Consumo interno",
        AdjustmentReason.Gift => "Regalo",
        _ => "Corrección",
    };

    /// <summary>The kardex keeps the words saved before the code was in English ("Danio: se cayó"); this is how they read.</summary>
    public static string? KardexReason(string? reason) => reason is null ? null : reason
        .Replace("Danio", "Daño", StringComparison.Ordinal)
        .Replace("ConsumoInterno", "Consumo interno", StringComparison.Ordinal)
        .Replace("Correccion", "Corrección", StringComparison.Ordinal);

    public static string PurchaseStatus(string status) => status switch
    {
        "Draft" => "Borrador",
        "Posted" => "Recibida",
        _ => "Anulada",
    };

    public static string Terms(string terms) => terms == nameof(PaymentTerms.Credit) ? "A crédito" : "Al contado";

    public static string Method(SupplierPaymentMethod method) => method switch
    {
        SupplierPaymentMethod.Cash => "Efectivo",
        SupplierPaymentMethod.Transfer => "Transferencia",
        SupplierPaymentMethod.Check => "Cheque",
        _ => "Tarjeta",
    };

    /// <summary>The DGII 606 names, shortened only where they would not fit a select.</summary>
    public static string Expense(ExpenseType type) => type switch
    {
        ExpenseType.PersonnelExpenses => "Gastos de personal",
        ExpenseType.WorkSuppliesAndServices => "Trabajos, suministros y servicios",
        ExpenseType.Leases => "Arrendamientos",
        ExpenseType.FixedAssetExpenses => "Gastos de activos fijos",
        ExpenseType.RepresentationExpenses => "Gastos de representación",
        ExpenseType.OtherAllowedDeductions => "Otras deducciones admitidas",
        ExpenseType.FinancialExpenses => "Gastos financieros",
        ExpenseType.ExtraordinaryExpenses => "Gastos extraordinarios",
        ExpenseType.CostOfSales => "Parte del costo de venta",
        ExpenseType.AssetAcquisitions => "Adquisición de activos",
        _ => "Gastos de seguros",
    };

    /// <summary>The audit log's action codes as the owner reads them (TK-213). A code not listed here shows as it is.</summary>
    public static readonly IReadOnlyDictionary<string, string> AuditActions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["sales.create"] = "Venta", ["sales.void"] = "Venta anulada", ["sales.return"] = "Devolución",
        ["orders.create"] = "Pedido creado", ["quotes.create"] = "Cotización creada", ["orders.cancel"] = "Pedido cancelado", ["orders.ready"] = "Pedido listo",
        ["cash.cash-in"] = "Entrada de efectivo", ["cash.withdrawal"] = "Retiro de efectivo", ["cash.expense"] = "Gasto de caja",
        ["cash.open-shift"] = "Abrió turno", ["cash.close-shift"] = "Cerró turno", ["cash.create-register"] = "Caja creada",
        ["cash.rename-register"] = "Caja renombrada", ["cash.assign-device"] = "Equipo asignado a caja", ["cash.open-drawer"] = "Gaveta abierta sin venta",
        ["customers.save"] = "Cliente guardado", ["customers.payment"] = "Abono de cliente", ["customers.void-payment"] = "Abono anulado",
        ["inventory.create-product"] = "Producto creado", ["inventory.update-product"] = "Producto editado", ["inventory.adjustment"] = "Ajuste de inventario",
        ["inventory.transfer"] = "Transferencia", ["inventory.apply-stock-count"] = "Conteo físico aplicado", ["inventory.save-category"] = "Categoría guardada",
        ["inventory.save-presentation"] = "Presentación guardada", ["inventory.add-barcode"] = "Código de barras agregado",
        ["inventory.remove-barcode"] = "Código de barras quitado", ["inventory.create-warehouse"] = "Almacén creado", ["inventory.label-formats"] = "Etiquetas de balanza",
        ["catalog.update"] = "Catálogo en línea",
        ["purchasing.post"] = "Compra recibida", ["purchasing.void"] = "Compra anulada", ["purchasing.record-expense"] = "Gasto registrado",
        ["purchasing.void-expense"] = "Gasto anulado", ["purchasing.pay-supplier"] = "Pago a suplidor", ["purchasing.void-payment"] = "Pago a suplidor anulado",
        ["purchasing.save-supplier"] = "Suplidor guardado",
        ["fiscal.load-sequence"] = "Secuencia cargada", ["fiscal.void-sequence"] = "Secuencia anulada", ["fiscal.void-document"] = "Comprobante anulado",
        ["fiscal.replace-rejected"] = "Comprobante rechazado reemplazado", ["fiscal.import-certificate"] = "Certificado digital instalado",
        ["identity.setup"] = "Primera configuración", ["identity.create-user"] = "Usuario creado", ["identity.update-user"] = "Usuario editado",
        ["identity.pin-failed"] = "PIN equivocado", ["identity.save-role"] = "Rol guardado",
        ["identity.delete-role"] = "Rol borrado", ["identity.pair"] = "Equipo emparejado", ["identity.revoke"] = "Equipo desconectado",
        ["approval.granted"] = "Autorización de supervisor", ["approval.used"] = "Autorización usada",
        ["settings.business"] = "Datos del negocio", ["settings.approvals"] = "Autorizaciones", ["settings.backup-passphrase"] = "Frase de respaldos",
        ["settings.restore-backup"] = "Respaldo restaurado", ["settings.license"] = "Licencia",
        ["sync.connect"] = "Conectada a la nube", ["sync.disconnect"] = "Desconectada de la nube",
    };

    public static string AuditAction(string code) => AuditActions.GetValueOrDefault(code, code);

    /// <summary>What an audit record is about. Approvals name the command they unlocked ("VoidSale"): those show as they are.</summary>
    public static string AuditResource(string type) => type switch
    {
        "Business" => "Negocio", "Category" => "Categoría", "CloudConnection" => "Nube", "Customer" => "Cliente", "Device" => "Equipo",
        "EcfCertificate" => "Certificado digital", "EcfVoid" => "Anulación de e-NCF", "Expense" => "Gasto", "FiscalDocument" => "Comprobante",
        "LabelFormat" => "Etiquetas", "License" => "Licencia", "NcfSequence" => "Secuencia", "Product" => "Producto", "Purchase" => "Compra",
        "Register" => "Caja", "Role" => "Rol", "Sale" => "Venta", "Shift" => "Turno", "CloseShift" => "Cierre de turno", "StockCount" => "Conteo físico",
        "SupervisorApproval" => "Autorización", "Supplier" => "Suplidor", "Transfer" => "Transferencia", "User" => "Usuario", "Warehouse" => "Almacén",
        "Order" => "Pedido", "CatalogSettings" => "Catálogo en línea",
        _ => type,
    };

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-DO");

    /// <summary>"15 oct 2026".</summary>
    public static string Day(DateOnly day) => day.ToString("d MMM yyyy", Es).Replace(".", "", StringComparison.Ordinal);

    public static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>The periods of the purchases and expenses lists (invoice dates, store days).</summary>
public static class Periods
{
    public static readonly IReadOnlyList<(string, string)> Options = [("month", "Este mes"), ("last", "Mes pasado"), ("90", "90 días")];

    public static (DateOnly From, DateOnly To) Range(string period)
    {
        var today = Format.Today;
        var first = new DateOnly(today.Year, today.Month, 1);
        return period switch
        {
            "last" => (first.AddMonths(-1), first.AddDays(-1)),
            "90" => (today.AddDays(-89), today),
            _ => (first, today),
        };
    }
}

