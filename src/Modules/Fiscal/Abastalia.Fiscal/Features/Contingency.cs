using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Fiscal.Contracts;
using Abastalia.Fiscal.Domain;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Fiscal.Features;

/// <summary>TK-157 (docs/ecf-propio.md §6, Decreto 587-24 arts. 40–43): when the store cannot issue e-CF (no certificate, expired, signing
/// broken) the owner declares the contingency in the OFV and here; while it lasts and the store still cannot sign, every sale takes the next
/// paper NCF of series B. Once it ends, each B receipt is replaced before the DGII by an e-CF with <c>CodigoModificacion</c> 4.</summary>
internal static class PaperContingency
{
    /// <summary>Inside the sale's transaction, like an e-CF: the B number is taken with an atomic update and a rollback gives it back.</summary>
    public static async Task<Result<IssuedDocument>> IssueAsync(FiscalDbContext db, DocumentToIssue doc, DateTime now, CancellationToken ct)
    {
        var today = BusinessDay.Of(now);
        var active = await db.Set<EcfContingency>().Where(c => c.EndedUtc == null).Select(c => new { c.Id, c.StartedUtc }).FirstAsync(ct);
        var contingency = active.Id;
        // R9 #18: series B only within the days the decree allows.
        if (Days(active.StartedUtc, now) >= EcfContingency.MaxDays) return OperationsErrors.OverLimit;
        var candidates = await db.Set<PaperSequence>().Where(s => s.Type == doc.Type && s.Next <= s.To && (s.ExpiresOn == null || s.ExpiresOn >= today))
            .OrderBy(s => s.From).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in candidates)
        {
            if (await db.Set<PaperSequence>().Where(s => s.Id == id && s.Next <= s.To).ExecuteUpdateAsync(s => s.SetProperty(x => x.Next, x => x.Next + 1), ct) != 1)
                continue;
            var number = await db.Set<PaperSequence>().Where(s => s.Id == id).Select(s => s.Next - 1).SingleAsync(ct);
            var p = new PaperDocument
            {
                Id = Guid.CreateVersion7(), Ncf = PaperNcf.Format(doc.Type, number), Type = doc.Type, ReferenceId = doc.ReferenceId, ContingencyId = contingency,
                IssuedUtc = now, Total = doc.Total, Data = JsonSerializer.Serialize(doc),
            };
            db.Set<PaperDocument>().Add(p);
            // No security code, QR nor signing time: a paper receipt has none.
            return new IssuedDocument(p.Id, p.Ncf, "", now, "");
        }
        return OperationsErrors.NoPaperSequence(doc.Type);
    }

    public static int Days(DateTime fromUtc, DateTime nowUtc) => BusinessDay.Of(nowUtc).DayNumber - BusinessDay.Of(fromUtc).DayNumber;
}

/// <summary>What the register and the owner see: whether series B is in use, since when, the B ranges and what still has to be regularized.
/// Any signed-in user may read it (the register shows the banner); the actions need <c>fiscal.manage</c>.</summary>
internal static class ContingencyStatus
{
    public sealed record Range(Guid Id, EcfType Type, string From, string To, string Next, long Available, DateOnly? ExpiresOn);
    public sealed record Pending(Guid Id, string Ncf, EcfType Type, DateTime IssuedUtc, decimal Total);

    /// <param name="Day">Calendar day of the active contingency (1 the first day); the DGII allows <see cref="EcfContingency.MaxDays"/>.</param>
    /// <param name="RegularizeBy">Last day to replace the B receipts with e-CF: 30 calendar days after the contingency ended.</param>
    /// <param name="ClockProblem">TK-156: the main PC's clock is off from the DGII's (the register shows it; sales go on, their e-CF wait).</param>
    public sealed record Response(bool Active, DateTime? StartedUtc, string? Reason, int? Day, int MaxDays, DateTime? LastEndedUtc, DateOnly? RegularizeBy,
        bool CanSign, string? CannotSignReason, IReadOnlyList<Range> Ranges, IReadOnlyList<Pending> ToRegularize, int Regularized, string? ClockProblem = null);

    public static async Task<Response> GetAsync(FiscalDbContext db, FiscalDocuments documents, DateTime now, CancellationToken ct)
    {
        var contingencies = await db.Set<EcfContingency>().AsNoTracking().OrderByDescending(c => c.StartedUtc).Take(20).ToListAsync(ct);
        var active = contingencies.Find(c => c.EndedUtc is null);
        var ended = contingencies.Where(c => c.EndedUtc is not null).Select(c => c.EndedUtc).Max();
        var ranges = (await db.Set<PaperSequence>().AsNoTracking().ToListAsync(ct)).OrderBy(s => s.Type).ThenBy(s => s.From)
            .Select(s => new Range(s.Id, s.Type, PaperNcf.Format(s.Type, s.From), PaperNcf.Format(s.Type, s.To), PaperNcf.Format(s.Type, Math.Min(s.Next, s.To)),
                Math.Max(0, s.To - s.Next + 1), s.ExpiresOn)).ToList();
        var pending = await db.Set<PaperDocument>().AsNoTracking().Where(d => d.RegularizedByENcf == null).OrderBy(d => d.IssuedUtc)
            .Select(d => new Pending(d.Id, d.Ncf, d.Type, d.IssuedUtc, d.Total)).ToListAsync(ct);
        var regularized = await db.Set<PaperDocument>().CountAsync(d => d.RegularizedByENcf != null, ct);
        var why = documents.CannotSign();
        return new Response(active is not null, active?.StartedUtc, active?.Reason, active is null ? null : PaperContingency.Days(active.StartedUtc, now) + 1,
            EcfContingency.MaxDays, ended, ended is { } e && pending.Count > 0 ? BusinessDay.Of(e).AddDays(EcfContingency.RegularizeDays) : null,
            why is null, why, ranges, pending, regularized, documents.ClockProblem);
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/contingency", async (FiscalDbContext db, FiscalDocuments documents, TimeProvider clock, CancellationToken ct) =>
                    Results.Ok(await GetAsync(db, documents, clock.GetUtcNow().UtcDateTime, ct)))
                .RequireAuthorization().WithTags("Fiscal");
    }
}

/// <summary>Starts the contingency: only when the store really cannot sign (never while it can) and with the reason declared in the OFV.</summary>
internal static class StartContingency
{
    public sealed record Command(string Reason) : ICommand<ContingencyStatus.Response>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Reason).NotEmpty().MaximumLength(500).WithMessage("Escriba por qué no se pueden emitir e-CF (lo mismo que declara en la OFV).");
    }

    internal sealed class Handler(FiscalDbContext db, FiscalDocuments documents, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, ContingencyStatus.Response>
    {
        public async Task<Result<ContingencyStatus.Response>> Handle(Command c, CancellationToken ct)
        {
            if (documents.CannotSign() is null) return OperationsErrors.CanStillSign;
            if (await db.Set<EcfContingency>().AnyAsync(x => x.EndedUtc == null, ct)) return OperationsErrors.AlreadyActive;
            var now = clock.GetUtcNow().UtcDateTime;
            var contingency = new EcfContingency { Id = Guid.CreateVersion7(), StartedUtc = now, Reason = c.Reason.Trim() };
            db.Set<EcfContingency>().Add(contingency);
            await audit.RecordAsync("fiscal.contingency-start", "EcfContingency", contingency.Id, new { contingency.Reason, Why = documents.CannotSign() }, ct);
            await db.SaveChangesAsync(ct);
            return await ContingencyStatus.GetAsync(db, documents, now, ct);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/contingency/start", async (Command c, ICommandHandler<Command, ContingencyStatus.Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Ok(r)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>Ends the contingency (the owner declares the exit in the OFV): from here the 30 days to regularize run.</summary>
internal static class EndContingency
{
    public sealed record Command : ICommand<ContingencyStatus.Response>;

    internal sealed class Handler(FiscalDbContext db, FiscalDocuments documents, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, ContingencyStatus.Response>
    {
        public async Task<Result<ContingencyStatus.Response>> Handle(Command c, CancellationToken ct)
        {
            var active = await db.Set<EcfContingency>().FirstOrDefaultAsync(x => x.EndedUtc == null, ct);
            if (active is null) return OperationsErrors.NotActive;
            var now = clock.GetUtcNow().UtcDateTime;
            active.EndedUtc = now;
            var issued = await db.Set<PaperDocument>().CountAsync(d => d.ContingencyId == active.Id, ct);
            await audit.RecordAsync("fiscal.contingency-end", "EcfContingency", active.Id, new { Days = PaperContingency.Days(active.StartedUtc, now) + 1, Issued = issued }, ct);
            await db.SaveChangesAsync(ct);
            return await ContingencyStatus.GetAsync(db, documents, now, ct);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/contingency/end", async (ICommandHandler<Command, ContingencyStatus.Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(), ct)).ToHttp(r => Results.Ok(r)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>Loads a range of paper NCF (series B) authorized by the DGII, ahead of any contingency.</summary>
internal static class LoadPaperSequence
{
    public sealed record Command(EcfType Type, long From, long To, DateOnly? ExpiresOn) : ICommand<LoadSequence.Response>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Type).IsInEnum();
            RuleFor(c => c.From).GreaterThan(0);
            RuleFor(c => c.To).GreaterThanOrEqualTo(c => c.From).LessThanOrEqualTo(99_999_999);
        }
    }

    internal sealed class Handler(FiscalDbContext db, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, LoadSequence.Response>
    {
        public async Task<Result<LoadSequence.Response>> Handle(Command c, CancellationToken ct)
        {
            if (await db.Set<PaperSequence>().AnyAsync(s => s.Type == c.Type && s.From <= c.To && c.From <= s.To, ct)) return FiscalErrors.OverlappingRange;
            var s = new PaperSequence { Id = Guid.CreateVersion7(), Type = c.Type, From = c.From, To = c.To, Next = c.From, ExpiresOn = c.ExpiresOn, CreatedUtc = clock.GetUtcNow().UtcDateTime };
            db.Set<PaperSequence>().Add(s);
            await audit.RecordAsync("fiscal.load-paper-sequence", "PaperSequence", s.Id, c, ct);
            return new LoadSequence.Response(s.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/contingency/sequences", async (Command c, ICommandHandler<Command, LoadSequence.Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/fiscal/contingency/sequences/{r.Id}", r)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>Replaces every B receipt not yet regularized with an e-CF of the same type and data, <c>InformacionReferencia</c> pointing to the B
/// NCF with <c>CodigoModificacion</c> 4. They go to the queue like any e-CF, only to the DGII (the buyer keeps the B receipt). The sale keeps
/// the B number its customer has.</summary>
/// <remarks>shortcut: a 32 under RD$250,000 goes by RFCE like any other; whether the DGII wants the full e-CF for a regularization is §11 #31.</remarks>
internal static class RegularizePaper
{
    public sealed record Command : ICommand<Response>;

    /// <param name="Manual">R9 #11: B03 and B04 left for the owner and the accountant (see <see cref="IsManual"/>).</param>
    public sealed record Response(int Regularized, int Manual);

    /// <summary>R9 #11: a B04 (or B03) already modifies another receipt, and the e-CF that regularizes it would reference the B04 itself
    /// (<c>CodigoModificacion</c> 4) and lose that one. Until the DGII or the accountant say how (docs/ecf-propio.md §11), they are
    /// regularized by hand in the OFV.</summary>
    public static bool IsManual(EcfType type) => type is EcfType.DebitNote33 or EcfType.CreditNote34;

    internal sealed class Handler(FiscalDbContext db, FiscalDocuments documents, ISettingsQueries settings, IAuditLog audit, TimeProvider clock)
        : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await db.Set<EcfContingency>().AnyAsync(x => x.EndedUtc == null, ct)) return OperationsErrors.StillActive;
            if (documents.CannotSign() is { } why) return OperationsErrors.CannotSign(why);
            if (documents.ClockProblem is { } clockOff) return OperationsErrors.ClockOff(clockOff);
            var business = await settings.GetBusinessProfileAsync(ct);
            var all = await db.Set<PaperDocument>().Where(d => d.RegularizedByENcf == null).OrderBy(d => d.IssuedUtc).ToListAsync(ct);
            var pending = all.Where(d => !IsManual(d.Type)).ToList();
            foreach (var p in pending)
            {
                var r = await RegularizeAsync(p, business, ct);
                // All or nothing: a failure rolls back the whole run, so no e-NCF is used up without its e-CF.
                if (r.IsFailure) return Error.Conflict(r.Error!.Code, $"{p.Ncf}: {r.Error.Description}");
                // R12 #3: a second "Regularizar" that read the receipt before this one finds it taken and rolls back the whole run.
                var (encf, at) = (r.Value.ENcf, r.Value.SignedUtc);
                if (await db.Set<PaperDocument>().Where(x => x.Id == p.Id && x.RegularizedByENcf == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.RegularizedByENcf, encf).SetProperty(x => x.RegularizedUtc, at), ct) != 1)
                    return Error.Conflict(FiscalErrors.AlreadyReplaced.Code, $"{p.Ncf}: {FiscalErrors.AlreadyReplaced.Description}");
                await audit.RecordAsync("fiscal.regularize-paper", "PaperDocument", p.Id, new { p.Ncf, ENcf = r.Value.ENcf }, ct);
            }
            return new Response(pending.Count, all.Count - pending.Count);
        }

        private async Task<Result<IssuedDocument>> RegularizeAsync(PaperDocument p, BusinessProfile business, CancellationToken ct)
        {
            var doc = JsonSerializer.Deserialize<DocumentToIssue>(p.Data)!
                with { ModifiedENcf = p.Ncf, ModificationCode = ModificationCode.ContingencyReplacement };
            var now = clock.GetUtcNow().UtcDateTime;
            if (await documents.ReserveAsync(p.Type, BusinessDay.Of(now), ct) is not { } r) return FiscalErrors.NoSequence(p.Type);
            var encf = ENcf.Format(p.Type, r.Number);
            var d = new FiscalDocument
            {
                Id = Guid.CreateVersion7(), ENcf = encf, Type = p.Type, ReferenceId = p.ReferenceId, ModifiedENcf = p.Ncf, BuyerRnc = doc.BuyerRnc,
                BuyerName = doc.BuyerName, Itbis = doc.Itbis, Total = doc.Total, SequenceExpiresOn = r.ExpiresOn, ReplacesENcf = p.Ncf,
            };
            return await documents.SignAndStoreAsync(d, EcfXml.Build(doc, encf, business, now, r.ExpiresOn, BusinessDay.Of(p.IssuedUtc)), business, now, ct);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/contingency/regularize", async (ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(), ct)).ToHttp(r => Results.Ok(r)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}
