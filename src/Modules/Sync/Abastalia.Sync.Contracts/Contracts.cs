using System.Text.Json;
using System.Text.Json.Nodes;
using Abastalia.SharedKernel;

namespace Abastalia.Sync.Contracts;

/// <summary>The store's main PC says it is alive and how it is used: on start and every hour while connected (TK-190, docs/metricas-producto.md).
/// Counts, flags and totals only: never names, PINs, customers, products or anything that points at a person.</summary>
/// <param name="AppVersion">The store server's version ("1.4.0").</param>
/// <param name="UptimeMinutes">Since the store server (service) started.</param>
/// <param name="ActiveRegisters">Registers with a shift opened in the last 30 store days.</param>
/// <param name="OutboxBacklog">Events not yet confirmed by the cloud.</param>
/// <param name="LastErrorCode">Kind of the last upload failure ("http_503", "unreachable", "other"), never its text; null after a good upload.</param>
/// <param name="Days">Yesterday and today (store days), so yesterday's last hour is not lost.</param>
/// <param name="Fiscal">TK-222, added later: null from stores on older versions (the cloud then sends them no fiscal alert).</param>
/// <param name="License">TK-196: added later, so heartbeats of older versions arrive without it (null).</param>
/// <param name="AuditHead">R11 A10: the head of the store's audit chain ("#seq hash8"), so the cloud keeps how far it went and a rewritten chain shows.</param>
public sealed record NodeHeartbeatV1(
    Guid NodeId, string AppVersion, string Os, int UptimeMinutes, int ActiveRegisters, int OpenShifts, int OutboxBacklog, string? LastErrorCode,
    NodeFeaturesV1 Features, NodeSetupV1 Setup, IReadOnlyList<DailyUsageV1> Days, NodeLicenseV1? License = null, NodeFiscalV1? Fiscal = null,
    string? AuditHead = null) : IntegrationEvent(NodeId);

/// <param name="Status">Trial, Active, Grace or Restricted.</param>
/// <param name="EndsUtc">When the trial ends or the license expires; null for a trial that has not sold yet.</param>
/// <param name="Jws">R11 B-Low8: the installed license as Abastalia signed it (null in a trial or from older versions); the cloud verifies it for the paid extras.</param>
public sealed record NodeLicenseV1(string Status, DateTime? EndsUtc, string? Jws = null);

/// <summary>TK-222: counts and dates the cloud alerts the owner on (no e-NCF, no amounts).</summary>
/// <param name="RejectedWithoutReplacement">e-CF the DGII rejected in the last 30 days still without a replacement.</param>
/// <param name="OldestPendingUtc">Signing time of the oldest e-CF not yet delivered to the DGII.</param>
/// <param name="CertificateExpiresUtc">Expiry of the e-CF certificate; null without one.</param>
public sealed record NodeFiscalV1(int RejectedWithoutReplacement, DateTime? OldestPendingUtc, DateTime? CertificateExpiresUtc);

/// <param name="OnAccount">Some customer owes on credit (fiado in use).</param>
/// <param name="ScaleConfigured">Some active register has a scale (each register reports its own, TK-190); null while none has reported.</param>
/// <param name="PrinterConfigured">Same as <paramref name="ScaleConfigured"/>, for a receipt printer.</param>
/// <param name="FiscalProvider">TK-238: the DGII environment the store sends to now (the one saved in "Comprobantes electrónicos", else appsettings):
/// Simulated, TesteCF, CerteCF or eCF. Stores before TK-238 sent <c>Fiscal:Provider</c> (Simulated or Dgii).</param>
public sealed record NodeFeaturesV1(bool OnAccount, bool? ScaleConfigured, bool? PrinterConfigured, string FiscalProvider);

/// <summary>Whether the store finished its setup (the cloud gets no business profile event). Flags only: the RNC itself never goes.</summary>
public sealed record NodeSetupV1(bool BusinessNameSet, bool RncSet, int ActiveProducts);

/// <param name="Day">Store business day (UTC−4).</param>
/// <param name="SalesAmount">Completed sales, ITBIS included.</param>
/// <param name="Returns">Money returned that day, ITBIS included.</param>
public sealed record DailyUsageV1(DateOnly Day, int Sales, decimal SalesAmount, int ActiveCashiers, int Voids, decimal Returns);

/// <summary>The store's authenticated channel to the cloud (ADR-0008): every call goes as this node, with <c>Authorization: Node &lt;nodeId&gt;.&lt;nodeSecret&gt;</c>.
/// The secret never leaves the Sync module.</summary>
public interface ICloudClient
{
    /// <summary>POSTs <paramref name="body"/> as JSON (camelCase) to <c>{cloudUrl}{path}</c>.</summary>
    /// <exception cref="InvalidOperationException">The store is not connected to the cloud.</exception>
    Task<HttpResponseMessage> PostAsJsonAsync<T>(string path, T body, CancellationToken ct);
}

/// <summary>A module's reference data the cloud cannot learn from past events (names of users and registers created before their events
/// existed). Called inside the connect command: each source writes its current rows to the outbox as its usual change events.</summary>
public interface ICloudSnapshot
{
    Task EnqueueAsync(CancellationToken ct);
}

/// <summary>TK-342: a question to Talia from the store app (POST /api/nodes/assistant/ask, signed as the node). The PIN never goes: the user is who the
/// store's session says, sent as data for the answer's wording and the screens offered; the store keeps checking every permission itself.</summary>
/// <param name="History">The chat so far, user and assistant turns only.</param>
/// <param name="Today">The store's business day.</param>
/// <param name="Actions">TK-343: the <see cref="StoreActions"/> types this store can run (its executor's registry); the cloud offers no others.</param>
public sealed record StoreAssistantRequest(string? Question, IReadOnlyList<StoreAssistantTurn>? History, StoreAssistantUser? User, DateOnly? Today,
    IReadOnlyList<string>? Actions = null);

public sealed record StoreAssistantTurn(string? Role, string? Content);

/// <param name="Role">"Administrador" when the user has every permission; null otherwise (the permissions say what they can do).</param>
public sealed record StoreAssistantUser(string? Name, string? Role, IReadOnlyList<string>? Permissions);

/// <param name="Screen">A route of the store app's screen list (storeScreens.json) Talia suggests opening; the person clicks "Abrir …". Null when none.</param>
/// <param name="Proposal">TK-343: the one store action Talia proposed, as the model asked for it (schema-checked by the cloud). The cloud never runs it:
/// the store resolves it with its own data, writes the summary and runs it with the user's session after their click.</param>
public sealed record StoreAssistantAnswer(string Answer, string? Screen = null, StoreProposal? Proposal = null);

/// <param name="Type">A <see cref="StoreActions"/> type the store declared.</param>
public sealed record StoreProposal(string Type, JsonObject Arguments);

/// <summary>TK-343: one store action Talia may propose (storeActions.json, shared by the cloud and the store).</summary>
/// <param name="Permissions">The store user needs every one (the same as its endpoint); the cloud offers it only then.</param>
/// <param name="Parameters">Closed JSON schema (<c>additionalProperties: false</c>), checked by <see cref="ToolSchema.Error"/> on both sides.</param>
public sealed record StoreActionSpec(string Type, string Description, IReadOnlyList<string> Permissions, JsonObject Parameters);

public static class StoreActions
{
    public static readonly IReadOnlyList<StoreActionSpec> All = JsonSerializer.Deserialize<List<StoreActionSpec>>(
        typeof(StoreActions).Assembly.GetManifestResourceStream("Abastalia.StoreActions.json")!, JsonSerializerOptions.Web)!;

    /// <summary>What may be offered: known here, declared by the store and allowed by every permission of the user.</summary>
    public static IReadOnlyList<StoreActionSpec> For(IEnumerable<string>? declared, IReadOnlySet<string> permissions)
    {
        var types = (declared ?? []).ToHashSet(StringComparer.Ordinal);
        return [.. All.Where(a => types.Contains(a.Type) && a.Permissions.All(permissions.Contains))];
    }
}

/// <summary>TK-338/343: the closed-schema check of a propose_* tool's arguments: string (enum, maxLength), boolean and integer properties only.</summary>
public static class ToolSchema
{
    /// <returns>Why the arguments break the schema, or null.</returns>
    public static string? Error(string tool, JsonObject parameters, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "Los argumentos deben ser un objeto.";
        var properties = parameters["properties"]!.AsObject();
        foreach (var p in args.EnumerateObject())
        {
            if (properties[p.Name] is not JsonObject spec) return $"{tool} no tiene el argumento {p.Name}.";
            var v = p.Value;
            var ok = spec["type"]?.GetValue<string>() switch
            {
                "string" => v.ValueKind == JsonValueKind.String
                    && (spec["maxLength"] is not { } max || v.GetString()!.Length <= max.GetValue<int>())
                    && (spec["enum"] is not JsonArray options || options.Any(o => o!.GetValue<string>() == v.GetString())),
                "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
                _ => false,
            };
            if (!ok) return $"El argumento {p.Name} no es válido para {tool}.";
        }
        foreach (var name in parameters["required"]?.AsArray().Select(r => r!.GetValue<string>()) ?? [])
            if (!args.TryGetProperty(name, out _)) return $"Falta el argumento {name}.";
        return null;
    }
}
