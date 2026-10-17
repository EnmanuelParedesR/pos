using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using ClosedXML.Excel;
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
using Abastalia.Fiscal.Domain;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Fiscal.Features;

/// <summary>TK-155: the DGII test set (step 2 of the certification portal) turned into e-CF. The set is an Excel with one row per e-CF and one
/// column per XML field; the XML must carry the same fields in the same order. The generator is generic: it walks the official XSD of the row's
/// type (<c>TipoeCF</c>) and fills every element whose name matches a column, so it does not know the set's contents in advance.
/// <list type="bullet">
/// <item>Columns match element names ignoring case (a name the XSD repeats under another parent is <c>Parent.Name</c>). A repeated element (items, payment forms, subtotals…) is numbered: <c>NombreItem[1]</c>,
/// <c>NombreItem[2]</c>; nested repeats carry every index (<c>MontoSubDescuento[1][2]</c>) or only the innermost one.</item>
/// <item>An empty cell, or <c>#e</c>, means "not sent" (the DGII forbids empty elements). <c>FechaHoraFirma</c> is the signing time when the set
/// leaves it empty.</item>
/// <item>A 32 under RD$250,000 also gets its RFCE, built from the signed e-CF as in a sale.</item>
/// </list></summary>
/// <remarks>shortcut: only the types with an embedded XSD (31 to 34); a set with 41, 43–47 reports those rows (docs/ecf-propio.md §11 #5).
/// The real Excel layout (index style, "#e", one sheet or several) is not confirmed until a store downloads its set (§11 #30).</remarks>
internal static class TestSet
{
    public sealed record Row(int Number, Dictionary<string, string> Values);

    /// <summary>Reads an .xlsx (the first sheet with <c>TipoeCF</c> and <c>eNCF</c> columns) or a CSV (comma, semicolon or tab).</summary>
    public static Result<List<Row>> Read(byte[] file, string name)
    {
        try
        {
            var table = name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? Xlsx(file) : Csv(Encoding.UTF8.GetString(file));
            if (table is not [var header, ..]) return OperationsErrors.UnreadableFile("no tiene filas");
            var keys = header.Select(h => h.Trim().ToLowerInvariant()).ToList();
            if (!keys.Contains("tipoecf") || !keys.Contains("encf")) return OperationsErrors.UnreadableFile("faltan las columnas TipoeCF y eNCF");
            var rows = new List<Row>();
            for (var r = 1; r < table.Count; r++)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var c = 0; c < keys.Count && c < table[r].Count; c++)
                    if (keys[c].Length > 0 && Clean(table[r][c]) is { } v) values[keys[c]] = v;
                if (values.Count > 0) rows.Add(new Row(r + 1, values));
            }
            return rows.Count == 0 ? OperationsErrors.UnreadableFile("no tiene comprobantes") : rows;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return OperationsErrors.UnreadableFile("no es un Excel (.xlsx) ni un CSV válido");
        }
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) || v.Trim() == "#e" ? null : v.Trim();

    private static List<List<string>> Xlsx(byte[] file)
    {
        using var book = new XLWorkbook(new MemoryStream(file));
        foreach (var sheet in book.Worksheets)
        {
            var rows = sheet.RowsUsed().Select(r => Enumerable.Range(1, sheet.LastColumnUsed()?.ColumnNumber() ?? 0).Select(c => Text(r.Cell(c))).ToList()).ToList();
            if (rows is [var header, ..] && header.Any(h => h.Trim().Equals("TipoeCF", StringComparison.OrdinalIgnoreCase))) return rows;
        }
        return [];
    }

    private static string Text(IXLCell cell) => cell.Value switch
    {
        { IsNumber: true } v => v.GetNumber().ToString(CultureInfo.InvariantCulture),
        { IsDateTime: true } v => v.GetDateTime().ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
        { IsBlank: true } => "",
        var v => v.ToString(CultureInfo.InvariantCulture),
    };

    private static readonly char[] Separators = [';', '\t', ','];

    private static List<List<string>> Csv(string text)
    {
        var first = text.Split('\n', 2)[0];
        var separator = Separators.MaxBy(c => first.Count(x => x == c));
        var (rows, row, cell, quoted) = (new List<List<string>>(), new List<string>(), new StringBuilder(), false);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == separator) { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n')
            {
                row.Add(cell.ToString().TrimEnd('\r'));
                (rows, row) = ([.. rows, row], []);
                cell.Clear();
            }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) rows.Add([.. row, cell.ToString()]);
        return rows;
    }

    /// <summary>The unsigned e-CF of one row, in the XSD order, or why it cannot be built.</summary>
    public static Result<(EcfType Type, XDocument Xml)> Build(Row row, DateTime nowUtc)
    {
        if (!int.TryParse(row.Values.GetValueOrDefault("tipoecf"), out var t) || !Enum.IsDefined((EcfType)t))
            return Error.Validation("Fiscal.TestSetType", $"Fila {row.Number}: el tipo {row.Values.GetValueOrDefault("tipoecf")} no se puede generar todavía (Abastalia emite 31, 32, 33 y 34).");
        var type = (EcfType)t;
        var values = new Dictionary<string, string>(row.Values, StringComparer.Ordinal);
        values.TryAdd("fechahorafirma", (nowUtc + BusinessDay.Offset).ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture));
        var schema = EcfXml.Schema(EcfXml.SchemaOf(type));
        var root = (XmlSchemaElement)schema.GlobalElements[new XmlQualifiedName("ECF")]!;
        var walker = new Walker(values, []);
        walker.Declare(root);
        return (type, new XDocument(walker.Element(root, "", []) ?? new XElement("ECF")));
    }

    /// <param name="First">Per element name, its first declaration in schema order: only that one matches the bare column name. Later ones with
    /// the same name (the <c>NumeroLinea</c> of a <c>DescuentoORecargo</c> after the item's) need <c>Parent.Name</c>, e.g.
    /// <c>DescuentoORecargo.NumeroLinea[1]</c>.</param>
    private sealed record Walker(Dictionary<string, string> Values, Dictionary<string, XmlSchemaElement> First)
    {
        public void Declare(XmlSchemaParticle particle)
        {
            switch (particle)
            {
                case XmlSchemaElement e:
                    First.TryAdd(e.Name!.ToLowerInvariant(), e);
                    if (e.ElementSchemaType is XmlSchemaComplexType c) Declare(c.ContentTypeParticle);
                    break;
                case XmlSchemaGroupBase g:
                    foreach (var item in g.Items.OfType<XmlSchemaParticle>()) Declare(item);
                    break;
            }
        }

        /// <summary>A complex element with no value anywhere below is left out, unless the XSD requires it (the 32's <c>Comprador</c> of an
        /// anonymous buyer): then it goes empty, as in a sale.</summary>
        public XElement? Element(XmlSchemaElement e, string parent, int[] index)
        {
            if (e.ElementSchemaType is not XmlSchemaComplexType complex)
                return Lookup(e, parent, index) is { } v ? new XElement(e.Name!, v) : null;
            var children = Particle(complex.ContentTypeParticle, e.Name!, index).ToList();
            return children.Exists(HasContent) ? new XElement(e.Name!, children) : e.MinOccurs > 0 ? new XElement(e.Name!) : null;
        }

        private IEnumerable<XElement> Particle(XmlSchemaParticle particle, string parent, int[] index)
        {
            switch (particle)
            {
                case XmlSchemaElement { MaxOccurs: > 1 } repeated:
                    for (var i = 1; i <= 1000 && Element(repeated, parent, [.. index, i]) is { } item && (HasContent(item) || i == 1); i++) yield return item;
                    break;
                case XmlSchemaElement single:
                    if (Element(single, parent, index) is { } e) yield return e;
                    break;
                case XmlSchemaGroupBase group:
                    foreach (var item in group.Items.OfType<XmlSchemaParticle>())
                        foreach (var child in Particle(item, parent, index)) yield return child;
                    break;
                // xs:any is the signature: added when signing.
            }
        }

        /// <summary>The column with every index (<c>MontoSubDescuento[2][1]</c>); an inner index of 1 may be left out (<c>MontoSubDescuento[2]</c>),
        /// and so may all of them when they are all 1 (<c>MontoSubDescuento</c>).</summary>
        private string? Lookup(XmlSchemaElement e, string parent, int[] index)
        {
            var name = e.Name!.ToLowerInvariant();
            string[] keys = First[name] == e ? [$"{parent.ToLowerInvariant()}.{name}", name] : [$"{parent.ToLowerInvariant()}.{name}"];
            foreach (var key in keys)
                for (var n = index.Length; n >= 0; n--)
                {
                    if (Values.TryGetValue(key + string.Concat(index[..n].Select(i => $"[{i}]")), out var v)) return v;
                    if (n > 0 && index[n - 1] != 1) break;
                }
            return null;
        }
    }

    private static bool HasContent(XElement e) => e.DescendantsAndSelf().Any(x => !x.HasElements && x.Value.Length > 0);
}

/// <summary>TK-155: imports the test set: every row is generated, validated against its XSD and signed (and its RFCE when it goes by the summary).
/// All or nothing: one row that cannot be built stops the import with every problem listed. A new import replaces the previous set, as
/// a rejection restarts the set at the DGII.</summary>
internal static class ImportTestSet
{
    /// <param name="File">The Excel (.xlsx) or CSV, in Base64.</param>
    public sealed record Command(string File, string Name) : ICommand<Response>;
    public sealed record Response(int Imported, IReadOnlyList<string> Errors);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.File).NotEmpty().MaximumLength(14_000_000).WithMessage("Elija el Excel del set de pruebas (máximo 10 MB).");
            RuleFor(c => c.Name).NotEmpty().MaximumLength(260);
        }
    }

    internal sealed class Handler(FiscalDbContext db, IEcfSigner signer, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(c.File); }
            catch (FormatException) { return OperationsErrors.UnreadableFile("llegó dañado"); }
            var rows = TestSet.Read(bytes, c.Name);
            if (rows.IsFailure) return rows.Error!;

            var now = clock.GetUtcNow().UtcDateTime;
            var (cases, errors) = (new List<CertificationCase>(), new List<string>());
            foreach (var row in rows.Value)
            {
                var built = TestSet.Build(row, now);
                if (built.IsFailure) { errors.Add(built.Error!.Description); continue; }
                var (type, xml) = built.Value;
                if (EcfXml.ValidateUnsigned(xml, type) is [var first, ..]) { errors.Add($"Fila {row.Number}: {first}"); continue; }
                var (signed, code) = signer.Sign(xml);
                var encf = row.Values["encf"];
                var total = decimal.TryParse(row.Values.GetValueOrDefault("montototal"), NumberStyles.Number, CultureInfo.InvariantCulture, out var m) ? m : 0;
                var item = new CertificationCase
                {
                    Id = Guid.CreateVersion7(), Row = row.Number, ENcf = encf.Length > 13 ? encf[..13] : encf, Type = type, SignedXml = EcfSubmissionQueue.Zip(signed),
                    SecurityCode = code, Status = SubmissionStatus.Pending, CreatedUtc = now,
                    Channel = type == EcfType.Consumer32 && total < EcfLimits.IdentifiedConsumer ? EcfChannel.Rfce : EcfChannel.Ecf,
                };
                if (item.Channel == EcfChannel.Rfce)
                {
                    var summary = EcfXml.Rfce(signed, code);
                    if (EcfXml.ValidateUnsigned(summary, EcfXml.RfceSchema) is [var error, ..]) { errors.Add($"Fila {row.Number} (RFCE): {error}"); continue; }
                    item.RfceXml = EcfSubmissionQueue.Zip(signer.Sign(summary).SignedXml);
                }
                cases.Add(item);
            }
            if (errors.Count > 0) return new Response(0, errors);

            await db.Set<CertificationCase>().ExecuteDeleteAsync(ct);
            db.Set<CertificationCase>().AddRange(cases);
            await audit.RecordAsync("fiscal.import-test-set", "CertificationCase", Guid.Empty, new { c.Name, Rows = cases.Count }, ct);
            return new Response(cases.Count, []);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/certification/test-set", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Ok(r)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>TK-155: sends the pending rows of the set to CerteCF in the Excel's order and asks for the result of those already sent (the owner
/// presses "Enviar y consultar" until every row has its answer). Only in CerteCF: the set means nothing anywhere else.</summary>
/// <remarks>Not a command: it talks to the DGII, so each row is saved on its own instead of inside one transaction.</remarks>
internal static class SendTestSet
{
    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/certification/send", async (FiscalDbContext db, IEcfProvider provider, ISettingsQueries settings, TimeProvider clock,
                    CancellationToken ct) =>
                {
                    if (provider.IsSimulated || provider.Environment != FiscalEnvironment.CerteCF) return ((Result<object>)OperationsErrors.NotCerteCF).ToHttp();
                    var rnc = (await settings.GetBusinessProfileAsync(ct)).Rnc ?? "";
                    var open = await db.Set<CertificationCase>().Where(x => x.Status == SubmissionStatus.Pending || x.Status == SubmissionStatus.Sent)
                        .OrderBy(x => x.Row).ToListAsync(ct);
                    foreach (var x in open)
                    {
                        try
                        {
                            var a = x.Status == SubmissionStatus.Sent ? await EcfSubmissionQueue.PollAsync(provider, rnc, x.ENcf, x.SecurityCode, x.Channel, x.TrackId, ct)
                                : x.Channel == EcfChannel.Rfce ? await provider.SendRfceAsync(rnc, x.ENcf, EcfSubmissionQueue.Unzip(x.RfceXml!), ct)
                                : await provider.SendEcfAsync(rnc, x.ENcf, EcfSubmissionQueue.Unzip(x.SignedXml), ct);
                            if (a.State is DgiiState.InProcess or DgiiState.NotFound) (x.Status, x.TrackId) = (SubmissionStatus.Sent, a.TrackId ?? x.TrackId);
                            else
                                (x.Status, x.Messages, x.ResultUtc, x.TrackId) = (a.State switch
                                {
                                    DgiiState.Accepted => SubmissionStatus.Accepted,
                                    DgiiState.AcceptedConditional => SubmissionStatus.AcceptedConditional,
                                    _ => SubmissionStatus.Rejected,
                                }, a.Messages, clock.GetUtcNow().UtcDateTime, a.TrackId ?? x.TrackId);
                        }
                        catch (Exception e) when (!ct.IsCancellationRequested)
                        {
                            x.Messages = e.Message.Length > 2000 ? e.Message[..2000] : e.Message;
                        }
                        await db.SaveChangesAsync(ct);
                    }
                    return Results.Ok(await CertificationStatus.CasesAsync(db, ct));
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>TK-155: the assistant at a glance: the steps the owner marked, the test set with each row's answer, and the simulation (sales
/// issued in CerteCF) by status.</summary>
internal static class CertificationStatus
{
    public sealed record Step(int Number, bool Done, DateTime? DoneUtc);
    public sealed record Case(Guid Id, int Row, string ENcf, EcfType Type, EcfChannel Channel, SubmissionStatus Status, string? TrackId, string? Messages);
    public sealed record Simulation(int Accepted, int Rejected, int Waiting);
    /// <param name="Guide">TK-256: the help, links and next action of each step, from <see cref="CertificationGuide"/>.</param>
    public sealed record Response(FiscalEnvironment Environment, IReadOnlyList<Step> Steps, IReadOnlyList<Case> Cases, Simulation Simulation,
        IReadOnlyList<CertificationGuideStep> Guide);

    public static Task<List<Case>> CasesAsync(FiscalDbContext db, CancellationToken ct) =>
        db.Set<CertificationCase>().AsNoTracking().OrderBy(c => c.Row)
            .Select(c => new Case(c.Id, c.Row, c.ENcf, c.Type, c.Channel, c.Status, c.TrackId, c.Messages)).ToListAsync(ct);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/certification", async (FiscalDbContext db, FiscalDocuments documents, CancellationToken ct) =>
                {
                    var steps = await db.Set<CertificationStep>().AsNoTracking().OrderBy(s => s.Step).Select(s => new Step(s.Step, s.Done, s.DoneUtc)).ToListAsync(ct);
                    var simulation = await db.Documents.Where(d => d.Environment == FiscalEnvironment.CerteCF).GroupBy(d => d.Status)
                        .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
                    int Count(params SubmissionStatus[] s) => simulation.Where(x => s.Contains(x.Key)).Sum(x => x.Count);
                    return Results.Ok(new Response(documents.Environment, steps, await CasesAsync(db, ct), new Simulation(
                        Count(SubmissionStatus.Accepted, SubmissionStatus.AcceptedConditional), Count(SubmissionStatus.Rejected),
                        Count(SubmissionStatus.Pending, SubmissionStatus.Sent)), CertificationGuide.Steps));
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>TK-155: the owner ticks a step of the certification portal as done (or not). TK-225: the progress goes up to the cloud (Abastalia staff
/// sees who is stuck; the owner gets an e-mail).</summary>
internal static class MarkCertificationStep
{
    public sealed record Body(bool Done);

    public sealed record Command(int Step, bool Done) : ICommand<Ok>;

    internal sealed class Handler(FiscalDbContext db, IOutbox outbox, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var steps = await db.Set<CertificationStep>().ToListAsync(ct);
            var s = steps.Find(x => x.Step == c.Step);
            if (s is null) db.Set<CertificationStep>().Add(s = new CertificationStep { Step = c.Step });
            (s.Done, s.DoneUtc) = (c.Done, c.Done ? clock.GetUtcNow().UtcDateTime : null);
            var done = steps.Append(s).Distinct().Where(x => x.Done).ToList();
            outbox.Add(new CertificationProgressedV1([.. done.Select(x => x.Step).Order()], CertificationSteps.Count, done.Max(x => x.DoneUtc)));
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/fiscal/certification/steps/{step:int}", async (int step, Body body, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    step is < 0 or > 30 ? Results.NotFound() : (await h.Handle(new Command(step, body.Done), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>TK-155 "Firmar archivo de la DGII": signs the XML the portal hands out (postulation, sworn statement, delegation) with the store's
/// certificate, the same XMLDSig as the e-CF. Works with the certificate imported even while the store still signs simulated documents.
/// Fiscal documents (e-CF, RFCE, ANECF, receipts, approvals, the seed) are refused: they are only signed by the sale and the queue.
/// Audited with the root element and the SHA-256 of what was signed.</summary>
internal static class SignDgiiFile
{
    public static readonly string[] Refused = ["ECF", "RFCE", "ANECF", "ARECF", "ACECF", "SemillaModel"];

    public sealed record Command(string Xml) : ICommand<Response>;
    public sealed record Response(string Root, string SignedXml);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Xml).NotEmpty().MaximumLength(2_000_000).WithMessage("Elija el archivo XML que descargó del portal (máximo 2 MB).");
    }

    internal sealed class Handler(EcfCertificate certificate, IAuditLog audit) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (certificate.Current is not { } cert) return OperationsErrors.NoCertificate;
            XDocument xml;
            try
            {
                using var reader = XmlReader.Create(new StringReader(c.Xml.TrimStart('﻿')), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                xml = XDocument.Load(reader);
            }
            catch (XmlException) { return OperationsErrors.UnreadableFile("no es un XML válido"); }
            var root = xml.Root!.Name.LocalName;
            if (Refused.Contains(root, StringComparer.OrdinalIgnoreCase)) return OperationsErrors.SignNotAllowed;
            var (signed, _) = new CertificateEcfSigner(cert).Sign(xml);
            await audit.RecordAsync("fiscal.sign-dgii-file", "EcfCertificate", Guid.Empty,
                new { Root = root, Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.Xml))), cert.Thumbprint }, ct);
            return new Response(root, signed);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/certification/sign", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Ok(r)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}
