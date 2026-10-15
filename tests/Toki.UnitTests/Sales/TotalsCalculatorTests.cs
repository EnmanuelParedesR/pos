using Shouldly;
using Toki.CashRegister.Contracts;
using Toki.SharedKernel;
using Toki.Sales.Domain;

namespace Toki.UnitTests.Sales;

public sealed class TotalsCalculatorTests
{
    private static Totals Calculate(Discount? global, params LineInput[] lines) => TotalsCalculator.Calculate(lines, global).Value;

    [Fact]
    public void Itbis_is_taken_out_of_a_price_that_already_includes_it()
    {
        var t = Calculate(null, new LineInput(118m, 2, ItbisRate.General18, null));
        t.Total.ShouldBe(236m);
        t.Itbis.ShouldBe(36m);
        t.Subtotal.ShouldBe(200m);
    }

    [Fact]
    public void Each_line_uses_its_rate()
    {
        var t = Calculate(null,
            new LineInput(118m, 1, ItbisRate.General18, null),
            new LineInput(116m, 1, ItbisRate.Reduced16, null),
            new LineInput(50m, 1, ItbisRate.Exempt, null));
        t.Itbis.ShouldBe(34m);
        t.Total.ShouldBe(284m);
    }

    [Fact]
    public void A_quantity_in_pounds_is_rounded_per_line()
    {
        // 2.355 lb × 165.00 = 388.575 → 388.58
        var t = Calculate(null, new LineInput(165m, 2.355m, ItbisRate.Exempt, null));
        t.Total.ShouldBe(388.58m);
    }

    [Fact]
    public void The_global_discount_is_spread_and_the_rounding_remainder_goes_to_the_largest_line()
    {
        var t = Calculate(new Discount(false, 10m),
            new LineInput(100m, 1, ItbisRate.General18, null),
            new LineInput(100m, 1, ItbisRate.General18, null),
            new LineInput(100m, 1, ItbisRate.General18, null));
        t.Discount.ShouldBe(10m);
        t.Lines.Sum(l => l.GlobalDiscount).ShouldBe(10m);
        t.Total.ShouldBe(290m);
    }

    [Fact]
    public void Line_discount_as_a_percentage()
    {
        var t = Calculate(null, new LineInput(200m, 1, ItbisRate.General18, new Discount(true, 15m)));
        t.Discount.ShouldBe(30m);
        t.Total.ShouldBe(170m);
        t.DiscountPercent.ShouldBe(15m);
    }

    [Fact]
    public void A_discount_larger_than_the_amount_is_an_error()
    {
        var r = TotalsCalculator.Calculate([new LineInput(50m, 1, ItbisRate.General18, new Discount(false, 60m))], null);
        r.Error!.Code.ShouldBe("Sales.InvalidDiscount");
    }

    [Fact]
    public void A_price_label_fixes_the_line_amount()
    {
        var t = Calculate(null, new LineInput(165m, 2.35m, ItbisRate.Exempt, null, FixedAmount: 387.75m));
        t.Total.ShouldBe(387.75m);
    }

    [Fact]
    public void A_price_label_accepts_a_line_discount()
    {
        var t = Calculate(null, new LineInput(165m, 2.35m, ItbisRate.Exempt, new Discount(true, 10m), FixedAmount: 387.75m));
        t.Lines[0].Gross.ShouldBe(387.75m);
        t.Discount.ShouldBe(38.78m);
        t.Total.ShouldBe(348.97m);
    }

    [Fact]
    public void Global_percentage_discount_over_different_rates()
    {
        // 10 % off 118 (18 %) + 116 (16 %) + 50 (exempt): each line keeps its own rate after the discount.
        var t = Calculate(new Discount(true, 10m),
            new LineInput(118m, 1, ItbisRate.General18, null),
            new LineInput(116m, 1, ItbisRate.Reduced16, null),
            new LineInput(50m, 1, ItbisRate.Exempt, null));
        t.Discount.ShouldBe(28.40m);
        t.Lines.Select(l => l.Total).ShouldBe([106.20m, 104.40m, 45m]);
        t.Lines.Select(l => l.Itbis).ShouldBe([16.20m, 14.40m, 0m]);
        t.Total.ShouldBe(255.60m);
        t.Itbis.ShouldBe(30.60m);
    }

    [Fact]
    public void A_100_percent_discount_leaves_the_line_at_zero()
    {
        var t = Calculate(null, new LineInput(59m, 2, ItbisRate.General18, new Discount(true, 100m)), new LineInput(100m, 1, ItbisRate.Exempt, null));
        t.Lines[0].Total.ShouldBe(0m);
        t.Lines[0].Itbis.ShouldBe(0m);
        t.Total.ShouldBe(100m);
        t.DiscountPercent.ShouldBe(54.13m);
    }
}

public sealed class PaymentsTests
{
    [Fact]
    public void Cash_gives_change_in_whole_pesos()
    {
        var r = Payments.Apply(376.75m, [new PaymentInput(PaymentMethod.Cash, 500m, null)]).Value;
        r.Change.ShouldBe(123m);
        r.Rounding.ShouldBe(0.25m);
        r.Payments.Single().Amount.ShouldBe(376.75m);
        r.Payments.Single().Received.ShouldBe(500m);
    }

    [Fact]
    public void The_cents_of_the_change_stay_as_rounding()
    {
        var r = Payments.Apply(505.75m, [new PaymentInput(PaymentMethod.Cash, 1000m, null)]).Value;
        r.Change.ShouldBe(494m);
        r.Rounding.ShouldBe(0.25m);
        (r.Payments.Sum(p => p.Amount) + r.Rounding + r.Change).ShouldBe(1000m); // what the drawer kept plus the change
    }

    [Fact]
    public void By_card_the_cents_are_charged_exactly()
    {
        var r = Payments.Apply(505.75m, [new PaymentInput(PaymentMethod.Card, 5.75m, null), new PaymentInput(PaymentMethod.Cash, 500m, null)]).Value;
        r.Change.ShouldBe(0m);
        r.Rounding.ShouldBe(0m);
    }

    [Fact]
    public void Cash_with_cents_is_an_error() =>
        Payments.Apply(100m, [new PaymentInput(PaymentMethod.Cash, 100.50m, null)]).Error!.Code.ShouldBe("Sales.CashNotWholePesos");

    [Fact]
    public void Mixed_payment_card_and_cash()
    {
        var r = Payments.Apply(1000m, [new PaymentInput(PaymentMethod.Card, 600m, "AUT123"), new PaymentInput(PaymentMethod.Cash, 500m, null)]).Value;
        r.Change.ShouldBe(100m);
        r.Payments.Single(p => p.Method == PaymentMethod.Cash).Amount.ShouldBe(400m);
        r.Payments.Single(p => p.Method == PaymentMethod.Card).Amount.ShouldBe(600m);
    }

    [Fact]
    public void A_card_cannot_exceed_the_total() =>
        Payments.Apply(100m, [new PaymentInput(PaymentMethod.Card, 150m, null)]).Error!.Code.ShouldBe("Sales.PaymentExceedsTotal");

    [Fact]
    public void Missing_money_is_an_error() =>
        Payments.Apply(100m, [new PaymentInput(PaymentMethod.Cash, 99m, null)]).Error!.Code.ShouldBe("Sales.PaymentIncomplete");
}
