using System.Globalization;
using System.Text;
using Toki.Client.UI.Api;
using Toki.Escuadra;

namespace Toki.Client.UI;

/// <summary>TK-200: the Z close and the customer statement as a one-page document to save as PDF or send by WhatsApp.
/// Built here from what the screen already has and handed to the same download as the reports (escuadra.download: the browser saves it,
/// the store app asks where). It is an A4 HTML page: opened, "Imprimir o guardar como PDF" (or Ctrl+P) turns it into the PDF.</summary>
/// <remarks>Decision: no PDF library. QuestPDF's Community license is not MIT/Apache (free only under US$1M revenue, and it ships SkiaSharp natives);
/// PDFsharp needs a font resolver off Windows. Every host already prints HTML to PDF, so a print-ready page needs no new dependency.</remarks>
public static class Printables
{
    /// <summary>Logo C5 (the tag with the winking eye), as in Escuadra's Logo component.</summary>
    private const string Logo =
        """<svg width="44" height="44" viewBox="0 0 120 120" role="img" aria-label="Toki"><path d="M44 24 H96 Q104 24 104 32 V88 Q104 96 96 96 H44 L16 60 Z" fill="#0B5CAD" stroke="#0B5CAD" stroke-width="6" stroke-linejoin="round"/><circle cx="35" cy="60" r="8" fill="#FFFFFF"/><circle cx="32.5" cy="60" r="3.8" fill="#14181D"/><path d="M70 36 V76 Q70 84 78 84 H86 M58 50 H86" fill="none" stroke="#FFFFFF" stroke-width="10" stroke-linecap="round" stroke-linejoin="round"/></svg>""";

    private const string Style = """
        @page { size: A4; margin: 14mm; }
        * { box-sizing: border-box; }
        body { margin: 0; padding: 24px; background: #F1F3F5; color: #14181D; font: 13px/1.45 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
        main { max-width: 760px; margin: 0 auto; background: #FFFFFF; padding: 32px; border-radius: 8px; }
        header { display: flex; gap: 14px; align-items: center; border-bottom: 2px solid #0B5CAD; padding-bottom: 14px; }
        header .business { flex: 1; }
        header h1 { margin: 0; font-size: 18px; }
        header p, footer p { margin: 2px 0 0; color: #5B6470; }
        h2 { font-size: 16px; margin: 20px 0 4px; }
        .meta { color: #5B6470; margin: 0 0 14px; }
        table { width: 100%; border-collapse: collapse; margin: 10px 0; font-variant-numeric: tabular-nums; }
        th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid #DDE1E6; }
        th { font-size: 11px; text-transform: uppercase; letter-spacing: .04em; color: #5B6470; }
        .num { text-align: right; white-space: nowrap; }
        .total td { font-weight: 700; border-top: 2px solid #14181D; border-bottom: none; }
        .kpis { display: grid; grid-template-columns: repeat(3, 1fr); gap: 8px; margin: 12px 0; }
        .kpi { border: 1px solid #DDE1E6; border-radius: 6px; padding: 8px 10px; }
        .kpi span { display: block; font-size: 11px; color: #5B6470; text-transform: uppercase; letter-spacing: .04em; }
        .kpi strong { font-size: 17px; font-variant-numeric: tabular-nums; }
        .bad { color: #B42318; } .good { color: #067647; }
        footer { margin-top: 24px; font-size: 11px; }
        .print { display: block; margin: 0 auto 16px; padding: 10px 18px; border: 0; border-radius: 6px; background: #0B5CAD; color: #FFFFFF; font: inherit; font-weight: 600; cursor: pointer; }
        @media print { body { background: #FFFFFF; padding: 0; } main { padding: 0; max-width: none; } .print { display: none; } }
        @media (max-width: 600px) { body { padding: 8px; } main { padding: 16px; } .kpis { grid-template-columns: 1fr; } }
        """;

    /// <summary>What the printed Z shows: payment methods, customer payments, cash in and out, expected, counted and the difference, and who approved it.</summary>
    public static (string FileName, byte[] Content) Z(ZReport z, BusinessProfile business, string? supervisor, DateTime nowUtc)
    {
        var title = z.ZNumber is { } n ? $"Cierre de turno Z #{n}" : "Turno abierto (reporte X)";
        var b = new StringBuilder();
        b.Append($"<h2>{E(title)}</h2><p class=\"meta\">{E(z.Register)} · {E(z.Cashier)} · {E(Format.Date(z.OpenedUtc))} – {(z.ClosedUtc is { } c ? E(Format.Date(c)) : "sigue abierto")}</p>");
        b.Append("<div class=\"kpis\">");
        Kpi(b, "Efectivo esperado", Format.WithCurrency(z.ExpectedCash));
        Kpi(b, "Contado", z.CountedCash is { } counted ? Format.WithCurrency(counted) : "—");
        Kpi(b, "Diferencia", z.Difference is { } d ? (d == 0 ? "Cuadró" : $"{(d < 0 ? "Faltaron" : "Sobraron")} {Format.WithCurrency(Math.Abs(d))}") : "—",
            z.Difference is < 0 ? "bad" : z.Difference == 0 ? "good" : null);
        b.Append("</div>");
        b.Append("<table><thead><tr><th>Forma de pago</th><th class=\"num\">Cobros</th><th class=\"num\">Anulaciones</th><th class=\"num\">Reembolsos</th><th class=\"num\">Abonos</th><th class=\"num\">Neto</th></tr></thead><tbody>");
        foreach (var m in z.ByMethod)
            b.Append($"<tr><td>{E(Names.PaymentMethod(m.Method))}</td><td class=\"num\">{Format.Amount(m.SalePayments)}</td><td class=\"num\">{Format.Amount(m.Voids)}</td><td class=\"num\">{Format.Amount(m.Refunds)}</td><td class=\"num\">{Format.Amount(m.CustomerPayments)}</td><td class=\"num\"><strong>{Format.Amount(m.Net)}</strong></td></tr>");
        b.Append("</tbody></table>");
        b.Append("<table><tbody>");
        Row(b, "Fondo inicial", Format.Amount(z.OpeningFloat));
        Row(b, "Ventas", $"{z.SaleCount} ({z.VoidedCount} anuladas)");
        Row(b, "Abonos de fiado", Format.Amount(z.CustomerPayments));
        Row(b, "Abonos de pedidos", Format.Amount(z.OrderDeposits));
        Row(b, "Entradas de efectivo", Format.Amount(z.CashIn));
        Row(b, "Retiros", Format.Amount(z.Withdrawals));
        Row(b, "Gastos pagados de la caja", Format.Amount(z.Expenses));
        Row(b, "Redondeo", Format.Amount(z.Rounding));
        Row(b, "Autorizó el cierre", supervisor ?? "—");
        if (z.AuditChainHead is { } head) Row(b, "Auditoría", head);
        b.Append("</tbody></table>");
        var name = z.ZNumber is { } number ? $"cierre-z-{number}" : $"turno-x-{Format.StoreTime(z.OpenedUtc):yyyyMMdd-HHmm}";
        return ($"{name}.html", Page(title, business, b.ToString(), nowUtc));
    }

    /// <summary>The customer's credit account, oldest movement first, ending in the balance the screen shows.</summary>
    public static (string FileName, byte[] Content) Statement(CustomerStatement s, string? taxId, BusinessProfile business, DateTime nowUtc)
    {
        var b = new StringBuilder();
        b.Append($"<h2>Estado de cuenta · {E(s.Name)}</h2><p class=\"meta\">{(taxId is null ? "" : $"RNC o cédula {E(taxId)} · ")}Al {E(Format.Date(nowUtc))}</p>");
        b.Append("<div class=\"kpis\">");
        Kpi(b, "Debe", Format.WithCurrency(s.Debt), s.Debt > 0 ? "bad" : null);
        Kpi(b, "Límite de crédito", s.CreditLimit > 0 ? Format.WithCurrency(s.CreditLimit) : "Sin crédito");
        Kpi(b, s.CreditBalance > 0 ? "A favor" : "Disponible", Format.WithCurrency(s.CreditBalance > 0 ? s.CreditBalance : Math.Max(0, s.CreditLimit - s.Debt)));
        b.Append("</div>");
        b.Append("<table><thead><tr><th>Fecha</th><th>Movimiento</th><th>Documento</th><th class=\"num\">Cargo</th><th class=\"num\">Abono</th><th class=\"num\">Balance</th></tr></thead><tbody>");
        foreach (var l in s.Lines)
            b.Append($"<tr><td>{Format.ShortDate(l.DateUtc)}</td><td>{E(l.Type)}</td><td>{E(l.Document ?? "")}</td><td class=\"num\">{(l.Charge > 0 ? Format.Amount(l.Charge) : "")}</td><td class=\"num\">{(l.Payment > 0 ? Format.Amount(l.Payment) : "")}</td><td class=\"num\">{Format.Amount(l.Debt)}</td></tr>");
        if (s.Lines.Length == 0) b.Append("<tr><td colspan=\"6\">Sin movimientos.</td></tr>");
        b.Append($"<tr class=\"total\"><td colspan=\"5\">Balance pendiente</td><td class=\"num\">{Format.WithCurrency(s.Debt)}</td></tr>");
        b.Append("</tbody></table>");
        var slug = new string([.. s.Name.ToLowerInvariant().Normalize(NormalizationForm.FormD).Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')]).Trim('-');
        return ($"estado-de-cuenta-{slug}-{Format.StoreTime(nowUtc):yyyyMMdd}.html", Page($"Estado de cuenta · {s.Name}", business, b.ToString(), nowUtc));
    }

    private static byte[] Page(string title, BusinessProfile business, string body, DateTime nowUtc)
    {
        var contact = string.Join(" · ", new[] { business.Rnc is null ? null : $"RNC {business.Rnc}", business.Address, business.Phone }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var html = $"""
            <!doctype html>
            <html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{E(title)} · {E(business.Name)}</title>
            <style>{Style}</style></head>
            <body><button class="print" onclick="window.print()">Imprimir o guardar como PDF</button><main>
            <header>{Logo}<div class="business"><h1>{E(business.Name)}</h1><p>{E(contact)}</p></div></header>
            {body}
            <footer><p>Generado con Toki el {E(Format.Date(nowUtc))}.</p></footer>
            </main></body></html>
            """;
        return Encoding.UTF8.GetBytes(html);
    }

    private static void Kpi(StringBuilder b, string label, string value, string? css = null) =>
        b.Append($"<div class=\"kpi\"><span>{E(label)}</span><strong{(css is null ? "" : $" class=\"{css}\"")}>{E(value)}</strong></div>");

    private static void Row(StringBuilder b, string label, string value) => b.Append($"<tr><td>{E(label)}</td><td class=\"num\">{E(value)}</td></tr>");

    /// <summary>Escapes markup only; accents stay as UTF-8 text (WebUtility.HtmlEncode would turn "í" into an entity).</summary>
    private static string E(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);
}
