using System.Globalization;
using System.Xml.Linq;
using Shouldly;
using Abastalia.Fiscal;
using Abastalia.Fiscal.Contracts;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.UnitTests.Fiscal;

/// <summary>TK-143: the e-CF XML of each type validates against the official DGII XSD.</summary>
public sealed class EcfXmlTests
{
    internal static readonly BusinessProfile Issuer = new("Carnicería La Escuadra SRL", "131245678", "Av. Duarte 25, Villa Consuelo, Santo Domingo", null, null, 10m);
    internal static readonly DateTime Now = new(2026, 10, 15, 18, 30, 5, DateTimeKind.Utc); // 14:30:05 in Santo Domingo
    private static readonly DateOnly Expires = new(2028, 12, 31);

    /// <summary>A line as the register computes it: prices include ITBIS, the discount comes off the line.</summary>
    internal static FiscalLine Line(string name, decimal quantity, decimal price, ItbisRate rate, decimal discount = 0)
    {
        var total = Money.Round(price * quantity) - discount;
        return new FiscalLine(name, quantity, price, discount, rate, Itbis.Included(total, rate), total);
    }

    internal static DocumentToIssue Document(EcfType type, IReadOnlyList<FiscalLine> lines, string? rnc = null, string? name = null,
        IReadOnlyList<FiscalPayment>? payments = null, EcfPaymentType paymentType = EcfPaymentType.Cash, string? modified = null, ModificationCode? code = null) =>
        new(type, Guid.NewGuid(), rnc, name, lines, lines.Sum(l => l.Itbis), lines.Sum(l => l.Total), modified, code, paymentType, payments);

    // Exempt rice, beef at 18 % with a discount, yogurt at 16 %.
    internal static readonly FiscalLine[] MixedLines =
    [
        Line("Arroz selecto 1 lb", 5, 45m, ItbisRate.Exempt),
        Line("Res para guisar", 2.35m, 210m, ItbisRate.General18, discount: 20m),
        Line("Yogurt natural", 2, 65m, ItbisRate.Reduced16),
    ];

    private static XDocument Valid(DocumentToIssue doc, DateOnly? expires = null, DateOnly? modifiedOn = null)
    {
        var xml = EcfXml.Build(doc, ENcfFor(doc.Type), Issuer, Now, expires, modifiedOn);
        EcfXml.ValidateUnsigned(xml, doc.Type).ShouldBeEmpty();
        return xml;
    }

    private static string ENcfFor(EcfType type) => $"E{(int)type}0000000001";

    private static string? Value(XDocument xml, string name) => xml.Descendants(name).SingleOrDefault()?.Value;

    [Fact]
    public void A_consumer_32_under_250000_with_mixed_rates_discount_and_two_payments_is_valid()
    {
        var doc = Document(EcfType.Consumer32, MixedLines,
            payments: [new FiscalPayment(EcfPaymentForm.Cash, 500m), new FiscalPayment(EcfPaymentForm.Card, 328.50m)]);
        var xml = Valid(doc);

        Value(xml, "MontoTotal").ShouldBe("828.50");
        Value(xml, "MontoExento").ShouldBe("225.00");
        Value(xml, "ITBIS1").ShouldBe("18");
        Value(xml, "ITBIS2").ShouldBe("16");
        Value(xml, "TotalITBIS1").ShouldBe(MixedLines[1].Itbis.ToString("0.00", CultureInfo.InvariantCulture));
        Value(xml, "MontoGravadoI1").ShouldBe((MixedLines[1].Total - MixedLines[1].Itbis).ToString("0.00", CultureInfo.InvariantCulture));
        Value(xml, "IndicadorMontoGravado").ShouldBe("1");
        Value(xml, "FechaEmision").ShouldBe("15-10-2026");
        Value(xml, "FechaHoraFirma").ShouldBe("15-10-2026 14:30:05");
        xml.Descendants("FormaDePago").Select(f => (f.Element("FormaPago")!.Value, f.Element("MontoPago")!.Value))
            .ShouldBe([("1", "500.00"), ("3", "328.50")]);
        // The 32 does not carry the range expiry; an anonymous buyer leaves only the (required) Comprador area empty.
        xml.Descendants("FechaVencimientoSecuencia").ShouldBeEmpty();
        xml.Descendants().Where(e => !e.HasElements && e.Value.Length == 0).Select(e => e.Name.LocalName).ShouldBe(["Comprador"]);
        var discounted = xml.Descendants("Item").ElementAt(1);
        discounted.Element("DescuentoMonto")!.Value.ShouldBe("20.00");
        discounted.Element("TablaSubDescuento")!.Element("SubDescuento")!.Element("MontoSubDescuento")!.Value.ShouldBe("20.00");
        xml.Descendants("Item").ElementAt(0).Element("DescuentoMonto").ShouldBeNull();
    }

    [Fact]
    public void A_consumer_32_with_a_100_percent_discount_and_no_payments_is_valid()
    {
        // TK-239: the XSD types MontoTotal, MontoItem and PrecioUnitarioItem as "mayor o igual a cero"; TablaFormasPago is left out.
        var xml = Valid(Document(EcfType.Consumer32, [Line("Refresco", 1, 59m, ItbisRate.General18, discount: 59m)]));
        Value(xml, "MontoTotal").ShouldBe("0.00");
        xml.Descendants("TablaFormasPago").ShouldBeEmpty();
    }

    [Fact]
    public void A_consumer_32_of_250000_or_more_identifies_the_buyer()
    {
        var doc = Document(EcfType.Consumer32, [Line("Res en canal", 1250, 210m, ItbisRate.General18)], rnc: "00112345678", name: "Juan Pérez",
            payments: [new FiscalPayment(EcfPaymentForm.CheckOrTransfer, 262_500m)]);
        var xml = Valid(doc);
        Value(xml, "RNCComprador").ShouldBe("00112345678");
        Value(xml, "MontoTotal").ShouldBe("262500.00");
    }

    [Fact]
    public void A_fiscal_credit_31_has_the_buyer_and_the_range_expiry_and_rounds_a_3_decimal_weight()
    {
        var doc = Document(EcfType.FiscalCredit31,
            [Line("Chuleta ahumada", 1.235m, 189.95m, ItbisRate.General18), Line("Plátanos", 10, 15m, ItbisRate.Exempt)],
            rnc: "131245678", name: "Colmado El Sol SRL", paymentType: EcfPaymentType.Credit,
            payments: [new FiscalPayment(EcfPaymentForm.OnCredit, 384.58m)]);
        var xml = Valid(doc, Expires);

        Value(xml, "FechaVencimientoSecuencia").ShouldBe("31-12-2028");
        Value(xml, "RazonSocialComprador").ShouldBe("Colmado El Sol SRL");
        Value(xml, "TipoPago").ShouldBe("2");
        xml.Descendants().Where(e => !e.HasElements && e.Value.Length == 0).ShouldBeEmpty();
        // The XSD allows 2 decimals in the quantity: the price is recomputed so Price × Quantity − Discount = MontoItem still holds.
        foreach (var item in xml.Descendants("Item"))
        {
            decimal D(string n) => decimal.Parse(item.Element(n)?.Value ?? "0", CultureInfo.InvariantCulture);
            Math.Abs(Money.Round(D("PrecioUnitarioItem") * D("CantidadItem")) - D("DescuentoMonto") - D("MontoItem")).ShouldBeLessThanOrEqualTo(0.01m);
        }
        xml.Descendants("CantidadItem").First().Value.ShouldBe("1.24");
    }

    [Theory]
    [InlineData(10, "0")]
    [InlineData(45, "1")] // more than 30 days after the invoice: the note cannot reduce ITBIS
    public void A_credit_note_34_references_the_original_e_NCF(int daysLater, string indicator)
    {
        var doc = Document(EcfType.CreditNote34, MixedLines, rnc: "131245678", name: "Colmado El Sol SRL",
            payments: [new FiscalPayment(EcfPaymentForm.Cash, 828.50m)], modified: "E310000000007", code: ModificationCode.AmountCorrection);
        var xml = Valid(doc, modifiedOn: BusinessDay.Of(Now).AddDays(-daysLater));

        Value(xml, "IndicadorNotaCredito").ShouldBe(indicator);
        Value(xml, "NCFModificado").ShouldBe("E310000000007");
        Value(xml, "FechaNCFModificado").ShouldBe(BusinessDay.Of(Now).AddDays(-daysLater).ToString("dd-MM-yyyy", CultureInfo.InvariantCulture));
        Value(xml, "CodigoModificacion").ShouldBe("3");
        xml.Descendants("TablaFormasPago").ShouldBeEmpty(); // the 34 has no payment table
        xml.Descendants("FechaVencimientoSecuencia").ShouldBeEmpty();
    }

    [Fact]
    public void A_credit_note_reissued_past_30_days_after_the_invoice_changes_its_indicator()
    {
        // R12 #9: rejected 10 days after the invoice (indicator 0), replaced 25 days later: now 35 days after it, indicator 1.
        var doc = Document(EcfType.CreditNote34, MixedLines, rnc: "131245678", name: "Colmado El Sol SRL", modified: "E310000000007",
            code: ModificationCode.AmountCorrection);
        var rejected = Valid(doc, modifiedOn: BusinessDay.Of(Now).AddDays(-10));
        Value(rejected, "IndicadorNotaCredito").ShouldBe("0");

        var reissued = EcfXml.Reissue(rejected.ToString(), "E340000000002", Now.AddDays(25), null);
        Value(reissued, "IndicadorNotaCredito").ShouldBe("1");
        EcfXml.ValidateUnsigned(reissued, EcfType.CreditNote34).ShouldBeEmpty();
        EcfXml.Reissue(rejected.ToString(), "E340000000003", Now.AddDays(5), null).Descendants("IndicadorNotaCredito").Single().Value.ShouldBe("0");
    }

    [Fact]
    public void A_void_credit_note_34_to_an_anonymous_consumer_has_no_buyer_area()
    {
        var doc = Document(EcfType.CreditNote34, MixedLines, modified: "E320000000003", code: ModificationCode.Void);
        var xml = Valid(doc, modifiedOn: BusinessDay.Of(Now));
        xml.Descendants("Comprador").ShouldBeEmpty();
        Value(xml, "CodigoModificacion").ShouldBe("1");
    }

    [Fact]
    public void A_debit_note_33_is_valid()
    {
        var doc = Document(EcfType.DebitNote33, [Line("Cargo por entrega", 1, 150m, ItbisRate.General18)], rnc: "131245678", name: "Colmado El Sol SRL",
            payments: [new FiscalPayment(EcfPaymentForm.Cash, 150m)], modified: "E310000000007", code: ModificationCode.AmountCorrection);
        var xml = Valid(doc, Expires, BusinessDay.Of(Now));
        Value(xml, "FechaVencimientoSecuencia").ShouldBe("31-12-2028");
    }

    [Fact]
    public void A_31_without_buyer_fails_the_XSD()
    {
        var xml = EcfXml.Build(Document(EcfType.FiscalCredit31, MixedLines), ENcfFor(EcfType.FiscalCredit31), Issuer, Now, Expires, null);
        EcfXml.ValidateUnsigned(xml, EcfType.FiscalCredit31).ShouldNotBeEmpty();
    }

    [Fact]
    public void Text_is_escaped_and_cut_to_the_XSD_length()
    {
        var doc = Document(EcfType.Consumer32, [Line($"Queso \"<Geo>\" & {new string('x', 100)}", 1, 100m, ItbisRate.General18)]);
        var xml = Valid(doc);
        var name = Value(xml, "NombreItem")!;
        name.Length.ShouldBe(80);
        xml.ToString(SaveOptions.DisableFormatting).ShouldContain("&lt;Geo&gt;\" &amp;");
    }
}
