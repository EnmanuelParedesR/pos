using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Host.Cloud.Nodes;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Fe;

/// <param name="Approved">Null while it waits for support.</param>
public sealed record FeDelegationItem(Guid Id, Guid TenantId, string TenantName, string Rnc, string Subject, string HolderName, DateTime RequestedUtc,
    bool? Approved, string? DecidedBy, DateTime? DecidedUtc);

public sealed record FeDelegationDecision(bool? Approve);

/// <summary>R11 B-High2: a receiver activated with a person's certificate (cédula) for a company's RNC. Nobody can check from here that the person
/// signs for the company (only the DGII knows, docs/ecf-propio.md §11 question 34), so Abastalia support checks it and decides in the console; every
/// console call is audited (<c>PlatformAuditRecord</c>).</summary>
internal static class FeDelegations
{
    public static readonly Error Pending = Error.Conflict("Fe.DelegationPending",
        "Ese certificado es de una persona (cédula), no del RNC del negocio. Soporte de Abastalia tiene que verificar que esa persona firma por el negocio: " +
        "le escribiremos. Cuando lo aprueben, active el receptor otra vez.");
    public static readonly Error Refused = Error.Conflict("Fe.DelegationRefused",
        "Soporte de Abastalia no pudo verificar que esa persona firma por el negocio. Use el certificado del RNC del negocio o escriba a soporte.");

    /// <summary>Null when <paramref name="signer"/> may activate the receiver of <paramref name="rnc"/>: it is that RNC's, or a cédula support
    /// approved for it. Otherwise the refusal; a new cédula request is recorded for support.</summary>
    public static async Task<IResult?> CheckAsync(CloudDbContext db, NodeIdentity node, string rnc, X509Certificate2 signer, string holder, DateTime now,
        CancellationToken ct)
    {
        if (FeTrust.BelongsTo(signer, rnc)) return null;
        if (!FeTrust.IsCedulaOnCompany(signer, rnc)) return EndpointExtensions.Problem(FeErrors.CertificateRncMismatch);
        var request = await db.FeDelegations.FirstOrDefaultAsync(d => d.TenantId == node.TenantId && d.Rnc == rnc && d.Thumbprint == signer.Thumbprint, ct);
        if (request?.Approved is true) return null;
        if (request?.Approved is false) return EndpointExtensions.Problem(Refused);
        if (request is null)
        {
            db.FeDelegations.Add(new FeDelegation
            {
                Id = Guid.CreateVersion7(), TenantId = node.TenantId, NodeId = node.NodeId, Rnc = rnc, Subject = FeXml.Cut(signer.Subject, 500),
                Thumbprint = signer.Thumbprint, HolderName = FeXml.Cut(holder, 150), RequestedUtc = now,
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); } // the same request a moment ago
        }
        return EndpointExtensions.Problem(Pending);
    }

    /// <summary>Under <c>/api/platform</c> (platform admins with 2FA, audited).</summary>
    public static void MapPlatformDelegations(RouteGroupBuilder platform)
    {
        // Pending first, newest first.
        platform.MapGet("/fe/delegations", async (CloudDbContext db, CancellationToken ct) =>
        {
            var rows = await db.FeDelegations.AsNoTracking().Join(db.Tenants, d => d.TenantId, t => t.Id, (d, t) => new { d, t.Name })
                .OrderByDescending(x => x.d.RequestedUtc).Take(200).ToListAsync(ct);
            return Results.Ok(rows.OrderBy(x => x.d.Approved is not null).Select(x => new FeDelegationItem(x.d.Id, x.d.TenantId, x.Name, x.d.Rnc, x.d.Subject,
                x.d.HolderName, x.d.RequestedUtc, x.d.Approved, x.d.DecidedBy, x.d.DecidedUtc)));
        });

        // Only a pending request is decided: a decision stands (a new certificate is a new request).
        platform.MapPost("/fe/delegations/{id:guid}", async (Guid id, FeDelegationDecision r, ClaimsPrincipal user, CloudDbContext db, TimeProvider clock,
            CancellationToken ct) =>
        {
            if (r.Approve is not { } approve) return EndpointExtensions.Problem(FeErrors.DecisionRequired);
            var by = FeXml.Cut(user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name, 256);
            var now = clock.GetUtcNow().UtcDateTime;
            return await db.FeDelegations.Where(d => d.Id == id && d.Approved == null).ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Approved, approve).SetProperty(d => d.DecidedBy, by).SetProperty(d => d.DecidedUtc, now), ct) == 0
                ? EndpointExtensions.Problem(Error.NotFound("Fe.DelegationNotFound", "No hay una solicitud pendiente con ese número."))
                : Results.NoContent();
        });
    }
}
