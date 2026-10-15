using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Fiscal.Contracts;
using Toki.Fiscal.Domain;
using Toki.SharedKernel;

namespace Toki.Fiscal.Features;

/// <summary>Loads an e-NCF range authorized by the DGII, for the current environment unless another is given (TK-149).</summary>
internal static class LoadSequence
{
    /// <param name="ExpiresOn">Required for 31 and 33; the 32 and 34 may have none.</param>
    public sealed record Command(EcfType Type, long From, long To, DateOnly? ExpiresOn, FiscalEnvironment? Environment = null) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Type).IsInEnum();
            RuleFor(c => c.Environment).IsInEnum();
            RuleFor(c => c.From).GreaterThan(0);
            RuleFor(c => c.To).GreaterThanOrEqualTo(c => c.From).LessThanOrEqualTo(9_999_999_999);
        }
    }

    internal sealed class Handler(FiscalDbContext db, FiscalDocuments documents, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (c.ExpiresOn is null && c.Type is EcfType.FiscalCredit31 or EcfType.DebitNote33) return FiscalErrors.ExpiryRequired;
            var env = c.Environment ?? documents.Environment;
            if (await db.Sequences.AnyAsync(s => s.Type == c.Type && s.Environment == env && s.From <= c.To && c.From <= s.To, ct)) return FiscalErrors.OverlappingRange;
            var s = new NcfSequence
            {
                Id = Guid.CreateVersion7(), Type = c.Type, Environment = env, From = c.From, To = c.To, Next = c.From, ExpiresOn = c.ExpiresOn,
                CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.Sequences.Add(s);
            await audit.RecordAsync("fiscal.load-sequence", "NcfSequence", s.Id, c with { Environment = env }, ct);
            return new Response(s.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/sequences", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/fiscal/sequences/{r.Id}", r)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>Loaded ranges with their usage, of every environment (the current one first).</summary>
internal static class ListSequences
{
    public sealed record Item(Guid Id, EcfType Type, FiscalEnvironment Environment, bool Current, string From, string To, string Next, long Available,
        decimal PercentUsed, DateOnly? ExpiresOn);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/sequences", async (FiscalDbContext db, FiscalDocuments documents, CancellationToken ct) =>
                {
                    var env = documents.Environment;
                    var sequences = await db.Sequences.AsNoTracking().ToListAsync(ct);
                    return Results.Ok(sequences.OrderBy(s => s.Environment != env).ThenBy(s => s.Environment).ThenBy(s => s.Type).ThenBy(s => s.From)
                        .Select(s => new Item(s.Id, s.Type, s.Environment, s.Environment == env, ENcf.Format(s.Type, s.From), ENcf.Format(s.Type, s.To),
                            ENcf.Format(s.Type, s.Next), Math.Max(0, s.To - s.Next + 1), s.PercentUsed, s.ExpiresOn)));
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>Per receipt type, over the ranges of the current environment that can still be used: an alert when fewer than 20 % of the numbers
/// are left or the last usable range expires within 30 days. The register shows it as a banner before numbers run out.</summary>
internal static class SequenceAlerts
{
    public sealed record Item(EcfType Type, long Available, DateOnly? ExpiresOn, bool Alert);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/alerts", async (FiscalDbContext db, FiscalDocuments documents, TimeProvider clock, CancellationToken ct) =>
                {
                    var today = BusinessDay.Of(clock.GetUtcNow().UtcDateTime);
                    var env = documents.Environment;
                    var usable = await db.Sequences.Where(s => s.Environment == env && s.Next <= s.To && (s.ExpiresOn == null || s.ExpiresOn >= today))
                        .AsNoTracking().ToListAsync(ct);
                    var types = new[] { EcfType.FiscalCredit31, EcfType.Consumer32, EcfType.CreditNote34 };
                    return Results.Ok(types.Select(t =>
                    {
                        var ranges = usable.Where(s => s.Type == t).ToList();
                        var available = ranges.Sum(s => s.To - s.Next + 1);
                        var total = ranges.Sum(s => s.Total);
                        // A range without expiry (32 and 34 may have none) never expires.
                        var expires = ranges.Count == 0 || ranges.Any(s => s.ExpiresOn is null) ? null : ranges.Max(s => s.ExpiresOn);
                        return new Item(t, available, expires, available == 0 || available * 5 < total || expires?.DayNumber - today.DayNumber <= 30);
                    }));
                })
                .RequireAuthorization().WithTags("Fiscal");
    }
}

/// <summary>The send queue at a glance: waiting, sent and waiting for the DGII result, past the 72-hour deadline (an infraction),
/// rejected without a replacement yet (TK-147) and simulated.</summary>
internal static class QueueStatus
{
    public sealed record Response(FiscalEnvironment Environment, int Pending, int Sent, int Overdue, DateTime? NextDeadlineUtc, int Rejected, int Simulated);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/queue", async (FiscalDbContext db, FiscalDocuments documents, TimeProvider clock, CancellationToken ct) =>
                {
                    var now = clock.GetUtcNow().UtcDateTime;
                    var byStatus = await db.Documents.GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() })
                        .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
                    var pending = db.Documents.Where(d => d.Status == SubmissionStatus.Pending);
                    return Results.Ok(new Response(documents.Environment, byStatus.GetValueOrDefault(SubmissionStatus.Pending),
                        byStatus.GetValueOrDefault(SubmissionStatus.Sent),
                        await pending.CountAsync(d => d.SubmitDeadlineUtc < now, ct),
                        await pending.MinAsync(d => (DateTime?)d.SubmitDeadlineUtc, ct),
                        await db.Documents.CountAsync(d => d.Status == SubmissionStatus.Rejected && d.ReplacedByENcf == null, ct),
                        byStatus.GetValueOrDefault(SubmissionStatus.Simulated)));
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>Issued e-CF, newest first. <c>pendingOnly</c>: still to be sent or waiting for the result; <c>rejectedOnly</c>: the
/// "Rechazados por la DGII" list (rejected and not replaced yet).</summary>
internal static class ListDocuments
{
    public sealed record Item(Guid Id, string ENcf, EcfType Type, Guid ReferenceId, decimal Total, decimal Itbis, DateTime SignedUtc, SubmissionStatus Status,
        DateTime SubmitDeadlineUtc, string? LastError, FiscalEnvironment Environment, EcfChannel Channel, string? TrackId, int? DgiiCode, string? DgiiMessages,
        bool? SequenceUsed, bool Contingency, string? ReplacesENcf, string? ReplacedByENcf);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/documents", async (bool? pendingOnly, bool? rejectedOnly, int? last, FiscalDbContext db, CancellationToken ct) =>
                {
                    var q = db.Documents.AsNoTracking();
                    if (pendingOnly == true) q = q.Where(d => d.Status == SubmissionStatus.Pending || d.Status == SubmissionStatus.Sent);
                    if (rejectedOnly == true) q = q.Where(d => d.Status == SubmissionStatus.Rejected && d.ReplacedByENcf == null);
                    return Results.Ok(await q.OrderByDescending(d => d.SignedUtc).Take(Math.Clamp(last ?? 100, 1, 1000))
                        .Select(d => new Item(d.Id, d.ENcf, d.Type, d.ReferenceId, d.Total, d.Itbis, d.SignedUtc, d.Status, d.SubmitDeadlineUtc, d.LastError,
                            d.Environment, d.Channel, d.TrackId, d.DgiiCode, d.DgiiMessages, d.SequenceUsed, d.Contingency, d.ReplacesENcf, d.ReplacedByENcf))
                        .ToListAsync(ct));
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>TK-147: replaces a document the DGII rejected with a new e-CF (same data, new e-NCF). No credit note: the rejected one never was valid.</summary>
internal static class ReplaceRejected
{
    public sealed record Command(Guid DocumentId) : ICommand<IssuedDocument>;

    internal sealed class Handler(FiscalDocuments documents, IAuditLog audit) : ICommandHandler<Command, IssuedDocument>
    {
        public async Task<Result<IssuedDocument>> Handle(Command c, CancellationToken ct)
        {
            var issued = await documents.ReplaceAsync(c.DocumentId, ct);
            if (issued.IsSuccess) await audit.RecordAsync("fiscal.replace-rejected", "FiscalDocument", c.DocumentId, new { Replacement = issued.Value.ENcf }, ct);
            return issued;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/documents/{id:guid}/replace", async (Guid id, ICommandHandler<Command, IssuedDocument> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id), ct)).ToHttp(r => Results.Created($"/api/fiscal/documents/{r.DocumentId}", r)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}
