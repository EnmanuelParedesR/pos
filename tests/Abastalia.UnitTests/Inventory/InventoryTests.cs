using Shouldly;
using Abastalia.Inventory.Domain;

namespace Abastalia.UnitTests.Inventory;

public sealed class ScaleLabelTests
{
    private static readonly LabelFormat[] Formats = [.. LabelFormat.Defaults()];

    private static string WithCheckDigit(string twelve) => twelve + ScaleLabel.CheckDigit(twelve);

    [Fact]
    public void Reads_the_weight_from_a_20_label()
    {
        // 20 + PLU 00123 + 02350 (2.350 lb)
        var r = ScaleLabel.Read(WithCheckDigit("200012302350"), Formats)!;
        r.IsSuccess.ShouldBeTrue();
        r.Value.Plu.ShouldBe("00123");
        r.Value.Weight.ShouldBe(2.350m);
        r.Value.Price.ShouldBeNull();
    }

    [Fact]
    public void Reads_the_price_from_a_22_label()
    {
        // 22 + PLU 0456 + 038775 (RD$ 387.75)
        var r = ScaleLabel.Read(WithCheckDigit("220456038775"), Formats)!;
        r.Value.Plu.ShouldBe("0456");
        r.Value.Price.ShouldBe(387.75m);
        r.Value.Weight.ShouldBeNull();
    }

    [Fact]
    public void A_bad_check_digit_is_an_error()
    {
        var good = WithCheckDigit("200012302350");
        var bad = good[..12] + ((good[12] - '0' + 1) % 10);
        ScaleLabel.Read(bad, Formats)!.Error!.Code.ShouldBe("Inventory.LabelMisread");
    }

    [Theory]
    [InlineData("7460312004518")]   // regular product barcode
    [InlineData("2900123023501")]   // prefix 29 not configured
    [InlineData("12345")]
    public void Anything_that_is_not_a_scale_label_returns_null(string code) => ScaleLabel.Read(code, Formats).ShouldBeNull();

    [Fact]
    public void Check_digit_of_a_known_EAN13() =>
        ScaleLabel.CheckDigit("400638133393").ShouldBe(1); // 4006381333931
}

public sealed class AverageCostTests
{
    [Fact]
    public void Weights_by_quantity() =>
        AverageCost.Calculate(stock: 10, currentCost: 100, quantity: 30, inboundCost: 140).ShouldBe(130m);

    [Fact]
    public void Without_stock_it_takes_the_inbound_cost() =>
        AverageCost.Calculate(stock: -2, currentCost: 100, quantity: 5, inboundCost: 120).ShouldBe(120m);

    [Fact]
    public void Voiding_a_purchase_takes_it_out_of_the_average()
    {
        // 50 lb at 100, then 100 lb typed at 1,000 → 700. Voiding it leaves 50 lb and gives back 100.
        var average = AverageCost.Calculate(stock: 50, currentCost: 100, quantity: 100, inboundCost: 1000);
        AverageCost.Reverse(stockAfter: 50, currentCost: average, quantity: 100, inboundCost: 1000).ShouldBe(100m);
        // Most of that purchase was sold already: the cost stays rather than going to zero or below.
        AverageCost.Reverse(stockAfter: 5, currentCost: 700, quantity: 100, inboundCost: 1000).ShouldBeNull();
        AverageCost.Reverse(stockAfter: 0, currentCost: 700, quantity: 100, inboundCost: 1000).ShouldBeNull();
    }
}
