using Shouldly;
using Toki.Client.UI.Api;
using Toki.Client.UI.State;
using Toki.Escuadra;
using Toki.Escuadra.Components;

namespace Toki.Client.UI.Tests;

public sealed class CartTests
{
    private static ProductByCode Product(string unit, decimal price, decimal? labelQuantity = null, decimal? labelAmount = null) =>
        new(Guid.NewGuid(), null, "Prueba", unit, "Exempt", price, null, 1m, labelQuantity, labelAmount, [], null);

    [Fact]
    public void The_same_per_unit_product_adds_up_on_its_line_and_weighed_ones_go_apart()
    {
        var c = new Cart();
        var salami = Product("Unit", 210m);
        c.Add(salami, null);
        c.Add(salami, null);
        c.Lines.Count.ShouldBe(1);
        c.Lines[0].Quantity.ShouldBe(2m);

        var breast = Product("Pound", 165m);
        c.Add(breast, null, 2.35m);
        c.Add(breast, null, 1m);
        c.Lines.Count.ShouldBe(3);
        c.Total.ShouldBe(420m + 387.75m + 165m);
    }

    [Fact]
    public void A_price_label_charges_its_amount_and_is_detected_when_repeated()
    {
        var c = new Cart();
        var line = c.Add(Product("Pound", 165m, labelAmount: 412.50m), "2201020041250");
        line.Total.ShouldBe(412.50m);
        line.Quantity.ShouldBe(2.5m);
        line.ToSaleLine().ScaleLabel.ShouldBe("2201020041250");
        c.HasLabel("2201020041250").ShouldBeTrue();
    }

    [Fact]
    public void Percentage_and_amount_discounts_and_a_new_id_after_clearing()
    {
        var c = new Cart();
        var l = c.Add(Product("Unit", 210m), null);
        c.Update(l with { Discount = new Discount(true, 20m) });
        c.Total.ShouldBe(168m);
        c.Update(c.Lines[0] with { Discount = new Discount(false, 500m) });
        c.Total.ShouldBe(0m); // a fixed discount never goes below zero
        var id = c.SaleId;
        c.Clear();
        c.SaleId.ShouldNotBe(id);
        c.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void A_held_sale_comes_back_the_same()
    {
        var c = new Cart();
        c.Add(Product("Pound", 165m), null, 1.5m);
        c.DocumentType = EcfType.FiscalCredit31;
        c.ManualRnc = "131245678";
        var json = System.Text.Json.JsonSerializer.SerializeToElement(c.Save(), TokiApi.Json);

        var other = new Cart();
        other.Restore(System.Text.Json.JsonSerializer.Deserialize<SavedCart>(json, TokiApi.Json)!);
        other.Total.ShouldBe(247.50m);
        other.DocumentType.ShouldBe(EcfType.FiscalCredit31);
        other.ManualRnc.ShouldBe("131245678");
    }

    [Theory]
    [InlineData("12", "3", "123")]
    [InlineData("12", "backspace", "1")]
    [InlineData("1.5", ".", "1.5")]
    [InlineData("12", "clear", "")]
    public void The_keypad(string current, string key, string expected) => Keypad.Apply(current, key).ShouldBe(expected);

    [Fact]
    public void Money_format_and_number_parsing()
    {
        Format.Amount(1234.5m).ShouldBe("1,234.50");
        Format.ParseNumber("1,250.5").ShouldBe(1250.5m);
        Format.ParseNumber("abc").ShouldBeNull();
        Format.Quantity(2.35m, "Pound").ShouldBe("2.350 lb");
    }

    [Fact]
    public void The_permission_the_api_asks_for_is_read_from_the_detail()
    {
        new Problem(403, "Approval.Required", "Esta acción necesita la autorización de un supervisor (sales.void).").PermissionRequired.ShouldBe("sales.void");
        new Problem(403, "Other", "x (y)").PermissionRequired.ShouldBeNull();
    }
}
