using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Fiscal.Contracts;
using Abastalia.Inventory.Contracts;
using Abastalia.Purchasing.Contracts;
using Abastalia.Purchasing.Domain;
using Abastalia.SharedKernel;
using Abastalia.Sync.Contracts;

namespace Abastalia.Purchasing.Features;

/// <summary>TK-224: the store has no AI, so the invoice goes to the cloud (as the node), which reads it with the chat model. What comes back is checked
/// here (RNC, NCF, date, totals), matched with this store's supplier and the products bought from it before, and returned to fill a new purchase
/// form. Nothing is saved: a person reviews it and saves the draft as usual.</summary>
internal static class ReadSupplierInvoice
{
    public sealed record Line(string Description, decimal? Quantity, decimal? UnitCost, ItbisRate? ItbisRate, Guid? ProductId, string? ProductName);

    /// <param name="SupplierId">This store's active supplier with the RNC read; null when it is not registered yet.</param>
    /// <param name="Warnings">What the person must check, in Spanish.</param>
    /// <param name="Source"><c>ai</c> (read by the cloud's AI) or <c>ecf</c> (TK-240: taken from the supplier's signed e-CF, exact).</param>
    public sealed record Response(Guid? SupplierId, string? SupplierRnc, string? SupplierName, string? Ncf, DateOnly? Date, IReadOnlyList<Line> Lines,
        decimal? Subtotal, decimal? Itbis, decimal? Total, decimal Confidence, IReadOnlyList<string> Warnings, string Source = Ai);

    public const string Ai = "ai";
    public const string Ecf = "ecf";

    public static readonly Error NotAnEcf = Error.Validation("Purchasing.NotAnEcf",
        "Ese XML no es un comprobante fiscal electrónico (e-CF). Suba el XML que le envió el suplidor, o el PDF o una foto de la factura.");

    /// <summary>TK-240: the supplier's e-CF as if it had been read: every amount is exact, so nothing is doubtful. An exempt line goes as 0 %.</summary>
    internal static SupplierInvoiceReading Reading(SupplierEcf e) =>
        new(e.IssuerRnc, e.IssuerName, e.ENcf, e.IssuedOn, [.. e.Lines.Select(l => new SupplierInvoiceLineReading(l.Description, l.Quantity, l.UnitCost, l.ItbisPercent ?? 0))],
            e.Subtotal, e.Itbis, e.Total, 1m, []);

    public static readonly Error NotConnected = Error.Conflict("Purchasing.AiNeedsCloud",
        "La lectura de facturas con IA se hace en la nube de Abastalia: conecte la tienda a la nube para usarla. Mientras, escriba la compra a mano.");
    public static readonly Error Unreachable = Error.Conflict("Purchasing.AiUnreachable", "No se pudo hablar con la nube de Abastalia. Revise el internet e intente de nuevo.");
    public static readonly Error Busy = Error.Conflict("Purchasing.AiBusy", "Se leyeron muchas facturas en poco tiempo. Espere un minuto e intente de nuevo.");
    /// <summary>The cloud's month quota (or daily cap) is spent: a batch stops there; the message says when it renews.</summary>
    public const string QuotaExceededCode = "Purchasing.AiQuotaExceeded";
    public static readonly Error InvalidFile = Error.Validation("Purchasing.AiInvalidFile", "Suba un PDF, una foto (JPG, PNG o WEBP) o el XML del e-CF, de hasta 4 MB.");

    public static async Task<Result<SupplierInvoiceReading>> AskCloudAsync(ICloudClient cloud, SupplierInvoiceReadRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await cloud.PostAsJsonAsync("/ai/v1/supplier-invoice", request, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<SupplierInvoiceReading>(ct))!;
            var body = await response.Content.ReadAsStringAsync(ct);
            var detail = body.Length > 0 && JsonSerializer.Deserialize<JsonElement>(body) is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("detail", out var d) ? d.GetString() : null;
            // The per-minute limit answers 429 with no body; the month's quota and the daily cap answer 429 saying when they renew.
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return detail is null ? Busy : Error.Conflict(QuotaExceededCode, detail);
            return Error.Conflict("Purchasing.AiRefused", detail ?? $"La nube respondió {(int)response.StatusCode}.");
        }
        catch (InvalidOperationException) { return NotConnected; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) { return Unreachable; }
    }

    /// <summary>The reading checked and matched with this store: invalid fields are dropped with a warning, never guessed.</summary>
    internal static async Task<Response> ReviewAsync(PurchasingDbContext db, ITaxpayerRegistry registry, SupplierInvoiceReading r, TimeProvider clock, CancellationToken ct)
    {
        var warnings = new List<string>();
        var rnc = Rnc.Normalize(r.SupplierRnc);
        if (!Rnc.IsValid(rnc))
        {
            if (r.SupplierRnc is not null) warnings.Add($"El RNC leído ({r.SupplierRnc}) no es válido: elija el suplidor.");
            rnc = null;
        }
        var supplier = rnc is null ? null : await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Rnc == rnc && s.IsActive, ct);
        var name = supplier?.Name ?? (rnc is null ? null : await registry.NameAsync(rnc, ct)) ?? r.SupplierName;
        if (rnc is not null && supplier is null) warnings.Add($"El suplidor con RNC {rnc} no está registrado: créelo en Suplidores antes de guardar.");

        var ncf = new string((r.Ncf ?? "").Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        if (!Ncf.IsValid(ncf))
        {
            warnings.Add(r.Ncf is null ? "No se leyó el NCF: escríbalo." : $"El NCF leído ({r.Ncf}) no es válido: escríbalo.");
            ncf = "";
        }
        var date = r.Date is { } d && DocumentDate.IsValid(d, clock) ? d : (DateOnly?)null;
        if (date is null) warnings.Add("No se leyó una fecha válida: escríbala.");

        // Products bought from this supplier before, by their name in Abastalia: an exact (or contained) name is a match; the rest the person picks.
        var known = supplier is null ? [] : await db.Purchases.AsNoTracking().Where(p => p.SupplierId == supplier.Id && p.Status != PurchaseStatus.Voided)
            .SelectMany(p => p.Lines).Select(l => new { l.ProductId, l.Description }).Distinct().ToListAsync(ct);
        var lines = r.Lines.Select(l =>
        {
            var key = Key(l.Description);
            var product = known.FirstOrDefault(k => Key(k.Description) == key) ?? known.FirstOrDefault(k => key.Length >= 4 && (Key(k.Description).Contains(key) || key.Contains(Key(k.Description))));
            return new Line(l.Description, l.Quantity is > 0 ? l.Quantity : null, l.UnitCost is >= 0 ? l.UnitCost : null, Rate(l.ItbisPercent), product?.ProductId, product?.Description);
        }).ToList();
        if (lines.Count == 0) warnings.Add("No se leyeron productos: agréguelos.");
        else if (lines.Any(l => l.ProductId is null)) warnings.Add("Algunos productos no se reconocieron: elija cada uno en la lista.");

        var linesSubtotal = lines.Sum(l => Money.Round((l.Quantity ?? 0) * (l.UnitCost ?? 0)));
        if (r.Subtotal is { } sub && lines.Count > 0 && Math.Abs(sub - linesSubtotal) > Purchase.InvoiceTolerance)
            warnings.Add($"La suma de las líneas (RD$ {linesSubtotal:N2}) no da el subtotal de la factura (RD$ {sub:N2}): revise cantidades y costos.");
        if (r is { Subtotal: { } s, Itbis: { } i, Total: { } t } && Math.Abs(s + i - t) > Purchase.InvoiceTolerance)
            warnings.Add("El subtotal más el ITBIS no da el total leído: revise los montos.");
        if (r.Confidence < 0.6m || r.Doubtful.Count > 0) warnings.Add("La IA no está segura de esta lectura: revísela completa contra la factura.");

        return new Response(supplier?.Id, rnc, name, ncf.Length > 0 ? ncf : null, date, lines, r.Subtotal, r.Itbis, r.Total, r.Confidence, warnings);
    }

    /// <summary>TK-240: an e-CF of the supplier, received through the cloud or uploaded as XML, reviewed like a reading. Its lines still need a product each.</summary>
    internal static async Task<Response> ReviewEcfAsync(PurchasingDbContext db, ITaxpayerRegistry registry, SupplierEcf e, TimeProvider clock, CancellationToken ct, string? note = null)
    {
        var review = await ReviewAsync(db, registry, Reading(e), clock, ct);
        return review with { Source = Ecf, Warnings = [.. note is null ? [] : new[] { note }, .. review.Warnings] };
    }

    /// <param name="EcfNote">Set when the data is the supplier's e-CF (exact), not what the AI read.</param>
    internal sealed record Read(SupplierInvoiceReading Reading, string? EcfNote);

    internal static async Task<Response> ReviewAsync(PurchasingDbContext db, ITaxpayerRegistry registry, Read read, TimeProvider clock, CancellationToken ct)
    {
        var review = await ReviewAsync(db, registry, read.Reading, clock, ct);
        return read.EcfNote is not { } note ? review : review with { Source = Ecf, Warnings = [note, .. review.Warnings] };
    }

    /// <summary>The uploaded file when it is a PDF, a photo or an XML of at most 4 MB. R10 #11: the type comes from the bytes, never from the name.</summary>
    internal static (byte[] Content, string Type)? File(string? base64)
    {
        byte[]? content = null;
        try { content = base64 is null ? null : Convert.FromBase64String(base64); }
        catch (FormatException) { }
        return content is { Length: > 0 and <= SupplierInvoiceReading.MaxFileBytes } && PurchaseFile.Sniff(content) is { } type ? (content, type) : null;
    }

    /// <summary>TK-240: the supplier's e-CF XML needs no AI (nor the cloud): it is read here, exactly. Anything else goes to the cloud's AI, unless the
    /// supplier also sent that e-CF (TK-154): its signed data wins over what the AI read on the paper.</summary>
    internal static async Task<Result<Read>> ReadAsync(byte[] content, string type, string? fileName, ICloudClient cloud, ISupplierEcfs ecfs, CancellationToken ct)
    {
        if (type == PurchaseFile.Xml)
            return ecfs.Read(Encoding.UTF8.GetString(content)) is { } e
                ? new Read(Reading(e), "Tomada del XML del e-CF del suplidor: los montos son los del comprobante.")
                : NotAnEcf;
        var reading = await AskCloudAsync(cloud, new SupplierInvoiceReadRequest(Convert.ToBase64String(content), type, fileName), ct);
        if (reading.IsFailure) return reading.Error!;
        var read = reading.Value;
        var ncf = new string((read.Ncf ?? "").Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        if (Rnc.Normalize(read.SupplierRnc) is { } rnc && Rnc.IsValid(rnc) && ncf.StartsWith('E') && await ecfs.ReceivedAsync(rnc, ncf, ct) is { } received)
            return new Read(Reading(received), $"El suplidor ya envió el e-CF {ncf}: se usaron sus datos, no los que leyó la IA.");
        return new Read(read, null);
    }

    private static ItbisRate? Rate(decimal? percent) => percent switch
    {
        18 => ItbisRate.General18,
        16 => ItbisRate.Reduced16,
        0 => ItbisRate.Exempt,
        _ => null,
    };

    private static string Key(string text) =>
        new(text.Normalize(NormalizationForm.FormD).Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal sealed record Body(string? File, string? FileName);

    /// <summary>TK-248: the month's readings from the cloud, or null when the store is not connected or the cloud does not answer (the button still works).</summary>
    public static async Task<SupplierInvoiceQuota?> QuotaAsync(ICloudClient cloud, CancellationToken ct)
    {
        try
        {
            using var response = await cloud.PostAsJsonAsync("/ai/v1/supplier-invoice/quota", new { }, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<SupplierInvoiceQuota>(ct) : null;
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/purchasing/purchases/read-invoice/quota", async (ICloudClient cloud, CancellationToken ct) =>
                    await QuotaAsync(cloud, ct) is { } quota ? Results.Ok(quota) : Results.NoContent())
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
            MapRead(app);
        }

        private static void MapRead(IEndpointRouteBuilder app) =>
            app.MapPost("/api/purchasing/purchases/read-invoice", async (Body b, ICloudClient cloud, PurchasingDbContext db, ITaxpayerRegistry registry,
                    ISupplierEcfs ecfs, TimeProvider clock, CancellationToken ct) =>
                {
                    // A file that is not a PDF, a photo or an XML never reaches the cloud.
                    if (File(b.File) is not { } file) return EndpointExtensions.Problem(InvalidFile);
                    var read = await ReadAsync(file.Content, file.Type, b.FileName, cloud, ecfs, ct);
                    return read.IsFailure ? EndpointExtensions.Problem(read.Error!) : Results.Ok(await ReviewAsync(db, registry, read.Value, clock, ct));
                })
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
    }
}

/// <summary>TK-224, batch: each of several invoices picked at once becomes its own draft, with the original attached. The supplier is found by the RNC
/// read, or created (with a warning to confirm it). Only the lines matched to a product, with quantity and cost, go in; the rest are listed to add.
/// Never received here: a person opens each draft, compares it with the invoice and receives it.</summary>
internal static class DraftFromInvoice
{
    public sealed record Command(ReadSupplierInvoice.Read Read, byte[] Content, string ContentType, string FileName) : ICommand<Response>;

    /// <param name="PurchaseId">Null when no valid RNC was read: with no supplier there is no draft, and the person fills the form from the review.</param>
    public sealed record Response(Guid? PurchaseId, ReadSupplierInvoice.Response Review);

    internal sealed class Handler(PurchasingDbContext db, ITaxpayerRegistry registry, ICatalogQueries catalog, IDgiiPublicLookup dgii, IUserContext user,
        IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var warnings = new List<string>();
            if (Rnc.Normalize(c.Read.Reading.SupplierRnc) is { } rnc && Rnc.IsValid(rnc) && !await db.Suppliers.AnyAsync(s => s.Rnc == rnc && s.IsActive, ct))
            {
                var created = await SupplierByRnc.FindOrCreateAsync(db, registry, audit, rnc, c.Read.Reading.SupplierName, ct);
                await db.SaveChangesAsync(ct);
                warnings.Add($"Se creó el suplidor {created.Name} con el RNC {rnc}: confirme que es el de la factura.");
            }
            var review = await ReadSupplierInvoice.ReviewAsync(db, registry, c.Read, clock, ct);
            if (review.SupplierId is not { } supplierId) return new Response(null, review);
            var supplier = await db.Suppliers.AsNoTracking().SingleAsync(s => s.Id == supplierId, ct);
            warnings.InsertRange(0, review.Warnings);
            var id = Guid.CreateVersion7();

            var ncf = review.Ncf ?? "";
            if (ncf.Length > 0 && (await db.Purchases.AnyAsync(x => x.SupplierId == supplierId && x.Ncf == ncf && x.Status != PurchaseStatus.Voided, ct)
                || await db.Expenses.AnyAsync(e => e.SupplierId == supplierId && e.Ncf == ncf && !e.IsVoided, ct)))
                return PurchasingErrors.DuplicateNcf;
            var date = review.Date ?? BusinessDay.Of(clock.GetUtcNow().UtcDateTime);
            // TK-252: asked to the DGII as when it is typed; refused, the draft keeps no NCF and the person decides when saving it.
            if (ncf.Length > 0 && await DgiiNcfCheck.RefuseAsync(dgii, audit, supplier.Rnc!, ncf, date, null, "Purchase", id, ct) is { } refused)
            {
                warnings.Add($"{refused.Description} Se dejó sin NCF.");
                ncf = "";
            }

            var matched = review.Lines.Where(l => l is { ProductId: not null, Quantity: not null, UnitCost: not null }).Select(l => (l.ProductId!.Value, (Guid?)null)).ToList();
            var products = (await catalog.GetForSaleAsync(matched, ct)).ToDictionary(p => p.ProductId);
            var lines = new List<PurchaseLine>();
            foreach (var l in review.Lines)
            {
                if (l is { ProductId: { } pid, Quantity: { } q, UnitCost: { } cost } && products.TryGetValue(pid, out var p))
                    lines.Add(new PurchaseLine
                    {
                        Id = Guid.CreateVersion7(), PurchaseId = id, LineNumber = lines.Count + 1, ProductId = p.ProductId, Description = p.Name, Quantity = q,
                        Factor = p.Factor, UnitCost = cost, ItbisRate = l.ItbisRate ?? p.ItbisRate,
                    });
                else warnings.Add($"Agregue: {l.Description} ({l.Quantity?.ToString("0.###") ?? "?"} × {l.UnitCost?.ToString("N2") ?? "?"})");
            }

            var purchase = new Purchase
            {
                Id = id, Status = PurchaseStatus.Draft, SupplierId = supplier.Id, SupplierName = supplier.Name, SupplierRnc = supplier.Rnc, Ncf = ncf, InvoiceDate = date,
                Terms = supplier.CreditDays > 0 ? PaymentTerms.Credit : PaymentTerms.Cash, DueDate = supplier.CreditDays > 0 ? date.AddDays(supplier.CreditDays) : null,
                Lines = lines, Notes = $"Leída de {c.FileName}: compárela con la factura adjunta y agregue lo que falte antes de recibirla.",
                AttachmentName = c.FileName, CreatedUtc = clock.GetUtcNow().UtcDateTime, UserId = user.UserId,
            };
            if (lines.Count == 0)
            {
                // Only the invoice's totals, as a draft from the DGII Excel (TK-223): the products go in before receiving it.
                purchase.Total = review.Total is > 0 and var total ? total : 0;
                purchase.Itbis = review.Itbis is >= 0 and var itbis && itbis < purchase.Total ? itbis : 0;
                purchase.Subtotal = purchase.Total - purchase.Itbis;
            }
            // With every line in, the printed totals win when within RD$ 1; else the lines' own.
            else if (lines.Count < review.Lines.Count || !purchase.Recalculate(review.Itbis, review.Total)) purchase.Recalculate();
            db.Purchases.Add(purchase);
            db.Set<PurchaseFile>().Add(new PurchaseFile { PurchaseId = id, Content = c.Content, ContentType = c.ContentType, FileName = c.FileName, CreatedUtc = purchase.CreatedUtc });
            await audit.RecordAsync("purchasing.draft-from-invoice", "Purchase", id, new { supplier.Rnc, ncf, date, purchase.Total, c.FileName, review.Source, Lines = lines.Count }, ct);
            return new Response(id, review with { Warnings = warnings });
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            // Read outside the transaction (the cloud takes seconds); the draft and its file are saved together.
            app.MapPost("/api/purchasing/purchases/draft-from-invoice", async (ReadSupplierInvoice.Body b, ICloudClient cloud, ISupplierEcfs ecfs,
                    ICommandHandler<Command, Response> h, CancellationToken ct) =>
                {
                    if (ReadSupplierInvoice.File(b.File) is not { } file) return EndpointExtensions.Problem(ReadSupplierInvoice.InvalidFile);
                    var read = await ReadSupplierInvoice.ReadAsync(file.Content, file.Type, b.FileName, cloud, ecfs, ct);
                    if (read.IsFailure) return EndpointExtensions.Problem(read.Error!);
                    return (await h.Handle(new Command(read.Value, file.Content, file.Type, PurchaseAttachment.Name(b.FileName)), ct)).ToHttp();
                })
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
    }
}
