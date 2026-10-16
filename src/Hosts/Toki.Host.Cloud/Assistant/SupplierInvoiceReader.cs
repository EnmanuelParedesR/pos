using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Nodes;
using Toki.Purchasing.Contracts;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Assistant;

/// <summary>TK-224: reads a supplier's invoice (PDF or photo) with the chat model and returns its fields as a proposal. The store shows it to a
/// person as a draft purchase; nothing is saved here and the file is not kept.</summary>
/// <remarks>gpt-5-nano takes images and PDFs as input. Cost (gpt-5-nano list price, US$0.05 / 1M input and US$0.40 / 1M output tokens): a page is
/// ~1,500 to 6,000 input tokens and the answer with its reasoning ~1,000 to 5,000 output tokens, so about US$0.001 to US$0.003 per invoice.
/// shortcut: no per-tenant daily limit yet; add one if a store abuses it.</remarks>
public sealed class SupplierInvoiceReader(IChatModel model)
{
    public const string ToolName = "supplier_invoice";

    public static readonly ChatTool Tool = new(ToolName, "Devuelve los datos leídos de la factura del suplidor.", JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "supplierRnc": { "type": "string", "description": "RNC (9 dígitos) o cédula (11) del suplidor que emite la factura, solo dígitos. No el del comprador." },
            "supplierName": { "type": "string", "description": "Razón social o nombre del suplidor." },
            "ncf": { "type": "string", "description": "NCF (B01 + 8 dígitos) o e-NCF (E31 + 10 dígitos) de la factura." },
            "date": { "type": "string", "description": "Fecha de la factura, yyyy-MM-dd." },
            "lines": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "description": { "type": "string" },
                  "quantity": { "type": "number" },
                  "unitCost": { "type": "number", "description": "Precio unitario SIN ITBIS." },
                  "itbisPercent": { "type": "number", "description": "18, 16 o 0 (exento)." }
                },
                "required": ["description"]
              }
            },
            "subtotal": { "type": "number", "description": "Total antes de ITBIS." },
            "itbis": { "type": "number", "description": "ITBIS facturado." },
            "total": { "type": "number", "description": "Total a pagar." },
            "confidence": { "type": "number", "description": "De 0 a 1, qué tan seguro estás de la lectura completa." },
            "doubtful": { "type": "array", "items": { "type": "string", "enum": ["supplierRnc", "ncf", "date", "lines", "total"] }, "description": "Campos que no se leen bien." }
          },
          "required": ["confidence"]
        }
        """)!.AsObject());

    private const string Prompt = """
        Eres un lector de facturas de suplidores de un negocio en República Dominicana.
        Lee la factura adjunta y responde SOLO llamando a la herramienta supplier_invoice. No inventes datos: deja vacío lo que no se lea.
        El RNC es el del suplidor que emite la factura, no el del comprador. Los montos son números sin RD$ ni comas.
        Si la factura trae los precios con ITBIS incluido, calcula el costo unitario sin ITBIS.
        """;

    public async Task<SupplierInvoiceReading?> ReadAsync(ChatAttachment file, CancellationToken ct)
    {
        var reply = await model.CompleteAsync([new("system", Prompt), new("user", "Esta es la factura.", Attachments: [file])], [Tool], ct);
        var json = reply.ToolCalls?.FirstOrDefault(c => c.Name == ToolName)?.Arguments ?? reply.Content;
        try { return json is null ? null : Parse(JsonDocument.Parse(json).RootElement); }
        catch (JsonException) { return null; }
    }

    /// <summary>Tolerant: a number may come as text, a field may be missing; text is cut to the store's column sizes.</summary>
    internal static SupplierInvoiceReading Parse(JsonElement e)
    {
        var lines = e.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Where(x => Text(x, "description", 200) is not null).Take(200)
                .Select(x => new SupplierInvoiceLineReading(Text(x, "description", 200)!, Number(x, "quantity"), Number(x, "unitCost"), Number(x, "itbisPercent"))).ToList()
            : [];
        var doubtful = e.TryGetProperty("doubtful", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(10).ToList()
            : [];
        var date = Text(e, "date", 30) is { } s && DateOnly.TryParseExact(s, ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : (DateOnly?)null;
        return new SupplierInvoiceReading(Text(e, "supplierRnc", 20), Text(e, "supplierName", 150), Text(e, "ncf", 20), date, lines,
            Number(e, "subtotal"), Number(e, "itbis"), Number(e, "total"), Math.Clamp(Number(e, "confidence") ?? 0, 0, 1), doubtful);
    }

    private static string? Text(JsonElement e, string name, int max) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s ? (s.Length > max ? s[..max] : s) : null;

    private static decimal? Number(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var v) ? null
        : v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n) ? n
        : v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString()!.Replace("RD$", "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var t) ? t
        : null;
}

internal static class SupplierInvoiceEndpoint
{
    public static void MapSupplierInvoiceReader(this IEndpointRouteBuilder app) =>
        app.MapPost("/ai/v1/supplier-invoice", async (SupplierInvoiceReadRequest request, ClaimsPrincipal user, IServiceProvider services,
            ILogger<SupplierInvoiceReader> log, CancellationToken ct) =>
        {
            if (services.GetService<IChatModel>() is not { } model) return Unavailable("Ai.NotConfigured", "La lectura de facturas con IA todavía no está configurada en la nube.");
            var type = request.ContentType?.Trim().ToLowerInvariant();
            byte[]? data = null;
            try { data = request.File is null ? null : Convert.FromBase64String(request.File); }
            catch (FormatException) { }
            if (type is null || !SupplierInvoiceReading.ContentTypes.Contains(type) || data is not { Length: > 0 and <= SupplierInvoiceReading.MaxFileBytes })
                return EndpointExtensions.Problem(Error.Validation("Ai.InvalidFile", "Envíe un PDF o una foto (JPG, PNG o WEBP) de hasta 4 MB."));

            var node = NodeIdentity.From(user);
            try
            {
                var reading = await new SupplierInvoiceReader(model).ReadAsync(new ChatAttachment(type, data, Path.GetFileName(request.FileName ?? "factura")), ct);
                // Sizes only: the invoice carries the store's and the supplier's data.
                log.LogInformation("Supplier invoice read for tenant {TenantId}: {Bytes} bytes {Type}, {Lines} lines", node.TenantId, data.Length, type, reading?.Lines.Count);
                return reading is null
                    ? Unavailable("Ai.Unreadable", "No se pudo leer la factura. Intente con una foto más clara o escríbala a mano.")
                    : Results.Ok(reading);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                log.LogWarning(ex, "Chat model call failed reading a supplier invoice");
                return Unavailable("Ai.Unavailable", "La lectura con IA no está disponible ahora. Intente de nuevo en un momento.");
            }
        }).RequireAuthorization(CloudAuth.NodePolicy);

    private static IResult Unavailable(string code, string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status503ServiceUnavailable, title: code, extensions: new Dictionary<string, object?> { ["code"] = code });
}
