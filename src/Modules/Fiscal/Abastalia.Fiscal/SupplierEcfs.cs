using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Abastalia.Fiscal.Contracts;

namespace Abastalia.Fiscal;

/// <summary>TK-240: a supplier's e-CF read for a purchase. The XML is the supplier's signed document, so its numbers win over any reading by eye or AI.
/// Unit costs go without ITBIS: an e-CF with <c>IndicadorMontoGravado</c> 1 carries it inside the price, and it is taken out.</summary>
internal sealed class SupplierEcfs(FiscalDbContext db) : ISupplierEcfs
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public async Task<SupplierEcf?> ReceivedAsync(Guid id, CancellationToken ct) =>
        Of(await db.Set<ReceivedDocument>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct));

    public async Task<SupplierEcf?> ReceivedAsync(string issuerRnc, string encf, CancellationToken ct) =>
        Of(await db.Set<ReceivedDocument>().AsNoTracking().Where(d => d.IssuerRnc == issuerRnc && d.ENcf == encf && d.Environment == "ecf")
            .OrderByDescending(d => d.CloudId).FirstOrDefaultAsync(ct));

    private SupplierEcf? Of(ReceivedDocument? d)
    {
        if (d is null) return null;
        // The cloud already checked the signature and took these fields out; the XML only adds the lines.
        var read = Read(EcfSubmissionQueue.Unzip(d.XmlGzip));
        return new SupplierEcf(d.IssuerRnc, d.IssuerName, d.ENcf, d.Type, d.IssuedOn, d.Total - d.Itbis, d.Itbis, d.Total, read?.Lines ?? [], d.Environment);
    }

    public SupplierEcf? Read(string xml)
    {
        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
            root = XDocument.Load(reader).Root!;
        }
        catch (XmlException) { return null; }
        if (root.Name.LocalName != "ECF" || El(root, "Encabezado") is not { } header) return null;
        var id = El(header, "IdDoc");
        var issuer = El(header, "Emisor");
        var totals = El(header, "Totales");
        if (!int.TryParse(Text(id, "TipoeCF"), Invariant, out var type) || Text(id, "eNCF") is not { } encf || Text(issuer, "RNCEmisor") is not { } rnc
            || !DateOnly.TryParseExact(Text(issuer, "FechaEmision"), "dd-MM-yyyy", Invariant, DateTimeStyles.None, out var issuedOn)
            || Amount(totals, "MontoTotal") is not { } total)
            return null;
        var itbis = Amount(totals, "TotalITBIS") ?? 0;
        var withItbis = Text(id, "IndicadorMontoGravado") == "1";
        var lines = (El(root, "DetallesItems")?.Elements().Where(e => e.Name.LocalName == "Item") ?? []).Select(i =>
        {
            var quantity = Amount(i, "CantidadItem") is decimal q and > 0 ? q : 1;
            var percent = Text(i, "IndicadorFacturacion") switch { "1" => 18m, "2" => 16m, "3" => 0m, _ => (decimal?)null };
            var amount = Amount(i, "MontoItem") ?? (Amount(i, "PrecioUnitarioItem") ?? 0) * quantity - (Amount(i, "DescuentoMonto") ?? 0);
            if (withItbis && percent is > 0) amount /= 1 + percent.Value / 100;
            var code = El(i, "TablaCodigosItem")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "CodigoItem")?.Value.Trim();
            return new SupplierEcfLine(Text(i, "NombreItem") ?? "", quantity, Math.Round(amount / quantity, 4, MidpointRounding.AwayFromZero), percent,
                string.IsNullOrEmpty(code) ? null : code);
        }).ToList();
        return new SupplierEcf(rnc, Text(issuer, "RazonSocialEmisor") ?? "", encf, type, issuedOn, total - itbis, itbis, total, lines, null);
    }

    private static XElement? El(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static string? Text(XElement? parent, string name) => El(parent, name)?.Value.Trim() is { Length: > 0 } v ? v : null;

    private static decimal? Amount(XElement? parent, string name) =>
        decimal.TryParse(Text(parent, name), NumberStyles.Number, Invariant, out var v) ? v : null;
}
