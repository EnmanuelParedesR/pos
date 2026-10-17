using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Security;
using Abastalia.SharedKernel;
using Abastalia.Sync.Contracts;

namespace Abastalia.Sync;

/// <summary>TK-343: Proposed until the person clicks; Running while the endpoint runs it; then Done or Failed. Cancelled and Expired never ran.</summary>
internal enum TaliaProposalStatus { Proposed, Running, Done, Failed, Cancelled, Expired }

/// <summary>TK-343: one store action Talia proposed (<c>sync.TaliaProposals</c>), the store's "done once" table: the id is the idempotency key.
/// The request is the one the store built from its own data; the question is not kept.</summary>
internal sealed class TaliaProposal
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Type { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string Body { get; set; } = "{}";
    public Guid ResourceId { get; set; }
    public TaliaProposalStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? DoneUtc { get; set; }
    /// <summary>The endpoint's status code and, when it succeeded, its JSON: a second click gets the same answer without running it again.</summary>
    public int? ResultStatus { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
}

internal sealed class TaliaProposalConfiguration : IEntityTypeConfiguration<TaliaProposal>
{
    public void Configure(EntityTypeBuilder<TaliaProposal> b)
    {
        b.ToTable("TaliaProposals");
        b.Property(p => p.Type).HasMaxLength(60);
        b.Property(p => p.Summary).HasMaxLength(500);
        b.Property(p => p.Method).HasMaxLength(10);
        b.Property(p => p.Path).HasMaxLength(300);
        b.Property(p => p.Body).HasMaxLength(4000);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(p => p.Result).HasMaxLength(4000);
        b.Property(p => p.Error).HasMaxLength(500);
        b.HasIndex(p => new { p.UserId, p.CreatedUtc });
    }
}

/// <summary>What the card in Talia's window gets: the store's summary and the exact request its Confirmar sends (with the proposal header).</summary>
public sealed record TaliaProposalCard(Guid Id, string Type, string Summary, DateTime ExpiresAt, string Method, string Path, JsonObject Body, Guid ResourceId);

/// <summary>TK-343: the store's executor (docs/talia-acciones.md §3 and §4): a proposal from the cloud becomes a card with a summary written here from
/// local data; the person's click calls the action's own endpoint with their session; <see cref="AllowAssistantExtensions.AllowAssistant"/> runs it
/// at most once through <see cref="IAssistantProposals"/>.</summary>
internal sealed class TaliaProposals(SyncDbContext db, IEnumerable<IAssistantAction> actions, IUserContext me, TimeProvider clock) : IAssistantProposals
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static readonly Error NotFound = Error.NotFound("Talia.ProposalNotFound", "No encontramos esa propuesta.");
    public static readonly Error Expired = Error.Conflict("Talia.ProposalExpired", "La propuesta venció. Pídasela a Talia otra vez.");
    public static readonly Error Cancelled = Error.Conflict("Talia.ProposalCancelled", "Esa propuesta se canceló.");
    public static readonly Error Running = Error.Conflict("Talia.ProposalRunning", "Esa propuesta ya se está haciendo.");
    public static readonly Error Mismatch = Error.Forbidden("Talia.ProposalMismatch", "Esa propuesta no es para esto.");

    /// <summary>The store actions this store can run: an executor here and a type the shared list knows. Sent to the cloud with each question.</summary>
    public IReadOnlyList<string> Declared => [.. actions.Select(a => a.Type).Where(t => StoreActions.All.Any(s => s.Type == t)).Distinct()];

    /// <summary>The cloud's proposal checked again here (the model's arguments are data): a declared type, the user's permissions, the closed schema;
    /// then the action resolves it with local data and the card is stored for 10 minutes.</summary>
    public async Task<Result<TaliaProposalCard>> ProposeAsync(StoreProposal p, ClaimsPrincipal user, CancellationToken ct)
    {
        var spec = StoreActions.All.FirstOrDefault(s => s.Type == p.Type);
        var action = actions.FirstOrDefault(a => a.Type == p.Type);
        if (spec is null || action is null) return Error.Validation("Talia.UnknownAction", "Talia propuso algo que esta tienda no hace.");
        if (!spec.Permissions.All(x => user.HasClaim(AbastaliaClaims.Permission, x)))
            return Error.Forbidden("Talia.NotAllowed", "Usted no tiene permiso para hacer esto. Pídaselo al administrador.");
        if (ToolSchema.Error("propose_" + spec.Type, spec.Parameters, JsonSerializer.SerializeToElement(p.Arguments)) is { } invalid)
            return Error.Validation("Talia.InvalidProposal", invalid);
        var preview = await action.PreviewAsync(p.Arguments, ct);
        if (preview.IsFailure) return preview.Error!;
        var v = preview.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var row = new TaliaProposal
        {
            Id = Guid.CreateVersion7(), UserId = me.UserId, Type = spec.Type, Summary = v.Summary, Method = v.Method, Path = v.Path,
            Body = v.Body.ToJsonString(), ResourceId = v.ResourceId, Status = TaliaProposalStatus.Proposed, CreatedUtc = now, ExpiresUtc = now + Lifetime,
        };
        db.TaliaProposals.Add(row);
        await db.SaveChangesAsync(ct);
        return Card(row);
    }

    public async Task<IResult?> ClaimAsync(Guid id, IReadOnlyList<string> types, string method, string path, JsonNode? body, CancellationToken ct)
    {
        var row = await FindAsync(id, ct);
        if (row is null) return EndpointExtensions.Problem(NotFound);
        if (!types.Contains(row.Type) || !string.Equals(row.Method, method, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase) || !JsonNode.DeepEquals(JsonNode.Parse(row.Body), body))
            return EndpointExtensions.Problem(Mismatch);
        if (Stored(row) is { } stored) return stored;
        var now = clock.GetUtcNow().UtcDateTime;
        if (await ExpireAsync(row, now, ct) is { } expired) return expired;
        // One click wins: only the request that moves it out of Proposed runs it (at most once: a crash after this leaves it Running, never run twice).
        if (await db.TaliaProposals.Where(p => p.Id == id && p.Status == TaliaProposalStatus.Proposed && p.ExpiresUtc > now)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, TaliaProposalStatus.Running), ct) == 1)
            return null;
        return Stored((await FindAsync(id, ct))!) ?? EndpointExtensions.Problem(Running);
    }

    public Task FinishAsync(Guid id, AssistantOutcome outcome, int status, string? result, CancellationToken ct) => outcome == AssistantOutcome.Retry
        // A supervisor must allow it: open again for the retry with the approval (the 10 minutes still count).
        ? db.TaliaProposals.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, TaliaProposalStatus.Proposed), ct)
        : db.TaliaProposals.Where(p => p.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.Status, outcome == AssistantOutcome.Done ? TaliaProposalStatus.Done : TaliaProposalStatus.Failed)
            .SetProperty(p => p.ResultStatus, status)
            .SetProperty(p => p.Result, outcome == AssistantOutcome.Done ? result : null)
            .SetProperty(p => p.Error, outcome == AssistantOutcome.Done ? null : Cut(result))
            .SetProperty(p => p.DoneUtc, clock.GetUtcNow().UtcDateTime), ct);

    public async Task<Result<Ok>> CancelAsync(Guid id, CancellationToken ct)
    {
        var row = await FindAsync(id, ct);
        if (row is null) return NotFound;
        if (row.Status == TaliaProposalStatus.Cancelled) return Ok.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        if (row.Status == TaliaProposalStatus.Proposed && row.ExpiresUtc <= now) return await ExpireAsync(row, now, ct) is null ? Ok.Value : Expired;
        if (await db.TaliaProposals.Where(p => p.Id == id && p.Status == TaliaProposalStatus.Proposed)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, TaliaProposalStatus.Cancelled).SetProperty(p => p.DoneUtc, now), ct) == 1)
            return Ok.Value;
        return Error.Conflict("Talia.ProposalClosed", "Esa propuesta ya se confirmó.");
    }

    /// <summary>Only the user who asked: anyone else gets the same 404 as an id that does not exist.</summary>
    private Task<TaliaProposal?> FindAsync(Guid id, CancellationToken ct) =>
        db.TaliaProposals.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.UserId == me.UserId, ct);

    /// <summary>A proposal already decided answers as it did: the endpoint's result, or why it failed. Null while it is still open.</summary>
    private static IResult? Stored(TaliaProposal row) => row.Status switch
    {
        TaliaProposalStatus.Done => row.Result is null
            ? Results.StatusCode(row.ResultStatus ?? StatusCodes.Status204NoContent)
            : Results.Content(row.Result, "application/json", statusCode: row.ResultStatus),
        TaliaProposalStatus.Failed => EndpointExtensions.Problem(Error.Conflict("Talia.ProposalFailed", row.Error ?? "No se pudo hacer.")),
        TaliaProposalStatus.Cancelled => EndpointExtensions.Problem(Cancelled),
        TaliaProposalStatus.Expired => EndpointExtensions.Problem(Expired),
        _ => null,
    };

    /// <summary>Proposals expire lazily: the first click after the 10 minutes marks it.</summary>
    private async Task<IResult?> ExpireAsync(TaliaProposal row, DateTime now, CancellationToken ct)
    {
        if (row.Status != TaliaProposalStatus.Proposed || row.ExpiresUtc > now) return null;
        await db.TaliaProposals.Where(p => p.Id == row.Id && p.Status == TaliaProposalStatus.Proposed)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, TaliaProposalStatus.Expired), ct);
        return EndpointExtensions.Problem(Expired);
    }

    private static string? Cut(string? text) => text is { Length: > 500 } t ? t[..500] : text;

    private static TaliaProposalCard Card(TaliaProposal r) =>
        new(r.Id, r.Type, r.Summary, r.ExpiresUtc, r.Method, r.Path, JsonNode.Parse(r.Body)!.AsObject(), r.ResourceId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/assistant/proposals/{id:guid}/cancel", async (Guid id, TaliaProposals proposals, CancellationToken ct) =>
                    (await proposals.CancelAsync(id, ct)).ToHttp(_ => Results.NoContent()))
                .RequireAuthorization(p => p.RequireClaim(AbastaliaClaims.Session)).WithTags("Sync");
    }
}
