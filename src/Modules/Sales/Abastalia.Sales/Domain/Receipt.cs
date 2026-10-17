using Abastalia.CashRegister.Contracts;
using Abastalia.Settings.Contracts;
using Abastalia.Fiscal.Contracts;
using Abastalia.Inventory.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Sales.Domain;

public sealed record ReceiptLine(string Description, decimal Quantity, UnitOfMeasure Unit, decimal Price, decimal Discount, decimal Total, string? Note);
public sealed record ReceiptItbis(string Rate, decimal Base, decimal Itbis);
/// <param name="Method">Payment method name as printed (Spanish), e.g. "Efectivo".</param>
public sealed record ReceiptPayment(string Method, decimal Amount, decimal? Received);

/// <summary>Print model of a receipt (the DGII "Representación Impresa"). The register renders it to ESC/POS for its own printer (58 or 80 mm).
/// Its texts (document type, ITBIS rates, payment methods, legend) are printed as they are, so they are Spanish.</summary>
/// <param name="QrUrl">The DGII page the QR opens: consultatimbre, or consultatimbrefc for a 32 under RD$250,000, on the environment's host.</param>
/// <param name="SignedAt">Signing date and time (DR time), printed under the QR with the security code.</param>
/// <param name="SequenceExpiresOn">"Válido hasta": the e-NCF range expiry, only on the 31.</param>
/// <param name="ContingencyLegend">Printed when the e-CF was issued while the DGII could not be reached.</param>
/// <param name="LogoVersion">TK-280: the business logo to print on top (the register fetches it once per version), or null.</param>
public sealed record Receipt(
    string BusinessName, string? Rnc, string? Address, string? Phone, string DocumentType, string ENcf, long Number, DateTime LocalDateTime,
    string Register, string Cashier, string? Customer, string? CustomerRnc, IReadOnlyList<ReceiptLine> Lines, decimal Subtotal, decimal Discount,
    IReadOnlyList<ReceiptItbis> Itbis, decimal Total, IReadOnlyList<ReceiptPayment> Payments, decimal Change, string SecurityCode, string QrUrl,
    string? Footer, bool IsVoided, DateTime? SignedAt = null, DateOnly? SequenceExpiresOn = null, string? ContingencyLegend = null, string? LogoVersion = null)
{
    internal static Receipt From(Sale s, BusinessProfile b)
    {
        var itbis = s.Lines.GroupBy(l => l.ItbisRate).OrderBy(g => g.Key)
            .Select(g => new ReceiptItbis(RateName(g.Key), g.Sum(l => l.Total - l.Itbis), g.Sum(l => l.Itbis))).ToList();
        // TK-157: a paper NCF of series B (contingency) has no security code, signing time nor QR, and is not "electrónica".
        var paper = s.ENcf.StartsWith('B');
        return new Receipt(b.Name, b.Rnc, b.Address, b.Phone, paper ? TypeName(s.DocumentType).Replace(" Electrónica", "", StringComparison.Ordinal) : TypeName(s.DocumentType), s.ENcf, s.Number,
            s.DateUtc + BusinessDay.Offset, s.Register, s.Cashier, s.CustomerName, s.CustomerRnc,
            s.Lines.OrderBy(l => l.Position).Select(l => new ReceiptLine(l.Description, l.Quantity, l.Unit, l.UnitPrice, l.Discount, l.Total, l.Note)).ToList(),
            s.Subtotal, s.Discount, itbis, s.Total, s.Payments.Select(p => new ReceiptPayment(PaymentMethodNames.Display(p.Method), p.Amount, p.Received)).ToList(), s.Change,
            // TK-156: an e-CF waiting for the clock is not signed yet (no code either); a reprint after the signature shows both.
            s.SecurityCode, s.QrUrl, b.ReceiptFooter, s.Status == SaleStatus.Voided, s.SecurityCode.Length == 0 ? null : s.SignedUtc + BusinessDay.Offset,
            s.DocumentType == EcfType.FiscalCredit31 ? s.SequenceExpiresOn : null, s.Contingency ? EcfReceiptTexts.ContingencyLegend : null, b.LogoVersion);
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
        PaymentMethod.OnAccount => "A crédito",
        PaymentMethod.CreditBalance => "Saldo a favor",
        _ => m.ToString(),
    };
}
