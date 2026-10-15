using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Toki.Fiscal.Contracts;
using Toki.Settings.Contracts;
using Toki.SharedKernel;

namespace Toki.Fiscal;

/// <summary>The e-CF XML (types 31 to 34) per the DGII "Formato e-CF v1.0" and its official XSD (in <c>Schemas/</c>).
/// Element names and order are the DGII's. Optional fields without a value are left out: empty tags cause rejections.</summary>
internal static class EcfXml
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly XName SignaturePlaceholder = XName.Get("Signature", "http://www.w3.org/2000/09/xmldsig#");

    /// <param name="nowUtc">Issue and signing moment: <c>FechaEmision</c> and <c>FechaHoraFirma</c> in DR time (GMT-4).</param>
    /// <param name="sequenceExpiresOn">Expiry of the e-NCF range; only 31 and 33 carry it.</param>
    /// <param name="modifiedIssuedOn">Issue date of the e-NCF a note modifies.</param>
    public static XDocument Build(DocumentToIssue doc, string encf, BusinessProfile issuer, DateTime nowUtc, DateOnly? sequenceExpiresOn, DateOnly? modifiedIssuedOn)
    {
        var local = nowUtc + BusinessDay.Offset;
        var type = doc.Type;
        var isNote = type is EcfType.DebitNote33 or EcfType.CreditNote34;
        var taxed = doc.Lines.Any(l => l.Rate != ItbisRate.Exempt);

        return new XDocument(new XElement("ECF",
            new XElement("Encabezado",
                new XElement("Version", "1.0"),
                new XElement("IdDoc",
                    new XElement("TipoeCF", (int)type),
                    new XElement("eNCF", encf),
                    type is EcfType.FiscalCredit31 or EcfType.DebitNote33 ? new XElement("FechaVencimientoSecuencia", Date(sequenceExpiresOn!.Value)) : null,
                    // 1 = the note is more than 30 days after the modified e-CF: it cannot reduce ITBIS.
                    type == EcfType.CreditNote34 ? new XElement("IndicadorNotaCredito", DateOnly.FromDateTime(local).DayNumber - modifiedIssuedOn!.Value.DayNumber > 30 ? 1 : 0) : null,
                    taxed ? new XElement("IndicadorMontoGravado", 1) : null, // Toki prices include ITBIS
                    new XElement("TipoIngresos", "01"),                         // operating income
                    new XElement("TipoPago", (int)doc.PaymentType),
                    // shortcut: no FechaLimitePago for credit sales; customers have no payment terms yet.
                    type != EcfType.CreditNote34 && doc.Payments is { Count: > 0 } payments
                        ? new XElement("TablaFormasPago", payments.GroupBy(p => p.Form).OrderBy(g => g.Key).Select(g => new XElement("FormaDePago",
                            new XElement("FormaPago", (int)g.Key),
                            new XElement("MontoPago", Amount(g.Sum(p => p.Amount))))))
                        : null),
                new XElement("Emisor",
                    new XElement("RNCEmisor", issuer.Rnc),
                    new XElement("RazonSocialEmisor", Text(issuer.Name, 150)),
                    Optional("DireccionEmisor", Text(issuer.Address, 100)),
                    new XElement("FechaEmision", Date(DateOnly.FromDateTime(local)))),
                Buyer(doc),
                Totals(doc)),
            new XElement("DetallesItems", doc.Lines.Select((l, i) => Item(l, i + 1))),
            isNote
                ? new XElement("InformacionReferencia",
                    new XElement("NCFModificado", doc.ModifiedENcf),
                    modifiedIssuedOn is { } modified ? new XElement("FechaNCFModificado", Date(modified)) : null,
                    new XElement("CodigoModificacion", (int)doc.ModificationCode!.Value))
                : null,
            new XElement("FechaHoraFirma", local.ToString("dd-MM-yyyy HH:mm:ss", Invariant))));
    }

    /// <summary>XSD errors of a signed e-CF (empty when valid).</summary>
    public static IReadOnlyList<string> Validate(XDocument xml, EcfType type)
    {
        var errors = new List<string>();
        var schemas = Schemas[type].Value;
        // shortcut: XmlSchemaSet is not documented as thread-safe for concurrent validation; one lock is plenty for a store.
        lock (schemas) xml.Validate(schemas, (_, e) => errors.Add(e.Message));
        return errors;
    }

    /// <summary>XSD errors before signing. The XSD requires the signature as the last element (<c>xs:any</c>, minOccurs 1),
    /// so a placeholder takes its place in a copy.</summary>
    public static IReadOnlyList<string> ValidateUnsigned(XDocument xml, EcfType type)
    {
        var copy = new XDocument(xml);
        copy.Root!.Add(new XElement(SignaturePlaceholder));
        return Validate(copy, type);
    }

    private static readonly Dictionary<EcfType, Lazy<XmlSchemaSet>> Schemas = Enum.GetValues<EcfType>()
        .ToDictionary(t => t, t => new Lazy<XmlSchemaSet>(() => LoadSchema(t)));

    // Each XSD declares the same global types, so each type gets its own set.
    private static XmlSchemaSet LoadSchema(EcfType type)
    {
        using var stream = typeof(EcfXml).Assembly.GetManifestResourceStream($"Toki.Fiscal.Schemas.e-CF {(int)type} v.1.0.xsd")
            ?? throw new InvalidOperationException($"Missing embedded XSD for e-CF {(int)type}.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var set = new XmlSchemaSet { XmlResolver = null };
        set.Add(XmlSchema.Read(reader, null)!);
        set.Compile();
        return set;
    }

    private static XElement? Buyer(DocumentToIssue doc)
    {
        var buyer = new XElement("Comprador",
            Optional("RNCComprador", doc.BuyerRnc),
            Optional("RazonSocialComprador", Text(doc.BuyerName, 150)));
        // 31 and 32 require the Comprador area. A 32 to an anonymous consumer leaves it empty: the only empty tag, and that
        // e-CF never travels whole (under RD$250,000 it goes as RFCE, TK-145; from that amount on the buyer is required).
        return buyer.HasElements || doc.Type is EcfType.FiscalCredit31 or EcfType.Consumer32 ? buyer : null;
    }

    private static XElement Totals(DocumentToIssue doc)
    {
        // Per DGII rate: 1 = 18 %, 2 = 16 %, 3 = 0 %. Taxed amounts are without ITBIS (prices include it, IndicadorMontoGravado = 1).
        // The sale's own ITBIS is used, not taxed × rate: the e-CF must match the sale to the cent, and the DGII tolerance covers the difference.
        var byRate = doc.Lines.GroupBy(l => l.Rate).ToDictionary(g => g.Key, g => (Amount: g.Sum(l => l.Total), Itbis: g.Sum(l => l.Itbis)));
        (decimal Amount, decimal Itbis)? Rate(ItbisRate r) => byRate.TryGetValue(r, out var v) ? v : null;
        var r1 = Rate(ItbisRate.General18);
        var r2 = Rate(ItbisRate.Reduced16);
        var r3 = Rate(ItbisRate.Zero);
        var exempt = Rate(ItbisRate.Exempt);
        var taxed = new[] { r1, r2, r3 }.Where(r => r is not null).Select(r => r!.Value).ToList();

        return new XElement("Totales",
            taxed.Count > 0 ? new XElement("MontoGravadoTotal", Amount(taxed.Sum(r => r.Amount - r.Itbis))) : null,
            r1 is { } a1 ? new XElement("MontoGravadoI1", Amount(a1.Amount - a1.Itbis)) : null,
            r2 is { } a2 ? new XElement("MontoGravadoI2", Amount(a2.Amount - a2.Itbis)) : null,
            r3 is { } a3 ? new XElement("MontoGravadoI3", Amount(a3.Amount - a3.Itbis)) : null,
            exempt is { } ex ? new XElement("MontoExento", Amount(ex.Amount)) : null,
            r1 is not null ? new XElement("ITBIS1", 18) : null,
            r2 is not null ? new XElement("ITBIS2", 16) : null,
            r3 is not null ? new XElement("ITBIS3", 0) : null,
            taxed.Count > 0 ? new XElement("TotalITBIS", Amount(taxed.Sum(r => r.Itbis))) : null,
            r1 is { } i1 ? new XElement("TotalITBIS1", Amount(i1.Itbis)) : null,
            r2 is { } i2 ? new XElement("TotalITBIS2", Amount(i2.Itbis)) : null,
            r3 is { } i3 ? new XElement("TotalITBIS3", Amount(i3.Itbis)) : null,
            new XElement("MontoTotal", Amount(doc.Total)));
    }

    private static XElement Item(FiscalLine l, int number)
    {
        // The XSD allows 2 decimals in CantidadItem and the DGII checks Price × Quantity − Discount = MontoItem (±1).
        // shortcut: a weight with 3 decimals is rounded to 2 and the unit price (4 decimals allowed) is recomputed so the line still adds up;
        // the receipt keeps the exact weight. Ask the DGII whether that is the expected practice for goods sold by weight.
        var quantity = Math.Max(0.01m, Math.Round(l.Quantity, 2, MidpointRounding.AwayFromZero));
        var gross = l.Total + l.Discount;
        var price = quantity == l.Quantity && Money.Round(l.UnitPrice * quantity) == gross
            ? Math.Round(l.UnitPrice, 4, MidpointRounding.AwayFromZero)
            : Math.Round(gross / quantity, 4, MidpointRounding.AwayFromZero);
        return new XElement("Item",
            new XElement("NumeroLinea", number),
            new XElement("IndicadorFacturacion", BillingIndicator(l.Rate)),
            new XElement("NombreItem", Text(l.Description, 80)),
            new XElement("IndicadorBienoServicio", 1), // goods
            new XElement("CantidadItem", quantity.ToString("0.00", Invariant)),
            new XElement("PrecioUnitarioItem", price.ToString("0.00##", Invariant)),
            l.Discount > 0 ? new XElement("DescuentoMonto", Amount(l.Discount)) : null,
            l.Discount > 0
                ? new XElement("TablaSubDescuento", new XElement("SubDescuento",
                    new XElement("TipoSubDescuento", "$"),
                    new XElement("MontoSubDescuento", Amount(l.Discount))))
                : null,
            new XElement("MontoItem", Amount(l.Total)));
    }

    // DGII billing indicator: 1 = 18 %, 2 = 16 %, 3 = 0 %, 4 = exempt.
    private static int BillingIndicator(ItbisRate r) => r switch { ItbisRate.General18 => 1, ItbisRate.Reduced16 => 2, ItbisRate.Zero => 3, _ => 4 };

    private static string Amount(decimal v) => Money.Round(v).ToString("0.00", Invariant);

    private static string Date(DateOnly d) => d.ToString("dd-MM-yyyy", Invariant);

    private static string? Text(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Length > max ? t[..max] : t;
    }

    private static XElement? Optional(string name, string? value) => string.IsNullOrWhiteSpace(value) ? null : new XElement(name, value);
}
