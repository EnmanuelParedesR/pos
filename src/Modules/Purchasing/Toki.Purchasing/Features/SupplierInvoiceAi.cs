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
    public sealed record Response(Guid? SupplierId, string? SupplierRnc, string? SupplierName, string? Ncf, DateOnly? Date, IReadOnlyList<Line> Lines,
        decimal? Subtotal, decimal? Itbis, decimal? Total, decimal Confidence, IReadOnlyList<string> Warnings);

    public static readonly Error NotConnected = Error.Conflict("Purchasing.AiNeedsCloud",
        "La lectura de facturas con IA se hace en la nube de Toki: conecte la tienda a la nube para usarla. Mientras, escriba la compra a mano.");
    public static readonly Error Unreachable = Error.Conflict("Purchasing.AiUnreachable", "No se pudo hablar con la nube de Toki. Revise el internet e intente de nuevo.");
    public static readonly Error InvalidFile = Error.Validation("Purchasing.AiInvalidFile", "Suba un PDF o una foto (JPG, PNG o WEBP) de hasta 4 MB.");

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

    private static ItbisRate? Rate(decimal? percent) => percent switch
    {
        18 => ItbisRate.General18,
        16 => ItbisRate.Reduced16,
        0 => ItbisRate.Exempt,
        _ => null,
    };

    private static string Key(string text) =>
        new(text.Normalize(NormalizationForm.FormD).Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal sealed record Body(string? File, string? ContentType, string? FileName);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/purchasing/purchases/read-invoice", async (Body b, ICloudClient cloud, PurchasingDbContext db, ITaxpayerRegistry registry, TimeProvider clock,
                    CancellationToken ct) =>
                {
                    var type = b.ContentType?.Trim().ToLowerInvariant();
                    var size = b.File is null ? 0 : b.File.Length / 4 * 3;
                    if (type is null || !SupplierInvoiceReading.ContentTypes.Contains(type) || size is 0 or > SupplierInvoiceReading.MaxFileBytes)
                        return EndpointExtensions.Problem(InvalidFile);
                    var reading = await AskCloudAsync(cloud, new SupplierInvoiceReadRequest(b.File, type, b.FileName), ct);
                    return reading.IsFailure ? EndpointExtensions.Problem(reading.Error!) : Results.Ok(await ReviewAsync(db, registry, reading.Value!, clock, ct));
                })
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
    }
}
