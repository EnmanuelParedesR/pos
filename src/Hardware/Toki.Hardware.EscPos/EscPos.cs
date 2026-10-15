using System.Globalization;
using System.Text;

namespace Toki.Hardware.EscPos;

/// <summary>The receipt laid out for a thermal printer (32 columns on 58 mm paper, 48 on 80 mm) and turned into ESC/POS bytes.
/// <see cref="Text"/> is the same layout as plain text (the simulated printer, and a readable snapshot).</summary>
public static class EscPos
{
    private const byte Esc = 0x1B, Gs = 0x1D, Lf = 0x0A;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    /// <summary>PC850 (Multilingual Latin I): ñ, á, é, í, ó, ú, ü, ¿, ¡ and their capitals. Selected with ESC t 2, which nearly every ESC/POS printer has.</summary>
    private static readonly Encoding Pc850 = CodePagesEncodingProvider.Instance.GetEncoding(850)!;

    /// <summary>ESC p 0 25 250: pulse on drawer pin 2 for 50 ms, then 500 ms off.</summary>
    public static readonly byte[] DrawerKick = [Esc, (byte)'p', 0, 25, 250];

    internal sealed record Line(string Text, bool Center = false, bool Bold = false, bool Tall = false, string? Qr = null);

    public static byte[] Render(Receipt r, int columns, bool openDrawer)
    {
        var o = new List<byte>(2048) { Esc, (byte)'@', Esc, (byte)'t', 2 }; // initialize; code table PC850
        foreach (var l in Layout(r, columns))
        {
            o.AddRange([Esc, (byte)'a', l.Center || l.Qr is not null ? (byte)1 : (byte)0]);
            if (l.Qr is { } data)
            {
                o.AddRange(Qr(data, columns == 32 ? (byte)5 : (byte)6));
                continue;
            }
            if (l.Bold) o.AddRange([Esc, (byte)'E', 1]);
            if (l.Tall) o.AddRange([Gs, (byte)'!', 0x01]); // double height, same width: the columns still fit
            o.AddRange(Pc850.GetBytes(Printable(l.Text)));
            o.Add(Lf);
            if (l.Tall) o.AddRange([Gs, (byte)'!', 0]);
            if (l.Bold) o.AddRange([Esc, (byte)'E', 0]);
        }
        o.AddRange([Esc, (byte)'a', 0, Gs, (byte)'V', 66, 3]); // feed past the cutter and cut (partial)
        if (openDrawer) o.AddRange(DrawerKick);
        return [.. o];
    }

    public static string Text(Receipt r, int columns)
    {
        var s = new StringBuilder();
        foreach (var l in Layout(r, columns))
        {
            var text = l.Qr is not null ? "[QR] " + l.Qr : l.Text;
            s.Append(l.Center || l.Qr is not null ? Center(text, columns) : text).Append('\n');
        }
        return s.ToString();
    }

    /// <summary>GS ( k, model 2: select the model, module size, error correction M, store the data, print it.</summary>
    internal static byte[] Qr(string data, byte moduleSize)
    {
        var bytes = Encoding.ASCII.GetBytes(data);
        var stored = bytes.Length + 3;
        return
        [
            Gs, (byte)'(', (byte)'k', 4, 0, 49, 65, 50, 0,
            Gs, (byte)'(', (byte)'k', 3, 0, 49, 67, moduleSize,
            Gs, (byte)'(', (byte)'k', 3, 0, 49, 69, 49,
            Gs, (byte)'(', (byte)'k', (byte)(stored % 256), (byte)(stored / 256), 49, 80, 48, .. bytes,
            Gs, (byte)'(', (byte)'k', 3, 0, 49, 81, 48,
            Lf,
        ];
    }

    internal static IEnumerable<Line> Layout(Receipt r, int w)
    {
        var rule = new Line(new string('-', w));
        foreach (var t in Wrap(r.BusinessName, w)) yield return new(t, Center: true, Bold: true);
        if (r.Rnc is not null) yield return new($"RNC {r.Rnc}", Center: true);
        if (r.Address is not null) foreach (var t in Wrap(r.Address, w)) yield return new(t, Center: true);
        if (r.Phone is not null) yield return new($"Tel. {r.Phone}", Center: true);
        yield return rule;
        foreach (var t in Wrap(r.DocumentType, w)) yield return new(t, Center: true, Bold: true);
        if (r.IsVoided) yield return new("*** ANULADA ***", Center: true, Bold: true);
        foreach (var t in Pair("e-NCF", r.ENcf, w)) yield return new(t);
        foreach (var t in Pair("Factura", $"#{r.Number}", w)) yield return new(t);
        foreach (var t in Pair("Fecha", r.LocalDateTime.ToString("dd/MM/yyyy h:mm tt", Invariant), w)) yield return new(t);
        if (r.SequenceExpiresOn is { } expires) foreach (var t in Pair("Válido hasta", expires.ToString("dd/MM/yyyy", Invariant), w)) yield return new(t);
        foreach (var t in Pair(r.Register, r.Cashier, w)) yield return new(t);
        if (r.Customer is not null)
        {
            foreach (var t in Pair("Cliente", r.Customer, w)) yield return new(t);
            if (r.CustomerRnc is not null) foreach (var t in Pair("RNC", r.CustomerRnc, w)) yield return new(t);
        }
        yield return rule;
        foreach (var l in r.Lines)
        {
            foreach (var t in Wrap(l.Description, w)) yield return new(t);
            foreach (var t in Pair($"  {Quantity(l.Quantity, l.Unit)} x {Amount(l.Price)}", Amount(l.Total), w)) yield return new(t);
            if (l.Discount > 0) foreach (var t in Pair("  Descuento", "-" + Amount(l.Discount), w)) yield return new(t);
        }
        yield return rule;
        // Same order as the screen: the discount first, so the base and the ITBIS add up to the total.
        if (r.Discount > 0) foreach (var t in Pair("Descuento aplicado", "-" + Amount(r.Discount), w)) yield return new(t);
        foreach (var t in Pair("Base sin ITBIS", Amount(r.Subtotal), w)) yield return new(t);
        foreach (var i in r.Itbis) foreach (var t in Pair(i.Rate, Amount(i.Itbis), w)) yield return new(t);
        foreach (var t in Pair("TOTAL", "RD$ " + Amount(r.Total), w)) yield return new(t, Bold: true, Tall: true);
        yield return rule;
        foreach (var p in r.Payments) foreach (var t in Pair(p.Method, Amount(p.Received ?? p.Amount), w)) yield return new(t);
        if (r.Change > 0) foreach (var t in Pair("Devuelta", Amount(r.Change), w)) yield return new(t, Bold: true);
        yield return rule;
        foreach (var t in Wrap($"Código de seguridad: {r.SecurityCode}", w)) yield return new(t, Center: true);
        if (r.SignedAt is { } signed) foreach (var t in Wrap($"Firmada: {signed.ToString("dd/MM/yyyy h:mm:ss tt", Invariant)}", w)) yield return new(t, Center: true);
        if (r.ContingencyLegend is not null) foreach (var t in Wrap(r.ContingencyLegend, w)) yield return new(t, Center: true, Bold: true);
        if (!string.IsNullOrEmpty(r.QrUrl)) yield return new("", Qr: r.QrUrl);
        if (r.Footer is not null) foreach (var t in Wrap(r.Footer, w)) yield return new(t, Center: true);
    }

    private static string Amount(decimal v) => v.ToString("#,##0.00", Invariant);

    private static string Quantity(decimal v, string unit) => unit == "Pound" ? v.ToString("0.000", Invariant) + " lb" : v.ToString("0.###", Invariant);

    /// <summary>Label on the left, value on the right; a label too long for the line goes alone above.</summary>
    private static IEnumerable<string> Pair(string left, string right, int w)
    {
        if (left.Length + 1 + right.Length <= w) return [left + right.PadLeft(w - left.Length)];
        return [.. Wrap(left, w), right.Length >= w ? right[..w] : right.PadLeft(w)];
    }

    private static IEnumerable<string> Wrap(string text, int w)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            for (var rest = word; rest.Length > 0;)
            {
                if (line.Length > 0 && line.Length + 1 + rest.Length > w)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                var take = Math.Min(rest.Length, w - (line.Length > 0 ? line.Length + 1 : 0));
                if (line.Length > 0) line.Append(' ');
                line.Append(rest[..take]);
                rest = rest[take..];
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private static string Center(string text, int w) => text.Length >= w ? text : new string(' ', (w - text.Length) / 2) + text;

    /// <summary>Characters PC850 lacks, written the way a receipt would.</summary>
    private static string Printable(string text) => text.Replace('−', '-').Replace('–', '-').Replace('—', '-').Replace('“', '"').Replace('”', '"')
        .Replace('‘', '\'').Replace('’', '\'').Replace("…", "...", StringComparison.Ordinal);
}
