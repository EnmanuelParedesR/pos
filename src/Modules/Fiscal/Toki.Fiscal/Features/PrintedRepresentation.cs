using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Security;
using Toki.Fiscal.Contracts;
using Toki.Fiscal.Domain;

namespace Toki.Fiscal.Features;

/// <summary>TK-148/TK-240: the DGII "Representación Impresa" of an e-CF as an A4 page (docs/ecf-propio.md §5): issuer top left, type in words, e-NCF and
/// sequence expiry top right, the buyer, the items and totals, the modified e-NCF and its code in words on a note, and the QR (at least 22 mm) bottom
/// left with the security code and the signing date and time under it; the contingency legend when it applies. Everything comes from the signed XML,
/// so the page always says what the DGII has. It opens in any browser and "Imprimir o guardar como PDF" makes the PDF the certification (steps 5–6) asks for.</summary>
/// <remarks>Decision: no PDF library, as in the client's Printables (QuestPDF's license, PDFsharp's fonts). shortcut: the QR is the version its URL needs
/// (the full consultatimbre URL does not fit the version 8 the DGII guide mentions), printed at 30 mm.</remarks>
internal static class PrintedRepresentation
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-DO");

    private const string Style = """
        @page { size: A4; margin: 14mm; }
        * { box-sizing: border-box; }
        body { margin: 0; padding: 24px; background: #F1F3F5; color: #14181D; font: 12px/1.4 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
        main { max-width: 780px; margin: 0 auto; background: #FFFFFF; padding: 28px; border-radius: 8px; }
        header { display: flex; justify-content: space-between; gap: 24px; }
        h1 { margin: 0 0 4px; font-size: 16px; }
        p { margin: 2px 0; }
        .doc { text-align: right; }
        .doc strong { display: block; font-size: 14px; }
        .muted { color: #5B6470; }
        .box { border: 1px solid #DDE1E6; border-radius: 6px; padding: 8px 10px; margin: 14px 0; }
        table { width: 100%; border-collapse: collapse; margin: 10px 0; font-variant-numeric: tabular-nums; }
        th, td { text-align: left; padding: 5px 6px; border-bottom: 1px solid #DDE1E6; vertical-align: top; }
        th { font-size: 10px; text-transform: uppercase; letter-spacing: .04em; color: #5B6470; }
        .num { text-align: right; white-space: nowrap; }
        .totals { width: auto; margin-left: auto; min-width: 260px; }
        .totals .grand td { font-weight: 700; border-top: 2px solid #14181D; border-bottom: none; }
        .seal { display: flex; gap: 14px; align-items: flex-end; margin-top: 20px; }
        .seal svg { width: 30mm; height: 30mm; display: block; }
        .warn { border-color: #B54708; color: #B54708; font-weight: 600; }
        .print { display: block; margin: 0 auto 16px; padding: 10px 18px; border: 0; border-radius: 6px; background: #0B5CAD; color: #FFFFFF; font: inherit; font-weight: 600; cursor: pointer; }
        footer { margin-top: 20px; font-size: 10px; color: #5B6470; }
        @media print { body { background: #FFFFFF; padding: 0; } main { padding: 0; max-width: none; } .print { display: none; } }
        @media (max-width: 600px) { body { padding: 8px; } main { padding: 14px; } header { flex-direction: column; } .doc { text-align: left; } }
        """;

    public static (string FileName, byte[] Content) Build(FiscalDocument d)
    {
        var root = XDocument.Parse(EcfSubmissionQueue.Unzip(d.SignedXml)).Root!;
        var header = root.Element("Encabezado")!;
        var id = header.Element("IdDoc")!;
        var issuer = header.Element("Emisor")!;
        var buyer = header.Element("Comprador");
        var totals = header.Element("Totales")!;
        string? V(XElement? e, string name) => e?.Element(name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var issuerRnc = V(issuer, "RNCEmisor") ?? "";
        var signedLocal = V(root, "FechaHoraFirma") ?? (d.SignedUtc + Toki.SharedKernel.BusinessDay.Offset).ToString("dd-MM-yyyy HH:mm:ss", Invariant);
        var qr = EcfQr.Url(d.Environment, d.Channel, issuerRnc, d.BuyerRnc, d.ENcf, d.Total, d.SignedUtc, d.SecurityCode);

        var b = new StringBuilder();
        if (d.Environment == FiscalEnvironment.Simulated)
            b.Append("<p class=\"box warn\">Comprobante simulado: no se envió a la DGII y no tiene valor fiscal.</p>");
        else if (d.Status is SubmissionStatus.Rejected or SubmissionStatus.Voided)
            b.Append($"<p class=\"box warn\">{(d.Status == SubmissionStatus.Rejected ? "Rechazado por la DGII" : "Anulado (ANECF)")}: esta representación impresa no tiene validez fiscal.</p>");

        b.Append("<header><div>");
        b.Append($"<h1>{E(V(issuer, "NombreComercial") ?? V(issuer, "RazonSocialEmisor") ?? "")}</h1>");
        if (V(issuer, "NombreComercial") is not null) b.Append($"<p>{E(V(issuer, "RazonSocialEmisor") ?? "")}</p>");
        b.Append($"<p>RNC {E(issuerRnc)}</p>");
        var place = string.Join(", ", new[] { V(issuer, "DireccionEmisor"), V(issuer, "Municipio"), V(issuer, "Provincia") }.Where(x => x is not null));
        if (place.Length > 0) b.Append($"<p class=\"muted\">{E(place)}</p>");
        b.Append($"<p>Fecha de emisión: {E(Day(V(issuer, "FechaEmision")))}</p>");
        b.Append("</div><div class=\"doc\">");
        b.Append($"<strong>{E(FiscalErrors.Name(d.Type))}</strong><p>e-NCF: <b>{E(d.ENcf)}</b></p>");
        if (V(id, "FechaVencimientoSecuencia") is { } expires) b.Append($"<p>Válida hasta: {E(Day(expires))}</p>");
        b.Append("</div></header>");

        var buyerRnc = V(buyer, "RNCComprador");
        var buyerName = V(buyer, "RazonSocialComprador");
        if (buyerRnc is not null || buyerName is not null)
            b.Append($"<div class=\"box\"><p><b>Cliente:</b> {E(buyerName ?? "")}</p>{(buyerRnc is null ? "" : $"<p>RNC o cédula: {E(buyerRnc)}</p>")}</div>");

        if (root.Element("InformacionReferencia") is { } reference)
            b.Append($"<div class=\"box\"><p><b>e-NCF modificado:</b> {E(V(reference, "NCFModificado") ?? "")}"
                + (V(reference, "FechaNCFModificado") is { } on ? $" del {E(Day(on))}" : "") + "</p>"
                + $"<p><b>Código de modificación:</b> {E(Modification(V(reference, "CodigoModificacion")))}</p></div>");

        b.Append("<table><thead><tr><th class=\"num\">Cantidad</th><th>Descripción</th><th class=\"num\">Precio</th><th>ITBIS</th><th class=\"num\">Descuento</th><th class=\"num\">Valor</th></tr></thead><tbody>");
        foreach (var item in root.Element("DetallesItems")?.Elements("Item") ?? [])
            b.Append($"<tr><td class=\"num\">{E(Number(V(item, "CantidadItem")))}</td><td>{E(V(item, "NombreItem") ?? "")}</td>"
                + $"<td class=\"num\">{E(Money(V(item, "PrecioUnitarioItem")))}</td><td>{E(Indicator(V(item, "IndicadorFacturacion")))}</td>"
                + $"<td class=\"num\">{E(V(item, "DescuentoMonto") is { } off ? Money(off) : "")}</td><td class=\"num\">{E(Money(V(item, "MontoItem")))}</td></tr>");
        b.Append("</tbody></table>");

        b.Append("<table class=\"totals\"><tbody>");
        void Total(string label, string name, string css = "")
        {
            if (V(totals, name) is { } v) b.Append($"<tr{css}><td>{E(label)}</td><td class=\"num\">{E(Money(v))}</td></tr>");
        }
        Total("Monto gravado", "MontoGravadoTotal");
        Total("Monto exento", "MontoExento");
        Total("ITBIS 18 %", "TotalITBIS1");
        Total("ITBIS 16 %", "TotalITBIS2");
        Total("Total ITBIS", "TotalITBIS");
        Total("Total", "MontoTotal", " class=\"grand\"");
        b.Append("</tbody></table>");

        using var data = QRCodeGenerator.GenerateQrCode(qr, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(4, "#000000", "#FFFFFF", true, SvgQRCode.SizingMode.ViewBoxAttribute);
        b.Append($"<div class=\"seal\"><div>{svg}</div><div><p>Código de seguridad: <b>{E(d.SecurityCode)}</b></p>"
            + $"<p>Fecha de firma digital: {E(signedLocal)}</p></div></div>");
        if (d.Contingency) b.Append($"<p class=\"box\">{E(EcfReceiptTexts.ContingencyLegend)}</p>");

        var title = $"{FiscalErrors.Name(d.Type)} {d.ENcf}";
        var html = $"""
            <!doctype html>
            <html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{E(title)}</title>
            <style>{Style}</style></head>
            <body><button class="print" onclick="window.print()">Imprimir o guardar como PDF</button><main>
            {b}
            <footer>Representación impresa de comprobante fiscal electrónico. Generada con Toki.</footer>
            </main></body></html>
            """;
        return ($"{issuerRnc}{d.ENcf}.html", Encoding.UTF8.GetBytes(html));
    }

    private static string Day(string? ddMMyyyy) =>
        DateOnly.TryParseExact(ddMMyyyy, "dd-MM-yyyy", Invariant, DateTimeStyles.None, out var d) ? d.ToString("dd/MM/yyyy", Invariant) : ddMMyyyy ?? "";

    private static string Money(string? v) => decimal.TryParse(v, NumberStyles.Number, Invariant, out var n) ? n.ToString("N2", Es) : v ?? "";

    private static string Number(string? v) => decimal.TryParse(v, NumberStyles.Number, Invariant, out var n) ? n.ToString("0.##", Es) : v ?? "";

    private static string Indicator(string? v) => v switch { "1" => "18 %", "2" => "16 %", "3" => "0 %", "4" => "Exento", _ => "" };

    /// <summary>The DGII's table of modification codes, in words.</summary>
    private static string Modification(string? code) => code switch
    {
        "1" => "1 · Anula el NCF modificado",
        "2" => "2 · Corrige texto del comprobante fiscal modificado",
        "3" => "3 · Corrige montos del NCF modificado",
        "4" => "4 · Reemplazo de NCF emitido en contingencia",
        "5" => "5 · Referencia factura de consumo electrónica",
        _ => code ?? "",
    };

    /// <summary>Escapes markup only; accents stay as UTF-8 text.</summary>
    private static string E(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/documents/{id:guid}/printed", async (Guid id, FiscalDbContext db, CancellationToken ct) =>
                {
                    var d = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
                    if (d is null) return EndpointExtensions.Problem(FiscalErrors.NotFound);
                    var (name, content) = Build(d);
                    return Results.File(content, "text/html; charset=utf-8", name);
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}
