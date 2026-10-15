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
