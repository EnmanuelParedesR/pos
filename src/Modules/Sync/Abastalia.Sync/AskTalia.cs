using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Security;
using Abastalia.SharedKernel;
using Abastalia.Sync.Contracts;

namespace Abastalia.Sync;

/// <summary>TK-342: Talia in Administración. The store has no AI, so the question goes to the cloud as this node (like reading a supplier's invoice,
/// TK-224) with who is asking (name and permissions, from the session: never the PIN). Nothing is stored here or there; without the cloud,
/// "Talia necesita internet" and the rest of the app works as always (docs/talia-acciones.md §4).</summary>
internal static class AskTalia
{
    public sealed record Request(string? Question, IReadOnlyList<StoreAssistantTurn>? History);

    public const string CloudPath = "/api/nodes/assistant/ask";

    /// <summary>Whoever lands on Administración when signing in (the store app's <c>Session.IsBackOffice</c>): the owner, supervisors, the accountant
    /// and whoever does not sell. A cashier (sales.create without those) does not ask Talia yet (TK-345 brings her to the register).</summary>
    public static bool IsBackOffice(ClaimsPrincipal user)
    {
        bool Has(string p) => user.HasClaim(AbastaliaClaims.Permission, p);
        return user.HasClaim(c => c.Type == AbastaliaClaims.Permission)
            && (Has(Permissions.ReportsView) || Has(Permissions.ShiftsView) || Has(Permissions.FiscalReports) || !Has(Permissions.SalesCreate));
    }

    public static readonly Error NotConnected = Error.Conflict("Talia.NotConnected",
        "Talia necesita internet: esta tienda no está conectada a la nube de Abastalia. Conéctela en Configuración → Nube para preguntarle.");
    public static readonly Error NeedsInternet = Error.Conflict("Talia.NeedsInternet",
        "Talia necesita internet. Revise la conexión e intente de nuevo; el resto de la app sigue funcionando.");
    public static readonly Error Busy = Error.Conflict("Talia.Busy", "Se le hicieron muchas preguntas a Talia en poco tiempo. Espere un minuto e intente de nuevo.");
    public static readonly Error InvalidQuestion = Error.Validation("Talia.InvalidQuestion", "Escriba una pregunta (hasta 1000 letras).");

    /// <summary>TK-343: what Talia's window gets. <paramref name="Proposal"/> is the card the store built from its own data; when it could not
    /// (the category exists, no permission), <paramref name="ProposalError"/> says why instead.</summary>
    public sealed record Response(string Answer, string? Screen, TaliaProposalCard? Proposal = null, string? ProposalError = null);

    public static async Task<Result<Response>> RunAsync(Request r, ClaimsPrincipal user, ICloudClient cloud, TaliaProposals proposals, TimeProvider clock,
        CancellationToken ct)
    {
        var answer = await AskCloudAsync(r, user, cloud, proposals.Declared, clock, ct);
        if (answer.IsFailure) return answer.Error!;
        var a = answer.Value;
        if (a.Proposal is not { } p) return new Response(a.Answer, a.Screen);
        var card = await proposals.ProposeAsync(p, user, ct);
        return card.IsSuccess ? new Response(a.Answer, a.Screen, card.Value) : new Response(a.Answer, a.Screen, null, card.Error!.Description);
    }

    private static async Task<Result<StoreAssistantAnswer>> AskCloudAsync(Request r, ClaimsPrincipal user, ICloudClient cloud, IReadOnlyList<string> actions,
        TimeProvider clock, CancellationToken ct)
    {
        var question = r.Question?.Trim() ?? "";
        if (question.Length is 0 or > 1000) return InvalidQuestion;
        var permissions = user.FindAll(AbastaliaClaims.Permission).Select(c => c.Value).Distinct().ToList();
        var who = new StoreAssistantUser(user.FindFirstValue(AbastaliaClaims.Name), Permissions.All.All(permissions.Contains) ? "Administrador" : null, permissions);
        // What the cloud keeps of it anyway (20 turns, 2,000 letters each), so a long chat never goes over its size limit.
        var history = (r.History ?? []).TakeLast(20).Select(t => t with { Content = t.Content is { Length: > 2000 } c ? c[..2000] : t.Content }).ToList();
        try
        {
            using var response = await cloud.PostAsJsonAsync(CloudPath, new StoreAssistantRequest(question, history, who, BusinessDay.Of(clock.GetUtcNow().UtcDateTime), actions), ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<StoreAssistantAnswer>(ct))!;
            var body = await response.Content.ReadAsStringAsync(ct);
            var detail = body.Length > 0 && JsonSerializer.Deserialize<JsonElement>(body) is { ValueKind: JsonValueKind.Object } p
                && p.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            // The per-minute limit answers 429 with no body; the month's questions answer 429 saying when they renew.
            if (response.StatusCode == HttpStatusCode.TooManyRequests && detail is null) return Busy;
            return Error.Conflict("Talia.Unavailable", detail ?? "Talia no está disponible ahora. Intente de nuevo en un momento.");
        }
        catch (InvalidOperationException) { return NotConnected; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) { return NeedsInternet; }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/assistant/ask", async (Request r, ClaimsPrincipal user, ICloudClient cloud, TaliaProposals proposals, TimeProvider clock,
                    CancellationToken ct) => (await RunAsync(r, user, cloud, proposals, clock, ct)).ToHttp())
                .RequireAuthorization(p => p.RequireClaim(AbastaliaClaims.Session).RequireAssertion(c => IsBackOffice(c.User))).WithTags("Sync");
    }
}
