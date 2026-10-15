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
using Toki.Settings.Contracts;
using Toki.SharedKernel;

namespace Toki.Fiscal.Features;

/// <summary>TK-150: voids the unused numbers of a range (expired or abandoned) with an ANECF. It goes from the next unused number up to
/// <c>To</c> (default: the end of the range), so a voided number can never be reserved again. Signed here, sent by the queue (works offline).</summary>
internal static class VoidSequence
{
    public sealed record Command(Guid SequenceId, long? To = null) : ICommand<Response>;
    public sealed record Response(Guid Id, string From, string To, long Count);

    internal sealed class Handler(FiscalDbContext db, FiscalDocuments documents, ISettingsQueries settings, IEcfSigner signer, IAuditLog audit, TimeProvider clock)
        : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var env = documents.Environment;
            var s = await db.Sequences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == c.SequenceId && x.Environment == env, ct);
            if (s is null) return FiscalErrors.SequenceNotFound;
            if (s.Next > s.To) return FiscalErrors.NothingToVoid;
            var (from, to) = (s.Next, c.To ?? s.To);
            if (to < from || to > s.To) return FiscalErrors.InvalidVoidRange;
            var business = await settings.GetBusinessProfileAsync(ct);
            if (!Rnc.IsValid(business.Rnc)) return FiscalErrors.MissingIssuerRnc;

            // Moves the range past the voided numbers; a sale that took the next number meanwhile makes this fail instead of voiding a used one.
            if (await db.Sequences.Where(x => x.Id == s.Id && x.Next == from).ExecuteUpdateAsync(x => x.SetProperty(q => q.Next, to + 1), ct) != 1)
                return FiscalErrors.NothingToVoid;

            var now = clock.GetUtcNow().UtcDateTime;
            var xml = EcfXml.Anecf(business.Rnc!, s.Type, from, to, now);
            if (!signer.IsSimulated && EcfXml.ValidateUnsigned(xml, EcfXml.AnecfSchema) is [var first, ..]) return FiscalErrors.InvalidXml(first);
            var v = new EcfVoid
            {
                Id = Guid.CreateVersion7(), SequenceId = s.Id, Type = s.Type, Environment = env, From = from, To = to, CreatedUtc = now,
                SignedXml = EcfSubmissionQueue.Zip(signer.Sign(xml).SignedXml),
                Status = signer.IsSimulated ? SubmissionStatus.Simulated : SubmissionStatus.Pending,
            };
            db.Voids.Add(v);
            var response = new Response(v.Id, ENcf.Format(s.Type, from), ENcf.Format(s.Type, to), to - from + 1);
            await audit.RecordAsync("fiscal.void-sequence", "EcfVoid", v.Id, response, ct);
            return response;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/sequences/{id:guid}/void", async (Guid id, Body? body, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, body?.To), ct)).ToHttp(r => Results.Created($"/api/fiscal/voids/{r.Id}", r)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }

    public sealed record Body(long? To);
}

/// <summary>TK-205: voids an e-CF that was signed but never left the store. The DGII "Anulación de e-NCF" covers unused numbers and e-CF
/// signed but not sent to the DGII nor to the buyer (docs/ecf-propio.md §2.5): the same ANECF, for that single number. What already reached the
/// DGII is corrected with a credit note instead. If an earlier send failed, it may have arrived anyway: the DGII is asked first (as the queue does
/// before resending). The document becomes <see cref="SubmissionStatus.Voided"/> and the queue never sends it; if the sale stands, "Reemplazar"
/// issues a new e-CF with the same data.</summary>
/// <remarks>shortcut: the receipt the customer already has still shows the voided e-NCF; the store must not hand it over as valid (§11 #26).</remarks>
internal static class VoidDocument
{
    public sealed record Command(Guid DocumentId) : ICommand<VoidSequence.Response>;

    internal sealed class Handler(FiscalDbContext db, ISettingsQueries settings, IEcfSigner signer, IEcfProvider provider, IAuditLog audit, TimeProvider clock)
        : ICommandHandler<Command, VoidSequence.Response>
    {
        public async Task<Result<VoidSequence.Response>> Handle(Command c, CancellationToken ct)
        {
            var d = await db.Documents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == c.DocumentId, ct);
            if (d is null) return FiscalErrors.NotFound;
            if (d.Status is not (SubmissionStatus.Pending or SubmissionStatus.Simulated) || d.SentUtc is not null || d.TrackId is not null) return FiscalErrors.AlreadySent;
            if (await db.Documents.AnyAsync(x => x.ModifiedENcf == d.ENcf && x.Status != SubmissionStatus.Voided, ct)) return FiscalErrors.HasNotes;
            var business = await settings.GetBusinessProfileAsync(ct);
            if (!Rnc.IsValid(business.Rnc)) return FiscalErrors.MissingIssuerRnc;
            if (d.Attempts > 0 && !provider.IsSimulated)
            {
                try
                {
                    if (await provider.FindAsync(business.Rnc!, d.ENcf, d.SecurityCode, d.Channel, ct) is not null) return FiscalErrors.AlreadySent;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    return FiscalErrors.DgiiUnreachable;
                }
            }
            // Only from the state read above: if the queue sent it meanwhile, this fails instead of voiding a document the DGII has.
            if (await db.Documents.Where(x => x.Id == d.Id && x.Status == d.Status && x.SentUtc == null)
                    .ExecuteUpdateAsync(x => x.SetProperty(q => q.Status, SubmissionStatus.Voided).SetProperty(q => q.NextAttemptUtc, (DateTime?)null), ct) != 1)
                return FiscalErrors.AlreadySent;

            var number = long.Parse(d.ENcf[3..], System.Globalization.CultureInfo.InvariantCulture);
            var now = clock.GetUtcNow().UtcDateTime;
            var xml = EcfXml.Anecf(business.Rnc!, d.Type, number, number, now);
            if (!signer.IsSimulated && EcfXml.ValidateUnsigned(xml, EcfXml.AnecfSchema) is [var first, ..]) return FiscalErrors.InvalidXml(first);
            var v = new EcfVoid
            {
                Id = Guid.CreateVersion7(), Type = d.Type, Environment = d.Environment, From = number, To = number, CreatedUtc = now,
                SequenceId = await db.Sequences.Where(s => s.Type == d.Type && s.Environment == d.Environment && s.From <= number && number <= s.To)
                    .Select(s => s.Id).FirstOrDefaultAsync(ct),
                SignedXml = EcfSubmissionQueue.Zip(signer.Sign(xml).SignedXml),
                Status = signer.IsSimulated || d.Status == SubmissionStatus.Simulated ? SubmissionStatus.Simulated : SubmissionStatus.Pending,
            };
            db.Voids.Add(v);
            var response = new VoidSequence.Response(v.Id, d.ENcf, d.ENcf, 1);
            await audit.RecordAsync("fiscal.void-document", "FiscalDocument", d.Id, response, ct);
            return response;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/documents/{id:guid}/void", async (Guid id, ICommandHandler<Command, VoidSequence.Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id), ct)).ToHttp(r => Results.Created($"/api/fiscal/voids/{r.Id}", r)))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>ANECF sent or waiting, newest first, with the DGII answer.</summary>
internal static class ListVoids
{
    public sealed record Item(Guid Id, EcfType Type, FiscalEnvironment Environment, string From, string To, long Count, DateTime CreatedUtc,
        SubmissionStatus Status, string? Message);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/voids", async (FiscalDbContext db, CancellationToken ct) =>
                    Results.Ok((await db.Voids.AsNoTracking().OrderByDescending(v => v.CreatedUtc).Take(200).ToListAsync(ct))
                        .Select(v => new Item(v.Id, v.Type, v.Environment, ENcf.Format(v.Type, v.From), ENcf.Format(v.Type, v.To), v.To - v.From + 1, v.CreatedUtc,
                            v.Status, v.Message))))
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}
