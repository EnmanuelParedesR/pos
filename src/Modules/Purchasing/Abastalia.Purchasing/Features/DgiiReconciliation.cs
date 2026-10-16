using System.Globalization;
using System.Text;
using ExcelDataReader;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Fiscal.Contracts;
using Abastalia.Purchasing.Domain;
using Abastalia.SharedKernel;

namespace Abastalia.Purchasing.Features;

/// <summary>TK-223: the Excel of received NCF / e-CF the owner downloads from the DGII Oficina Virtual. Columns are found by their header names,
/// so their order and extra columns do not matter (assumed layout: docs/ecf-propio.md §11 #41). TK-240: as .xlsx, the old .xls or .csv (comma,
/// semicolon or tab; UTF-8 or Windows-1252), told apart by their first bytes, all read with ExcelDataReader (MIT).</summary>
internal static class DgiiReceivedFile
{
    public sealed record Row(int Number, string Rnc, string? Name, string Ncf, DateOnly? Date, decimal? Total, decimal? Itbis);

    public static readonly Error Unreadable = Error.Validation("Purchasing.DgiiFileUnreadable",
        "El archivo no parece el Excel de comprobantes recibidos de la DGII (.xlsx, .xls o .csv): necesita columnas con el RNC del emisor y el NCF. "
        + "Si la Oficina Virtual lo bajó en otro formato, ábralo en Excel y guárdelo como .xlsx.");

    /// <summary>A non-empty line of a sheet: its number in the file (1 = first) and its cells (string, double, DateTime or null).</summary>
    private sealed record Line(int Number, object?[] Cells)
    {
        public object? Cell(int column) => column <= Cells.Length ? Cells[column - 1] : null;

        public string Text(int column) => Cell(column) switch
        {
            null => "",
            double d => d.ToString(CultureInfo.InvariantCulture),
            DateTime t => t.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            var v => v.ToString()?.Trim() ?? "",
        };
    }

    static DgiiReceivedFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // .xls and Windows-1252 CSV

    public static Result<IReadOnlyList<Row>> Read(byte[] file)
    {
        try
        {
            foreach (var lines in Sheets(file))
            {
                // The OFV puts a title or the period above the header: look for it in the first rows.
                foreach (var header in lines.Take(15))
                {
                    var names = Enumerable.Range(1, header.Cells.Length).Select(c => Normalize(header.Text(c))).ToList();
                    if (Columns.Find(names) is not { } cols) continue;
                    return lines.Where(r => r.Number > header.Number).Select(cols.Read).OfType<Row>().ToList();
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        return Unreadable;
    }

    private static List<List<Line>> Sheets(byte[] file)
    {
        using var stream = new MemoryStream(file);
        using var reader = file is [0x50, 0x4B, ..] or [0xD0, 0xCF, 0x11, 0xE0, ..] // .xlsx (zip) or .xls (OLE)
            ? ExcelReaderFactory.CreateReader(stream)
            : ExcelReaderFactory.CreateCsvReader(stream, new ExcelReaderConfiguration
            {
                FallbackEncoding = IsUtf8(file) ? Encoding.UTF8 : Encoding.GetEncoding(1252),
                AutodetectSeparators = [',', ';', '\t', '|'],
            });
        var sheets = new List<List<Line>>();
        do
        {
            var lines = new List<Line>();
            for (var number = 1; reader.Read(); number++)
            {
                var cells = Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray();
                if (cells.Any(c => c?.ToString()?.Trim() is { Length: > 0 })) lines.Add(new Line(number, cells));
            }
            sheets.Add(lines);
        } while (reader.NextResult());
        return sheets;
    }

    private static bool IsUtf8(byte[] file)
    {
        try
        {
            _ = new UTF8Encoding(false, true).GetString(file);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>Lower case, no accents, letters and digits only: "Fecha de Emisión" → "fechadeemision", "e-NCF" → "encf".</summary>
    internal static string Normalize(string header) =>
        new(header.Normalize(NormalizationForm.FormD).Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record Columns(int Rnc, int? Name, int Ncf, int? Date, int? Total, int? Itbis, int? Status)
    {
        private static readonly string[] Buyer = ["comprador", "receptor", "cliente"];
        private static readonly string[] OtherDates = ["pago", "recepcion", "vencimiento", "firma", "acuse", "aprobacion"];
        private static readonly string[] OtherItbis = ["retenido", "percibido", "costo", "proporcionalidad", "adelantar", "sujeto"];

        public static Columns? Find(List<string> h)
        {
            int? Pick(Func<string, bool> match, params string[] prefer)
            {
                var all = h.Select((n, i) => (n, i)).Where(x => x.n.Length > 0 && match(x.n) && !Buyer.Any(x.n.Contains)).ToList();
                var best = all.FirstOrDefault(x => prefer.Any(x.n.Contains));
                return best.n is not null ? best.i + 1 : all.Count > 0 ? all[0].i + 1 : null;
            }
            var rnc = Pick(n => n.Contains("rnc") || n.Contains("cedula"), "emisor", "proveedor", "suplidor");
            var ncf = Pick(n => n.Contains("ncf") && !n.Contains("modificado") && !n.Contains("afectado") && !n.Contains("referencia"));
            if (rnc is null || ncf is null || rnc == ncf) return null;
            return new Columns(rnc.Value,
                Pick(n => n.Contains("razonsocial") || n.Contains("nombre"), "emisor", "razonsocial"),
                ncf.Value,
                Pick(n => n.Contains("fecha") && !OtherDates.Any(n.Contains), "emision", "comprobante"),
                Pick(n => (n.Contains("total") || n.Contains("montofacturado")) && !n.Contains("itbis") && !n.Contains("isc"), "montototal"),
                Pick(n => n.Contains("itbis") && !OtherItbis.Any(n.Contains), "facturado"),
                Pick(n => n.Contains("estado")));
        }

        /// <summary>Null for a blank line, a total line or a document the DGII lists as voided or rejected.</summary>
        public Row? Read(Line r)
        {
            var ncf = r.Text(Ncf).ToUpperInvariant();
            var rnc = SharedKernel.Rnc.Normalize(r.Text(Rnc));
            if (!Domain.Ncf.IsValid(ncf) || !SharedKernel.Rnc.IsValid(rnc)) return null;
            if (Status is { } s && Normalize(r.Text(s)) is var status && (status.Contains("anul") || status.Contains("rechaz"))) return null;
            return new Row(r.Number, rnc!, Name is { } n && r.Text(n) is { Length: > 0 } name ? name : null, ncf,
                Date is { } d ? DateOf(r.Cell(d)) : null, Total is { } t ? Amount(r.Cell(t)) : null, Itbis is { } i ? Amount(r.Cell(i)) : null);
        }
    }

    private static readonly string[] DateFormats = ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "d-M-yyyy", "yyyyMMdd"];

    private static DateOnly? DateOf(object? cell) => cell switch
    {
        DateTime t => DateOnly.FromDateTime(t),
        null => null,
        var v => DateOnly.TryParseExact(v.ToString()!.Trim().Split(' ')[0], DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
    };

    private static decimal? Amount(object? cell) => cell switch
    {
        double d => Math.Round((decimal)d, 2),
        null => null,
        var v => decimal.TryParse(v.ToString()!.Replace("RD$", "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null,
    };
}

/// <summary>TK-223: crosses the DGII Excel with the purchases and expenses in Abastalia: what the DGII has that Abastalia does not (missing), what is in both with
/// another amount or ITBIS (difference), what is repeated, and what Abastalia reports for those dates that the DGII does not list. Reads only.</summary>
internal static class ReconcileDgiiReceived
{
    public enum MatchStatus { Matched, Missing, Difference, Duplicate, OnlyInAbastalia }

    /// <param name="Row">Excel row; null for <see cref="MatchStatus.OnlyInAbastalia"/>.</param>
    /// <param name="DocumentType">Purchase or Expense, when Abastalia has it.</param>
    /// <param name="SupplierId">The active supplier with that RNC, when there is one.</param>
    public sealed record Item(MatchStatus Status, int? Row, string Rnc, string? Name, string Ncf, DateOnly? Date, decimal? DgiiTotal, decimal? DgiiItbis,
        string? DocumentType, Guid? DocumentId, string? DocumentStatus, decimal? AbastaliaTotal, decimal? AbastaliaItbis, Guid? SupplierId);

    public sealed record Response(int Rows, int Matched, int Missing, int Differences, int Duplicates, int OnlyInAbastalia, IReadOnlyList<Item> Items);

    private sealed record Doc(string Type, Guid Id, string Status, string? Rnc, string Supplier, string Ncf, DateOnly Date, decimal Total, decimal Itbis);

    public static async Task<Response> RunAsync(PurchasingDbContext db, IReadOnlyList<DgiiReceivedFile.Row> rows, CancellationToken ct)
    {
        var ncfs = rows.Select(r => r.Ncf).Distinct().ToList();
        var dates = rows.Where(r => r.Date is not null).Select(r => r.Date!.Value).ToList();
        var (from, to) = dates.Count > 0 ? (dates.Min(), dates.Max()) : (DateOnly.MaxValue, DateOnly.MinValue);
        var purchases = await db.Purchases.AsNoTracking()
            .Where(p => p.Status != PurchaseStatus.Voided && p.Ncf != "" && (ncfs.Contains(p.Ncf) || (p.InvoiceDate >= from && p.InvoiceDate <= to)))
            .Select(p => new Doc("Purchase", p.Id, p.Status.ToString(), p.SupplierRnc, p.SupplierName, p.Ncf, p.InvoiceDate, p.Total, p.Itbis)).ToListAsync(ct);
        var expenses = await db.Expenses.AsNoTracking()
            .Where(e => !e.IsVoided && (ncfs.Contains(e.Ncf) || (e.Date >= from && e.Date <= to)))
            .Select(e => new Doc("Expense", e.Id, "Recorded", e.SupplierRnc, e.SupplierName, e.Ncf, e.Date, e.Total, e.Itbis)).ToListAsync(ct);
        var abastalia = purchases.Concat(expenses).ToLookup(d => (d.Rnc, d.Ncf));
        var suppliers = (await db.Suppliers.AsNoTracking().Where(s => s.IsActive && s.Rnc != null).Select(s => new { s.Id, s.Rnc }).ToListAsync(ct))
            .GroupBy(s => s.Rnc!).ToDictionary(g => g.Key, g => g.First().Id);

        var items = new List<Item>();
        var seen = new HashSet<(string, string)>();
        foreach (var r in rows)
        {
            var docs = abastalia[(r.Rnc, r.Ncf)].ToList();
            var doc = docs.FirstOrDefault();
            var status = !seen.Add((r.Rnc, r.Ncf)) || docs.Count > 1 ? MatchStatus.Duplicate
                : doc is null ? MatchStatus.Missing
                : Differs(r.Total, doc.Total) || Differs(r.Itbis, doc.Itbis) ? MatchStatus.Difference
                : MatchStatus.Matched;
            items.Add(new Item(status, r.Number, r.Rnc, r.Name ?? doc?.Supplier, r.Ncf, r.Date, r.Total, r.Itbis, doc?.Type, doc?.Id, doc?.Status, doc?.Total, doc?.Itbis,
                suppliers.TryGetValue(r.Rnc, out var s) ? s : null));
        }
        // What Abastalia reports in the 606 for those dates (posted purchases and expenses) that the DGII does not list: a typo in the NCF or the RNC, or a supplier that did not report.
        items.AddRange(purchases.Concat(expenses).Where(d => d.Status != "Draft" && d.Date >= from && d.Date <= to && !seen.Contains((d.Rnc ?? "", d.Ncf)))
            .OrderBy(d => d.Date)
            .Select(d => new Item(MatchStatus.OnlyInAbastalia, null, d.Rnc ?? "", d.Supplier, d.Ncf, d.Date, null, null, d.Type, d.Id, d.Status, d.Total, d.Itbis, null)));

        int Count(MatchStatus s) => items.Count(i => i.Status == s);
        return new Response(rows.Count, Count(MatchStatus.Matched), Count(MatchStatus.Missing), Count(MatchStatus.Difference), Count(MatchStatus.Duplicate),
            Count(MatchStatus.OnlyInAbastalia), items);
    }

    private static bool Differs(decimal? dgii, decimal abastalia) => dgii is { } d && Math.Abs(d - abastalia) > Purchase.InvoiceTolerance;

    internal sealed record Body(string? File, string? Name);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/purchasing/dgii-reconciliation", async (Body b, PurchasingDbContext db, CancellationToken ct) =>
                {
                    byte[]? file = null;
                    try { file = b.File is null ? null : Convert.FromBase64String(b.File); }
                    catch (FormatException) { }
                    if (file is not { Length: > 0 and <= 10_000_000 }) return EndpointExtensions.Problem(DgiiReceivedFile.Unreadable);
                    var rows = DgiiReceivedFile.Read(file);
                    return rows.IsFailure ? EndpointExtensions.Problem(rows.Error!) : Results.Ok(await RunAsync(db, rows.Value!, ct));
                })
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
    }
}

/// <summary>TK-223: "Crear compra en borrador" from a row the DGII has and Abastalia does not. The supplier is found by RNC, or created with the name
/// in the DGII taxpayer registry (or the one in the Excel). The draft has the invoice's totals and no products: a person adds them before receiving it.</summary>
internal static class DraftFromDgii
{
    public sealed record Command(string Rnc, string? Name, string Ncf, DateOnly Date, decimal Total, decimal Itbis) : ICommand<Response>;
    public sealed record Response(Guid PurchaseId, Guid SupplierId, string Supplier);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator(TimeProvider clock)
        {
            RuleFor(c => c.Rnc).Must(r => SharedKernel.Rnc.IsValid(SharedKernel.Rnc.Normalize(r))).WithMessage("El RNC debe tener 9 dígitos (u 11 si es cédula).");
            RuleFor(c => c.Ncf).Must(n => Domain.Ncf.IsValid(n?.Trim().ToUpperInvariant())).WithMessage("El NCF no es válido (B01… de 11 caracteres o E31… de 13).");
            RuleFor(c => c.Date).Must(d => DocumentDate.IsValid(d, clock)).WithMessage(DocumentDate.Message);
            RuleFor(c => c.Total).GreaterThan(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.Itbis).GreaterThanOrEqualTo(0).PrecisionScale(19, 2, true).LessThan(c => c.Total);
            RuleFor(c => c.Name).MaximumLength(150);
        }
    }

    internal sealed class Handler(PurchasingDbContext db, ITaxpayerRegistry registry, IUserContext user, IAuditLog audit, TimeProvider clock)
        : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var rnc = SharedKernel.Rnc.Normalize(c.Rnc)!;
            var ncf = c.Ncf.Trim().ToUpperInvariant();
            var supplier = await SupplierByRnc.FindOrCreateAsync(db, registry, audit, rnc, c.Name, ct);
            if (await db.Purchases.AnyAsync(x => x.SupplierId == supplier.Id && x.Ncf == ncf && x.Status != PurchaseStatus.Voided, ct)
                || await db.Expenses.AnyAsync(e => e.SupplierId == supplier.Id && e.Ncf == ncf && !e.IsVoided, ct))
                return PurchasingErrors.DuplicateNcf;

            var purchase = new Purchase
            {
                Id = Guid.CreateVersion7(), Status = PurchaseStatus.Draft, SupplierId = supplier.Id, SupplierName = supplier.Name, SupplierRnc = rnc, Ncf = ncf,
                InvoiceDate = c.Date, Terms = supplier.CreditDays > 0 ? PaymentTerms.Credit : PaymentTerms.Cash,
                DueDate = supplier.CreditDays > 0 ? c.Date.AddDays(supplier.CreditDays) : null,
                Subtotal = c.Total - c.Itbis, Itbis = c.Itbis, Total = c.Total,
                Notes = "Del Excel de comprobantes recibidos de la DGII. Agregue los productos antes de recibirla.",
                CreatedUtc = clock.GetUtcNow().UtcDateTime, UserId = user.UserId,
            };
            db.Purchases.Add(purchase);
            await audit.RecordAsync("purchasing.draft-from-dgii", "Purchase", purchase.Id, new { rnc, ncf, c.Date, c.Total, c.Itbis }, ct);
            return new Response(purchase.Id, supplier.Id, supplier.Name);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/purchasing/dgii-reconciliation/draft", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/purchasing/purchases/{r.PurchaseId}", r)))
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
    }
}

/// <summary>TK-223: the active supplier with an RNC, or a new one named as in the DGII taxpayer registry (else the name given, else "RNC …").
/// "Registrar como gasto" from the Excel calls it before the usual expense endpoint.</summary>
internal static class SupplierByRnc
{
    public static async Task<Supplier> FindOrCreateAsync(PurchasingDbContext db, ITaxpayerRegistry registry, IAuditLog audit, string rnc, string? name, CancellationToken ct)
    {
        if (await db.Suppliers.FirstOrDefaultAsync(s => s.Rnc == rnc && s.IsActive, ct) is { } found) return found;
        name = await registry.NameAsync(rnc, ct) ?? (string.IsNullOrWhiteSpace(name) ? $"RNC {rnc}" : name.Trim());
        var supplier = new Supplier { Id = Guid.CreateVersion7(), Name = name.Length > 150 ? name[..150] : name, Rnc = rnc };
        db.Suppliers.Add(supplier);
        await audit.RecordAsync("purchasing.save-supplier", "Supplier", supplier.Id, new { supplier.Name, supplier.Rnc, Source = "dgii-reconciliation" }, ct);
        return supplier;
    }

    public sealed record Command(string Rnc, string? Name) : ICommand<Response>;
    public sealed record Response(Guid Id, string Name);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Rnc).Must(r => SharedKernel.Rnc.IsValid(SharedKernel.Rnc.Normalize(r))).WithMessage("El RNC debe tener 9 dígitos (u 11 si es cédula).");
            RuleFor(c => c.Name).MaximumLength(150);
        }
    }

    internal sealed class Handler(PurchasingDbContext db, ITaxpayerRegistry registry, IAuditLog audit) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var s = await FindOrCreateAsync(db, registry, audit, SharedKernel.Rnc.Normalize(c.Rnc)!, c.Name, ct);
            return new Response(s.Id, s.Name);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/purchasing/dgii-reconciliation/supplier", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp())
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
    }
}
