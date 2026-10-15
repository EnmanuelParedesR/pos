using System.Globalization;
using Toki.Client.UI.Api;
using Toki.Escuadra;

namespace Toki.Client.UI;

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

