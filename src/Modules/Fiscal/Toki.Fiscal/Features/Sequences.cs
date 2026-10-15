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

/// <summary>Loads an e-NCF range authorized by the DGII.</summary>
internal static class LoadSequence
{
    public sealed record Command(EcfType Type, long From, long To, DateOnly ExpiresOn) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Type).IsInEnum();
            RuleFor(c => c.From).GreaterThan(0);
            RuleFor(c => c.To).GreaterThanOrEqualTo(c => c.From).LessThanOrEqualTo(9_999_999_999);
        }
    }

    internal sealed class Handler(FiscalDbContext db, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await db.Sequences.AnyAsync(s => s.Type == c.Type && s.From <= c.To && c.From <= s.To, ct)) return FiscalErrors.OverlappingRange;
            var s = new NcfSequence
            {
                Id = Guid.CreateVersion7(), Type = c.Type, From = c.From, To = c.To, Next = c.From, ExpiresOn = c.ExpiresOn,
                CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.Sequences.Add(s);
            await audit.RecordAsync("fiscal.load-sequence", "NcfSequence", s.Id, c, ct);
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

/// <summary>Loaded ranges with their usage.</summary>
internal static class ListSequences
{
    public sealed record Item(Guid Id, EcfType Type, string From, string To, string Next, long Available, decimal PercentUsed, DateOnly ExpiresOn);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/sequences", async (FiscalDbContext db, CancellationToken ct) =>
                {
                    var sequences = await db.Sequences.AsNoTracking().OrderBy(s => s.Type).ThenBy(s => s.From).ToListAsync(ct);
                    return Results.Ok(sequences.Select(s => new Item(s.Id, s.Type, ENcf.Format(s.Type, s.From), ENcf.Format(s.Type, s.To),
                        ENcf.Format(s.Type, s.Next), Math.Max(0, s.To - s.Next + 1), s.PercentUsed, s.ExpiresOn)));
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>Per receipt type, over the ranges that can still be used: an alert when fewer than 20 % of the numbers are left
/// or the last usable range expires within 30 days. The register shows it as a banner before numbers run out.</summary>
internal static class SequenceAlerts
{
    public sealed record Item(EcfType Type, long Available, DateOnly? ExpiresOn, bool Alert);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/alerts", async (FiscalDbContext db, TimeProvider clock, CancellationToken ct) =>
                {
                    var today = BusinessDay.Of(clock.GetUtcNow().UtcDateTime);
                    var usable = await db.Sequences.Where(s => s.Next <= s.To && s.ExpiresOn >= today).AsNoTracking().ToListAsync(ct);
                    var types = new[] { EcfType.FiscalCredit31, EcfType.Consumer32, EcfType.CreditNote34 };
                    return Results.Ok(types.Select(t =>
                    {
                        var ranges = usable.Where(s => s.Type == t).ToList();
                        var available = ranges.Sum(s => s.To - s.Next + 1);
                        var total = ranges.Sum(s => s.Total);
                        DateOnly? expires = ranges.Count == 0 ? null : ranges.Max(s => s.ExpiresOn);
                        return new Item(t, available, expires, available == 0 || available * 5 < total || expires!.Value.DayNumber - today.DayNumber <= 30);
                    }));
                })
                .RequireAuthorization().WithTags("Fiscal");
    }
}

/// <summary>The send queue at a glance: waiting, past the DGII 72-hour deadline (an infraction), rejected (needs a credit note) and simulated.</summary>
internal static class QueueStatus
{
    public sealed record Response(int Pending, int Overdue, DateTime? NextDeadlineUtc, int Rejected, int Simulated);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/queue", async (FiscalDbContext db, TimeProvider clock, CancellationToken ct) =>
                {
                    var now = clock.GetUtcNow().UtcDateTime;
                    var byStatus = await db.Documents.GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() })
                        .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
                    var pending = db.Documents.Where(d => d.Status == SubmissionStatus.Pending);
                    return Results.Ok(new Response(byStatus.GetValueOrDefault(SubmissionStatus.Pending),
                        await pending.CountAsync(d => d.SubmitDeadlineUtc < now, ct),
                        await pending.MinAsync(d => (DateTime?)d.SubmitDeadlineUtc, ct),
                        byStatus.GetValueOrDefault(SubmissionStatus.Rejected), byStatus.GetValueOrDefault(SubmissionStatus.Simulated)));
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>Issued e-CF, newest first, optionally only those still waiting to be sent.</summary>
internal static class ListDocuments
{
    public sealed record Item(Guid Id, string ENcf, EcfType Type, Guid ReferenceId, decimal Total, decimal Itbis, DateTime SignedUtc, SubmissionStatus Status,
        DateTime SubmitDeadlineUtc, string? LastError);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/documents", async (bool? pendingOnly, int? last, FiscalDbContext db, CancellationToken ct) =>
                {
                    var q = db.Documents.AsNoTracking();
                    if (pendingOnly == true) q = q.Where(d => d.Status == SubmissionStatus.Pending);
                    return Results.Ok(await q.OrderByDescending(d => d.SignedUtc).Take(Math.Clamp(last ?? 100, 1, 1000))
                        .Select(d => new Item(d.Id, d.ENcf, d.Type, d.ReferenceId, d.Total, d.Itbis, d.SignedUtc, d.Status, d.SubmitDeadlineUtc, d.LastError))
                        .ToListAsync(ct));
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}
