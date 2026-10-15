using Toki.CashRegister.Contracts;
using Toki.Settings.Contracts;
using Toki.Fiscal.Contracts;
using Toki.Inventory.Contracts;
using Toki.SharedKernel;

namespace Toki.Sales.Domain;

public sealed record ReceiptLine(string Description, decimal Quantity, UnitOfMeasure Unit, decimal Price, decimal Discount, decimal Total, string? Note);
public sealed record ReceiptItbis(string Rate, decimal Base, decimal Itbis);
/// <param name="Method">Payment method name as printed (Spanish), e.g. "Efectivo".</param>
public sealed record ReceiptPayment(string Method, decimal Amount, decimal? Received);

/// <summary>Print model of a receipt. The register renders it to ESC/POS for its own printer (58 or 80 mm).
/// Its texts (document type, ITBIS rates, payment methods) are printed as they are, so they are Spanish.</summary>
public sealed record Receipt(
    string BusinessName, string? Rnc, string? Address, string? Phone, string DocumentType, string ENcf, long Number, DateTime LocalDateTime,
    string Register, string Cashier, string? Customer, string? CustomerRnc, IReadOnlyList<ReceiptLine> Lines, decimal Subtotal, decimal Discount,
    IReadOnlyList<ReceiptItbis> Itbis, decimal Total, IReadOnlyList<ReceiptPayment> Payments, decimal Change, string SecurityCode, string QrUrl,
    string? Footer, bool IsVoided)
{
    internal static Receipt From(Sale s, BusinessProfile b)
    {
        var itbis = s.Lines.GroupBy(l => l.ItbisRate).OrderBy(g => g.Key)
            .Select(g => new ReceiptItbis(RateName(g.Key), g.Sum(l => l.Total - l.Itbis), g.Sum(l => l.Itbis))).ToList();
        return new Receipt(b.Name, b.Rnc, b.Address, b.Phone, TypeName(s.DocumentType), s.ENcf, s.Number,
            s.DateUtc + BusinessDay.Offset, s.Register, s.Cashier, s.CustomerName, s.CustomerRnc,
            s.Lines.OrderBy(l => l.Position).Select(l => new ReceiptLine(l.Description, l.Quantity, l.Unit, l.UnitPrice, l.Discount, l.Total, l.Note)).ToList(),
            s.Subtotal, s.Discount, itbis, s.Total, s.Payments.Select(p => new ReceiptPayment(PaymentMethodNames.Display(p.Method), p.Amount, p.Received)).ToList(), s.Change,
            s.SecurityCode, s.QrUrl, b.ReceiptFooter, s.Status == SaleStatus.Voided);
    }

    private static string TypeName(EcfType t) => t == EcfType.FiscalCredit31 ? "Factura de Crédito Fiscal Electrónica" : "Factura de Consumo Electrónica";

    private static string RateName(ItbisRate r) => r switch
    {
        ItbisRate.General18 => "ITBIS 18%",
        ItbisRate.Reduced16 => "ITBIS 16%",
        ItbisRate.Zero => "ITBIS 0%",
        _ => "Exento",
    };
}

/// <summary>Payment method names as a person in the store reads them (receipt, messages).</summary>
internal static class PaymentMethodNames
{
    public static string Display(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "Efectivo",
        PaymentMethod.Card => "Tarjeta",
        PaymentMethod.Transfer => "Transferencia",
        PaymentMethod.OnAccount => "Fiado",
        PaymentMethod.CreditBalance => "Saldo a favor",
        _ => m.ToString(),
    };
}
