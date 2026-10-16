using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Security;
using Toki.Fiscal.Contracts;
using Toki.Purchasing.Contracts;
using Toki.Purchasing.Domain;
using Toki.SharedKernel;
using Toki.Sync.Contracts;

namespace Toki.Purchasing.Features;

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
        "La lectura de facturas con IA se hace en la nube de Toki: conecte la tienda a la nube para usarla. Mientras, escriba la compra a mano.");
    public static readonly Error Unreachable = Error.Conflict("Purchasing.AiUnreachable", "No se pudo hablar con la nube de Toki. Revise el internet e intente de nuevo.");
    public static readonly Error InvalidFile = Error.Validation("Purchasing.AiInvalidFile", "Suba un PDF, una foto (JPG, PNG o WEBP) o el XML del e-CF, de hasta 4 MB.");

    public static async Task<Result<SupplierInvoiceReading>> AskCloudAsync(ICloudClient cloud, SupplierInvoiceReadRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await cloud.PostAsJsonAsync("/ai/v1/supplier-invoice", request, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<SupplierInvoiceReading>(ct))!;
            var detail = await response.Content.ReadFromJsonAsync<JsonElement>(ct) is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("detail", out var d) ? d.GetString() : null;
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

        // Products bought from this supplier before, by their name in Toki: an exact (or contained) name is a match; the rest the person picks.
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

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/purchasing/purchases/read-invoice", async (Body b, ICloudClient cloud, PurchasingDbContext db, ITaxpayerRegistry registry,
                    ISupplierEcfs ecfs, TimeProvider clock, CancellationToken ct) =>
                {
                    byte[]? content = null;
                    try { content = b.File is null ? null : Convert.FromBase64String(b.File); }
                    catch (FormatException) { }
                    // R10 #11: the type comes from the bytes (as for the attachment), so a file that is none of them never reaches the cloud.
                    if (content is not { Length: > 0 and <= SupplierInvoiceReading.MaxFileBytes } || PurchaseFile.Sniff(content) is not { } type)
                        return EndpointExtensions.Problem(InvalidFile);
                    // TK-240: the supplier's e-CF XML needs no AI (nor the cloud): it is read here, exactly.
                    if (type == PurchaseFile.Xml)
                        return ecfs.Read(Encoding.UTF8.GetString(content)) is { } e
                            ? Results.Ok(await ReviewEcfAsync(db, registry, e, clock, ct, "Tomada del XML del e-CF del suplidor: los montos son los del comprobante."))
                            : EndpointExtensions.Problem(NotAnEcf);
                    var reading = await AskCloudAsync(cloud, new SupplierInvoiceReadRequest(b.File, type, b.FileName), ct);
                    if (reading.IsFailure) return EndpointExtensions.Problem(reading.Error!);
                    // TK-240: when the supplier also sent that e-CF (TK-154), its signed data wins over what the AI read on the paper.
                    var read = reading.Value!;
                    var ncf = new string((read.Ncf ?? "").Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
                    if (Rnc.Normalize(read.SupplierRnc) is { } rnc && Rnc.IsValid(rnc) && ncf.StartsWith('E') && await ecfs.ReceivedAsync(rnc, ncf, ct) is { } received)
                        return Results.Ok(await ReviewEcfAsync(db, registry, received, clock, ct,
                            $"El suplidor ya envió el e-CF {ncf}: se usaron sus datos, no los que leyó la IA."));
                    return Results.Ok(await ReviewAsync(db, registry, read, clock, ct));
                })
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
    }
}
