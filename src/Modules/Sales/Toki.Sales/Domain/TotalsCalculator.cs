using Toki.CashRegister.Contracts;
using Toki.SharedKernel;

namespace Toki.Sales.Domain;

public sealed record Discount(bool IsPercentage, decimal Value);

/// <param name="FixedAmount">Line amount read from a price label: the line total is fixed, quantity is informative.</param>
internal sealed record LineInput(decimal UnitPrice, decimal Quantity, ItbisRate Rate, Discount? Discount, decimal? FixedAmount = null);

internal sealed record CalculatedLine(decimal Gross, decimal Discount, decimal Total, decimal Itbis);

internal sealed record Totals(IReadOnlyList<CalculatedLine> Lines, decimal Gross, decimal Discount, decimal Subtotal, decimal Itbis, decimal Total)
{
    public decimal DiscountPercent => Gross == 0 ? 0 : Math.Round(Discount / Gross * 100, 2);
}

/// <summary>The only place where a sale's money is computed. Shelf prices include ITBIS (DR retail),
/// so ITBIS is extracted from each line's final amount. Rounding: 2 decimals, half away from zero, per line.</summary>
internal static class TotalsCalculator
{
    public static Result<Totals> Calculate(IReadOnlyList<LineInput> lines)
    {
        var calculated = new List<CalculatedLine>(lines.Count);
        foreach (var l in lines)
        {
            var gross = l.FixedAmount ?? Money.Round(l.UnitPrice * l.Quantity);
            var d = DiscountAmount(l.Discount, gross);
            if (d.IsFailure) return d.Error!;
            var total = gross - d.Value;
            calculated.Add(new CalculatedLine(gross, d.Value, total, Itbis.Included(total, l.Rate)));
        }

        var saleTotal = calculated.Sum(c => c.Total);
        var itbis = calculated.Sum(c => c.Itbis);
        return new Totals(calculated, calculated.Sum(c => c.Gross), calculated.Sum(c => c.Discount), saleTotal - itbis, itbis, saleTotal);
    }

    private static Result<decimal> DiscountAmount(Discount? d, decimal over)
    {
        if (d is null || d.Value == 0) return 0m;
        if (d.Value < 0 || (d.IsPercentage && d.Value > 100))
            return Error.Validation("Sales.InvalidDiscount", "El descuento debe estar entre 0 y 100 %, o ser un monto positivo.");
        var amount = Money.Round(d.IsPercentage ? over * d.Value / 100 : d.Value);
        if (amount > over) return Error.Validation("Sales.InvalidDiscount", "El descuento es mayor que el importe.");
        return amount;
    }
}

internal sealed record PaymentInput(PaymentMethod Method, decimal Amount, string? Reference);

/// <param name="Amount">Amount applied to the sale (cash received minus change).</param>
/// <param name="Received">For cash: what the customer handed over.</param>
internal sealed record AppliedPayment(PaymentMethod Method, decimal Amount, decimal? Received, string? Reference);

internal static class Payments
{
    /// <summary>Mixed payments must add up to the total. Only cash may exceed it, and the excess is the change.
    /// There are no coins under RD$ 1: cash comes in whole pesos and the change is rounded down; the kept centavos are
    /// <c>Rounding</c>, so the drawer's expected cash is always a countable amount.</summary>
    public static Result<(IReadOnlyList<AppliedPayment> Payments, decimal Change, decimal Rounding)> Apply(decimal total, IReadOnlyList<PaymentInput> payments)
    {
        if (payments.Any(p => p.Amount <= 0 || p.Amount != Math.Round(p.Amount, 2)))
            return Error.Validation("Sales.InvalidPayment", "Cada pago debe ser un monto positivo con hasta 2 decimales.");
        if (payments.Any(p => p.Method == PaymentMethod.Cash && p.Amount != Math.Truncate(p.Amount)))
            return Error.Validation("Sales.CashNotWholePesos", "El efectivo se recibe en pesos enteros.");
        var cash = payments.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.Amount);
        var others = payments.Where(p => p.Method != PaymentMethod.Cash).Sum(p => p.Amount);
        if (others > total) return Error.Validation("Sales.PaymentExceedsTotal", "Solo el efectivo puede pasar del total (la devuelta sale del efectivo).");
        if (cash + others < total) return Error.Validation("Sales.PaymentIncomplete", $"Faltan RD$ {total - cash - others:N2}.");

        var excess = cash + others - total;
        var change = Math.Floor(excess);
        var toDeduct = excess;
        var applied = new List<AppliedPayment>();
        foreach (var p in payments.Reverse())
        {
            if (p.Method != PaymentMethod.Cash) { applied.Add(new AppliedPayment(p.Method, p.Amount, null, p.Reference)); continue; }
            var deducted = Math.Min(toDeduct, p.Amount);
            toDeduct -= deducted;
            applied.Add(new AppliedPayment(p.Method, p.Amount - deducted, p.Amount, p.Reference));
        }
        applied.Reverse();
        return (applied.Where(a => a.Amount > 0).ToList(), change, excess - change);
    }
}
