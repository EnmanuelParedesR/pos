using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Persistence;
using Toki.Host.Cloud.Accounts;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Panel;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Assistant;

public sealed record ChatToolCall(string Id, string Name, string Arguments);

/// <param name="Role">system, user, assistant or tool.</param>
/// <param name="Attachments">Files a user message carries (TK-224: the supplier's invoice as an image or a PDF).</param>
public sealed record ChatMessage(string Role, string? Content, IReadOnlyList<ChatToolCall>? ToolCalls = null, string? ToolCallId = null,
    IReadOnlyList<ChatAttachment>? Attachments = null);

/// <param name="ContentType">image/jpeg, image/png, image/webp or application/pdf.</param>
public sealed record ChatAttachment(string ContentType, byte[] Data, string FileName);

/// <param name="Parameters">JSON schema of the arguments.</param>
public sealed record ChatTool(string Name, string Description, JsonObject Parameters);

/// <summary>The chat model, behind an interface only so tests can fake it.</summary>
public interface IChatModel
{
    Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, CancellationToken ct);
}

public sealed class AzureOpenAIOptions
{
    public const string Section = "AzureOpenAI";
    public string? Endpoint { get; set; }
    public string? Deployment { get; set; }
    /// <remarks>shortcut: API key from Key Vault; switch to the App Service managed identity (Entra token) when convenient.</remarks>
    public string? ApiKey { get; set; }
    public string ApiVersion { get; set; } = "2024-10-21";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Deployment) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>Azure OpenAI Chat Completions with function calling, over plain HTTP.</summary>
internal sealed class AzureOpenAIChatModel(HttpClient http, AzureOpenAIOptions options) : IChatModel
{
    public async Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatTool> tools, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray([.. messages.Select(ToWire)]),
            ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
            })]),
            ["tool_choice"] = "auto",
        };
        var url = $"{options.Endpoint!.TrimEnd('/')}/openai/deployments/{Uri.EscapeDataString(options.Deployment!)}/chat/completions?api-version={options.ApiVersion}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("api-key", options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
        var calls = message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array
            ? tc.EnumerateArray().Select(c => new ChatToolCall(c.GetProperty("id").GetString()!, c.GetProperty("function").GetProperty("name").GetString()!,
                c.GetProperty("function").GetProperty("arguments").GetString() ?? "{}")).ToList()
            : null;
        var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        return new ChatMessage("assistant", content, calls);
    }

    private static JsonNode ToWire(ChatMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.Attachments is { Count: > 0 })
            o["content"] = new JsonArray([
                new JsonObject { ["type"] = "text", ["text"] = m.Content ?? "" },
                .. m.Attachments.Select(a => (JsonNode)(a.ContentType == "application/pdf"
                    ? new JsonObject { ["type"] = "file", ["file"] = new JsonObject { ["filename"] = a.FileName, ["file_data"] = DataUrl(a) } }
                    : new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = DataUrl(a) } })),
            ]);
        if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
        if (m.ToolCalls is { Count: > 0 })
            o["tool_calls"] = new JsonArray([.. m.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            })]);
        return o;
    }

    private static string DataUrl(ChatAttachment a) => $"data:{a.ContentType};base64,{Convert.ToBase64String(a.Data)}";
}

public sealed record AssistantTurn(string? Role, string? Content);
public sealed record AskRequest(string? Question, IReadOnlyList<AssistantTurn>? History);
public sealed record AskResponse(string Answer);

/// <summary>Answers the owner's questions with read-only tools over the replica. The tenant comes from the token and is never a tool argument.</summary>
/// <param name="showCosts">TK-201: false for a member who may not see costs; top_products then has no profit.</param>
public sealed class OwnerAssistant(IChatModel model, PanelQueries queries, bool showCosts = true)
{
    public const int MaxDays = 92;
    private const int MaxRounds = 5;
    private const int MaxHistory = 20;

    public static readonly IReadOnlyList<ChatTool> Tools =
    [
        Tool("sales_by_day", "Ventas por día de la tienda: cantidad de ventas, total vendido, descuentos, ITBIS, devoluciones, neto y anuladas."),
        Tool("top_products", "Productos más vendidos en el rango, por monto vendido (con cantidad, su unidad: Pound = libras, Unit = unidades, y ganancia estimada).", withLimit: true),
        Tool("shift_differences", "Turnos cerrados (cierres Z) con efectivo esperado, contado y diferencia (negativa = faltante, positiva = sobrante), por cajero y caja."),
        Tool("sales_by_cashier", "Ventas por cajero: cantidad, total y anuladas."),
        Tool("payment_mix", "Cómo pagaron los clientes: efectivo, tarjeta, transferencia, fiado o saldo a favor, con cantidad y monto."),
        new("sync_status", "Hora del último dato recibido de cada tienda y si hay datos pendientes de procesar.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        new("receivables", "Cuentas por cobrar (fiado) de hoy: total que deben los clientes, cuántos deben y los que más deben, con su nombre (customerName) cuando la tienda lo envió.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["storeId"] = new JsonObject { ["type"] = "string", ["description"] = "Id de una tienda; vacío para todas." } },
            }),
    ];

    public async Task<string> AskAsync(Guid tenantId, string question, IReadOnlyList<AssistantTurn> history, CancellationToken ct)
    {
        var stores = await queries.StoreNamesAsync(tenantId, ct);
        var messages = new List<ChatMessage> { new("system", SystemPrompt(queries.Today, stores)) };
        messages.AddRange(history.Where(h => h.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(h.Content)).TakeLast(MaxHistory)
            .Select(h => new ChatMessage(h.Role!, h.Content!.Length > 2000 ? h.Content[..2000] : h.Content)));
        messages.Add(new("user", question));

        for (var round = 0; round < MaxRounds; round++)
        {
            var reply = await model.CompleteAsync(messages, Tools, ct);
            if (reply.ToolCalls is not { Count: > 0 }) return string.IsNullOrWhiteSpace(reply.Content) ? "No tengo una respuesta para eso." : reply.Content.Trim();
            messages.Add(reply);
            foreach (var call in reply.ToolCalls) messages.Add(new("tool", await RunToolAsync(tenantId, call, ct), ToolCallId: call.Id));
        }
        return "No pude completar la respuesta. Intenta con una pregunta más concreta.";
    }

    /// <summary>Runs one tool for the caller's tenant and returns its JSON result (or a Spanish error the model can relay).</summary>
    public async Task<string> RunToolAsync(Guid tenantId, ChatToolCall call, CancellationToken ct)
    {
        if (call.Name == "sync_status") return Serialize(await queries.SyncStatusAsync(tenantId, ct));

        JsonElement args;
        try { args = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments).RootElement; }
        catch (JsonException) { return Failure("Los argumentos no son JSON válido."); }

        var today = queries.Today;
        var to = Date(args, "to") ?? today;
        var from = Date(args, "from") ?? to;
        if (from > to || to.DayNumber - from.DayNumber >= MaxDays) return Failure($"Rango de fechas no válido: máximo {MaxDays} días, 'from' antes de 'to'.");
        Guid? storeId = args.TryGetProperty("storeId", out var s) && Guid.TryParse(s.GetString(), out var id) ? id : null;
        var f = new PanelFilter(tenantId, storeId, from, to);

        return call.Name switch
        {
            "receivables" => Serialize(await queries.ReceivablesAsync(tenantId, storeId, 10, ct)),
            "sales_by_day" => Serialize(await queries.SalesByDayAsync(f, ct)),
            "top_products" => Serialize(await queries.TopProductsAsync(f, args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n) ? n : 10, showCosts, ct)),
            "shift_differences" => Serialize(await queries.ShiftsAsync(f, onlyDifferences: false, ct)),
            "sales_by_cashier" => Serialize(await queries.SalesByCashierAsync(f, ct)),
            "payment_mix" => Serialize(await queries.PaymentMixAsync(f, ct)),
            _ => Failure($"No existe la herramienta {call.Name}."),
        };
    }

    private static string SystemPrompt(DateOnly today, Dictionary<Guid, string> stores)
    {
        var es = CultureInfo.GetCultureInfo("es-DO");
        var list = stores.Count == 0 ? "(todavía no hay tiendas)" : string.Join("; ", stores.Select(s => $"{s.Value} (storeId {s.Key})"));
        return $"""
            Eres el asistente de Toki para el dueño de una carnicería en República Dominicana.
            Contesta en español, breve y claro.
            Usa solo los datos que devuelven las herramientas: nunca inventes cifras. Si no hay datos para lo que te preguntan, dilo.
            Escribe los montos así: RD$ 1,234.50.
            Las fechas son días de la tienda (hora de República Dominicana, UTC−4). Hoy es {today.ToString("dddd d 'de' MMMM 'de' yyyy", es)} ({today:yyyy-MM-dd}).
            Cada consulta cubre como máximo {MaxDays} días. Sin storeId se suman todas las tiendas.
            Tiendas: {list}.
            Nombra a los cajeros y las cajas por su nombre (cashierName, registerName). Si el nombre viene vacío, la tienda todavía no lo envió: di "un cajero" o "una caja", nunca el id.
            """;
    }

    private static ChatTool Tool(string name, string description, bool withLimit = false)
    {
        var properties = new JsonObject
        {
            ["from"] = new JsonObject { ["type"] = "string", ["description"] = "Primer día, yyyy-MM-dd." },
            ["to"] = new JsonObject { ["type"] = "string", ["description"] = "Último día (incluido), yyyy-MM-dd." },
            ["storeId"] = new JsonObject { ["type"] = "string", ["description"] = "Id de una tienda; vacío para todas." },
        };
        if (withLimit) properties["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Cuántos productos (1 a 50, por defecto 10)." };
        return new(name, description, new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray("from", "to") });
    }

    private static DateOnly? Date(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json.Options);

    private static string Failure(string message) => JsonSerializer.Serialize(new { error = message });
}

internal static class AssistantEndpoints
{
    public static void MapAssistant(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/assistant/ask", async (AskRequest request, ClaimsPrincipal user, IServiceProvider services, ILogger<OwnerAssistant> log, CancellationToken ct) =>
        {
            var model = services.GetService<IChatModel>();
            if (model is null) return Unavailable("Assistant.NotConfigured", "El asistente todavía no está configurado.");
            var question = request.Question?.Trim() ?? "";
            if (question.Length is 0 or > 1000)
                return EndpointExtensions.Problem(Error.Validation("Assistant.InvalidQuestion", "Escribe una pregunta (hasta 1000 letras)."));

            try
            {
                var assistant = new OwnerAssistant(model, services.GetRequiredService<PanelQueries>(),
                    await PanelAccess.CanViewCostsAsync(user, services.GetRequiredService<CloudDbContext>(), ct));
                return Results.Ok(new AskResponse(await assistant.AskAsync(user.GetTenantId(), question, request.History ?? [], ct)));
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // The question is not logged: it can carry personal data.
                log.LogWarning(ex, "Chat model call failed");
                return Unavailable("Assistant.Unavailable", "El asistente no está disponible ahora. Intenta de nuevo en un momento.");
            }
        }).RequireAuthorization(CloudAuth.PanelPolicy);

    private static IResult Unavailable(string code, string detail) =>
        Results.Problem(detail: detail, statusCode: StatusCodes.Status503ServiceUnavailable, title: code, extensions: new Dictionary<string, object?> { ["code"] = code });
}
