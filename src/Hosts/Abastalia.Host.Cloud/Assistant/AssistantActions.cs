using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Billing;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Host.Cloud.Quotas;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Assistant;

/// <summary>TK-338: Proposed until the user clicks; Confirmed while it runs; then Done or Failed. Cancelled and Expired never ran.</summary>
public enum AssistantActionStatus { Proposed, Confirmed, Done, Failed, Cancelled, Expired }

/// <summary>TK-338: one write Talia proposed (<c>cloud.AssistantActions</c>), the cloud side of the audit (docs/talia-acciones.md §3). The question is not kept.</summary>
public sealed class AssistantAction
{
    /// <summary>The idempotency key: confirming runs the action at most once per id.</summary>
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string UserId { get; set; } = "";
    /// <summary><see cref="AssistantActionType.Type"/>.</summary>
    public string Type { get; set; } = "";
    /// <summary>The arguments as the server validated and resolved them (JSON), never the model's raw text.</summary>
    public string Arguments { get; set; } = "{}";
    /// <summary>Written by the server from real data (<see cref="AssistantActionType.PrepareAsync"/>).</summary>
    public string Summary { get; set; } = "";
    public AssistantActionStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? DoneUtc { get; set; }
    /// <summary>What the action returned (JSON), shown on the card only: it never goes back to the model.</summary>
    public string? Result { get; set; }
    public string? Error { get; set; }

    internal static void Configure(ModelBuilder builder) => builder.Entity<AssistantAction>(e =>
    {
        e.ToTable("AssistantActions");
        e.Property(a => a.UserId).HasMaxLength(450);
        e.Property(a => a.Type).HasMaxLength(60);
        e.Property(a => a.Arguments).HasMaxLength(4000);
        e.Property(a => a.Summary).HasMaxLength(500);
        e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
        e.Property(a => a.Result).HasMaxLength(4000);
        e.Property(a => a.Error).HasMaxLength(500);
        e.HasIndex(a => new { a.TenantId, a.UserId, a.CreatedUtc });
    });
}

/// <summary>TK-338: what the ask response carries for the confirmation card (TK-340).</summary>
public sealed record ProposalCard(Guid Id, string Summary, DateTime ExpiresAt);

public sealed record AssistantActionResponse(Guid Id, string Type, string Summary, AssistantActionStatus Status, DateTime ExpiresAt, DateTime? DoneAt,
    JsonElement? Result, string? Error);

/// <summary>Who the action runs for: always the signed-in user and their tenant, never an argument.</summary>
public sealed record ActionContext(ClaimsPrincipal User, CloudDbContext Db, IServiceProvider Services)
{
    public Guid TenantId => User.GetTenantId();
    public string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
}

/// <summary>TK-338: a write Talia may propose. The model only gets <see cref="Tool"/> (a closed schema); the server validates, writes the summary from
/// real data and, after the user's click, checks the target endpoint's policies again and executes. TK-341 adds the real list.</summary>
public abstract class AssistantActionType
{
    /// <summary>Stable code, stored in <see cref="AssistantAction.Type"/>; the model's tool is <c>propose_&lt;Type&gt;</c>.</summary>
    public abstract string Type { get; }
    public abstract string Description { get; }
    /// <summary>JSON schema with <c>additionalProperties: false</c>: string (with enum or maxLength), boolean and integer properties.</summary>
    public abstract JsonObject Parameters { get; }
    /// <summary>The authorization policies of the endpoint this action stands for, checked when offered and again on confirm.</summary>
    public abstract IReadOnlyList<string> Policies { get; }

    public ChatTool Tool => new("propose_" + Type, Description, Parameters);

    /// <summary>Resolves the (schema-valid) arguments with the server's own data and writes the summary the card shows.</summary>
    public abstract Task<Result<(JsonObject Arguments, string Summary)>> PrepareAsync(ActionContext ctx, JsonObject args, CancellationToken ct);

    /// <summary>Does it, inside the confirmation's transaction; returns what the card shows (never sent to the model).</summary>
    public abstract Task<Result<object?>> ExecuteAsync(ActionContext ctx, JsonObject args, CancellationToken ct);

    /// <returns>Why the arguments break the closed schema, or null.</returns>
    public string? SchemaError(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "Los argumentos deben ser un objeto.";
        var properties = Parameters["properties"]!.AsObject();
        foreach (var p in args.EnumerateObject())
        {
            if (properties[p.Name] is not JsonObject spec) return $"{Tool.Name} no tiene el argumento {p.Name}.";
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
            if (!ok) return $"El argumento {p.Name} no es válido para {Tool.Name}.";
        }
        foreach (var name in Parameters["required"]?.AsArray().Select(r => r!.GetValue<string>()) ?? [])
            if (!args.TryGetProperty(name, out _)) return $"Falta el argumento {name}.";
        return null;
    }
}

/// <summary>TK-338: the actions Talia can propose. DI registers each (tests add their own); the evaluation harness reads this list.</summary>
public static class AssistantActionTypes
{
    public static readonly IReadOnlyList<AssistantActionType> Builtin = [new Alerts.AlertPreferenceAction()];
}

/// <summary>TK-338: proposals (from the ask) and the user's click: <c>POST /api/assistant/actions/{id}/confirm</c> and <c>/cancel</c>.</summary>
internal static class AssistantActionEndpoints
{
    /// <summary>Per user: <c>Ai:AssistantConfirmationsPerMinutePerUser</c> (10), confirm and cancel together.</summary>
    public const string RateLimit = "assistant-confirm";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The license function <see cref="Feature.AssistantActions"/> of the tenant's plan (on in every plan during the pilot).</summary>
    public static bool FeatureOn(IServiceProvider services, Guid tenantId)
    {
        var (plan, addOns) = services.GetRequiredService<TenantPlans>().Of(tenantId);
        return CloudLicenses.Features(services.GetRequiredService<IConfiguration>(), services.GetRequiredService<BillingCatalog>(), plan, addOns)
            .Contains(nameof(Feature.AssistantActions));
    }

    /// <summary>What this user may propose now: the function on and every policy of each action's endpoint. Fewer tools, fewer impossible proposals;
    /// the confirmation checks again anyway.</summary>
    public static async Task<IReadOnlyList<AssistantActionType>> OfferedAsync(HttpContext http)
    {
        if (!FeatureOn(http.RequestServices, http.User.GetTenantId())) return [];
        var offered = new List<AssistantActionType>();
        foreach (var a in http.RequestServices.GetServices<AssistantActionType>())
            if (await AllowedAsync(http, a)) offered.Add(a);
        return offered;
    }

    private static async Task<bool> AllowedAsync(HttpContext http, AssistantActionType action)
    {
        var auth = http.RequestServices.GetRequiredService<IAuthorizationService>();
        foreach (var policy in action.Policies)
            if (!(await auth.AuthorizeAsync(http.User, http, policy)).Succeeded) return false;
        return true;
    }

    /// <summary>Stores the proposal (Proposed, 10 minutes) once the action resolved its arguments and wrote the summary.</summary>
    public static async Task<Result<ProposalCard>> ProposeAsync(HttpContext http, AssistantActionType action, JsonObject args, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<CloudDbContext>();
        var ctx = new ActionContext(http.User, db, http.RequestServices);
        var prepared = await action.PrepareAsync(ctx, args, ct);
        if (prepared.IsFailure) return prepared.Error!;
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
        var row = new AssistantAction
        {
            Id = Guid.CreateVersion7(), TenantId = ctx.TenantId, UserId = ctx.UserId, Type = action.Type, Arguments = prepared.Value.Arguments.ToJsonString(),
            Summary = prepared.Value.Summary, Status = AssistantActionStatus.Proposed, CreatedUtc = now, ExpiresUtc = now + Lifetime,
        };
        db.AssistantActions.Add(row);
        await db.SaveChangesAsync(ct);
        return new ProposalCard(row.Id, row.Summary, row.ExpiresUtc);
    }

    public static void MapAssistantActions(this IEndpointRouteBuilder app)
    {
        var actions = app.MapGroup("/api/assistant/actions").RequireAuthorization(CloudAuth.PanelPolicy).RequireRateLimiting(RateLimit);
        actions.MapPost("/{id:guid}/confirm", ConfirmAsync);
        actions.MapPost("/{id:guid}/cancel", CancelAsync);
    }

    private static async Task<IResult> ConfirmAsync(Guid id, HttpContext http, CloudDbContext db, TimeProvider clock, ILogger<OwnerAssistant> log, CancellationToken ct)
    {
        var ctx = new ActionContext(http.User, db, http.RequestServices);
        var row = await FindAsync(db, id, ctx, ct);
        if (row is null) return NotFound();
        if (row.Status is AssistantActionStatus.Done or AssistantActionStatus.Failed) return Results.Ok(Response(row)); // a second click: the stored result
        var now = clock.GetUtcNow().UtcDateTime;
        if (await ExpireAsync(db, row, now, ct) is { } expired) return expired;
        if (row.Status != AssistantActionStatus.Proposed) return Closed(row.Status);
        if (!FeatureOn(http.RequestServices, ctx.TenantId))
            return EndpointExtensions.Problem(Error.Forbidden("Assistant.ActionsOff", "Las acciones de Talia no están incluidas en su plan."));

        var action = http.RequestServices.GetServices<AssistantActionType>().FirstOrDefault(a => a.Type == row.Type);
        if (action is null) return await RefuseAsync(db, row, log, Error.Conflict("Assistant.UnknownAction", "Esa acción ya no existe."), ct);
        // The same policies as the endpoint it stands for, checked now: a role changed since the proposal counts.
        if (!await AllowedAsync(http, action))
            return await RefuseAsync(db, row, log, Error.Forbidden("Assistant.NotAllowed", "Usted ya no tiene permiso para hacer esto."), ct);

        // One click wins: only the request that moves it out of Proposed runs it (at most once; a crash after this leaves it Confirmed, never run twice).
        if (await db.AssistantActions.Where(a => a.Id == id && a.Status == AssistantActionStatus.Proposed && a.ExpiresUtc > now)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AssistantActionStatus.Confirmed), ct) == 0)
            return await FindAsync(db, id, ctx, ct) is { Status: AssistantActionStatus.Done or AssistantActionStatus.Failed } done ? Results.Ok(Response(done)) : Closed(AssistantActionStatus.Confirmed);

        Result<object?> result;
        try
        {
            result = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var r = await action.ExecuteAsync(ctx, JsonNode.Parse(row.Arguments)!.AsObject(), ct);
                if (r.IsFailure) return r;
                await FinishAsync(db, id, AssistantActionStatus.Done, JsonSerializer.Serialize(r.Value, Json.Options), null, now, ct);
                await tx.CommitAsync(ct);
                return r;
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Assistant action {ActionId} ({Type}) failed via talia", id, row.Type);
            result = Error.Validation("Assistant.ActionFailed", "No se pudo completar. Inténtelo de nuevo desde su pantalla.");
        }
        if (result.IsFailure)
        {
            db.ChangeTracker.Clear(); // nothing the executor left half-done is saved
            await FinishAsync(db, id, AssistantActionStatus.Failed, null, result.Error!.Description, now, ct);
        }
        row = (await FindAsync(db, id, ctx, ct))!;
        log.LogInformation("Assistant action {ActionId} ({Type}) {Status} for user {UserId} of tenant {TenantId} via talia", id, row.Type, row.Status, ctx.UserId, ctx.TenantId);
        return Results.Ok(Response(row));
    }

    private static async Task<IResult> CancelAsync(Guid id, HttpContext http, CloudDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var ctx = new ActionContext(http.User, db, http.RequestServices);
        var row = await FindAsync(db, id, ctx, ct);
        if (row is null) return NotFound();
        if (row.Status == AssistantActionStatus.Cancelled) return Results.Ok(Response(row));
        var now = clock.GetUtcNow().UtcDateTime;
        if (await ExpireAsync(db, row, now, ct) is { } expired) return expired;
        if (await db.AssistantActions.Where(a => a.Id == id && a.Status == AssistantActionStatus.Proposed)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AssistantActionStatus.Cancelled).SetProperty(a => a.DoneUtc, now), ct) == 0)
            return Closed((await FindAsync(db, id, ctx, ct))!.Status);
        return Results.Ok(Response((await FindAsync(db, id, ctx, ct))!));
    }

    /// <summary>Only the user who asked, in the tenant of their token: anyone else gets the same 404 as an id that does not exist.</summary>
    private static Task<AssistantAction?> FindAsync(CloudDbContext db, Guid id, ActionContext ctx, CancellationToken ct) =>
        db.AssistantActions.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.TenantId == ctx.TenantId && a.UserId == ctx.UserId, ct);

    /// <summary>Proposals expire lazily: the first click after the 10 minutes marks it.</summary>
    private static async Task<IResult?> ExpireAsync(CloudDbContext db, AssistantAction row, DateTime now, CancellationToken ct)
    {
        if (row.Status != AssistantActionStatus.Proposed || row.ExpiresUtc > now) return null;
        await db.AssistantActions.Where(a => a.Id == row.Id && a.Status == AssistantActionStatus.Proposed)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AssistantActionStatus.Expired), ct);
        return Closed(AssistantActionStatus.Expired);
    }

    private static async Task<IResult> RefuseAsync(CloudDbContext db, AssistantAction row, ILogger log, Error error, CancellationToken ct)
    {
        await FinishAsync(db, row.Id, AssistantActionStatus.Failed, null, error.Description, null, ct);
        log.LogInformation("Assistant action {ActionId} ({Type}) refused: {Code} via talia", row.Id, row.Type, error.Code);
        return EndpointExtensions.Problem(error);
    }

    private static Task<int> FinishAsync(CloudDbContext db, Guid id, AssistantActionStatus status, string? result, string? error, DateTime? doneUtc, CancellationToken ct) =>
        db.AssistantActions.Where(a => a.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(a => a.Status, status).SetProperty(a => a.Result, result).SetProperty(a => a.Error, error).SetProperty(a => a.DoneUtc, doneUtc), ct);

    private static IResult Closed(AssistantActionStatus status) => EndpointExtensions.Problem(status switch
    {
        AssistantActionStatus.Expired => Error.Conflict("Assistant.ProposalExpired", "La propuesta venció. Pídasela a Talia otra vez."),
        AssistantActionStatus.Cancelled => Error.Conflict("Assistant.ProposalCancelled", "Esa propuesta se canceló."),
        AssistantActionStatus.Confirmed => Error.Conflict("Assistant.ProposalRunning", "Esa propuesta ya se está haciendo."),
        _ => Error.Conflict("Assistant.ProposalClosed", "Esa propuesta ya se confirmó."),
    });

    private static IResult NotFound() => EndpointExtensions.Problem(Error.NotFound("Assistant.ProposalNotFound", "No encontramos esa propuesta."));

    private static AssistantActionResponse Response(AssistantAction a) => new(a.Id, a.Type, a.Summary, a.Status, a.ExpiresUtc, a.DoneUtc,
        a.Result is null ? null : JsonDocument.Parse(a.Result).RootElement.Clone(), a.Error);
}
