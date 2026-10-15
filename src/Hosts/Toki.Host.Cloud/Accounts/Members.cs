using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Accounts;

/// <summary>Outgoing e-mail of the cloud (TK-217: invites).</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>shortcut: no e-mail provider yet, so nothing is sent; the owner copies the invite link from the panel.
/// Wire Azure Communication Services or SendGrid here before the owners expect the e-mail. The body (it has the link) is logged only at Debug.</summary>
internal sealed class LoggingEmailSender(ILogger<LoggingEmailSender> log) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        log.LogInformation("E-mail not sent (no provider configured) to {To}: {Subject}", to, subject);
        log.LogDebug("E-mail body: {Body}", body);
        return Task.CompletedTask;
    }
}

public sealed record MemberResponse(string UserId, string? Email, MemberRole Role, DateTime JoinedUtc);
public sealed record InviteResponse(Guid Id, string Email, MemberRole Role, string InvitedBy, DateTime CreatedUtc, DateTime ExpiresUtc);
public sealed record MembersResponse(IReadOnlyList<MemberResponse> Members, IReadOnlyList<InviteResponse> Invites);
public sealed record CreateInviteRequest(string? Email, MemberRole? Role);
/// <param name="Link">The single-use link; it is shown only now (the database keeps its hash).</param>
public sealed record CreatedInviteResponse(InviteResponse Invite, string Link);
public sealed record ChangeRoleRequest(MemberRole? Role);
/// <summary>What the invite page shows before joining.</summary>
public sealed record InvitePreview(string TenantName, string Email, MemberRole Role, DateTime ExpiresUtc);
public sealed record AcceptInviteRegisterRequest(string? Password);

/// <summary>TK-217: the people who use a tenant's panel. Owners invite (single-use link, 7 days), change roles and remove members;
/// a tenant never loses its last Owner. Membership is checked on every call (<see cref="PanelAccess"/>).</summary>
internal static class MemberEndpoints
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    public static void MapMembers(this IEndpointRouteBuilder app)
    {
        var members = app.MapGroup("/api/members").RequireAuthorization(CloudAuth.PanelPolicy).RequireAuthorization(CloudAuth.OwnerPolicy);

        members.MapGet("", async (ClaimsPrincipal user, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            var now = clock.GetUtcNow().UtcDateTime;
            var list = await db.TenantMembers.AsNoTracking().Where(m => m.TenantId == tenantId)
                .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new MemberResponse(m.UserId, u.Email, m.Role, m.JoinedUtc)).ToListAsync(ct);
            var invites = await PendingInvites(db, tenantId, now).ToListAsync(ct);
            return Results.Ok(new MembersResponse([.. list.OrderBy(m => m.Role).ThenBy(m => m.Email, StringComparer.OrdinalIgnoreCase)],
                [.. invites.OrderByDescending(i => i.CreatedUtc).Select(ToResponse)]));
        });

        members.MapPost("/invites", async (CreateInviteRequest request, ClaimsPrincipal user, CloudDbContext db, IEmailSender email, IConfiguration config,
            TimeProvider clock, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            var to = request.Email?.Trim() ?? "";
            if (to.Length is 0 or > 256 || !System.Net.Mail.MailAddress.TryCreate(to, out _))
                return EndpointExtensions.Problem(Error.Validation("Members.InvalidEmail", "Escriba un correo válido."));
            if (request.Role is not { } role || !Enum.IsDefined(role))
                return EndpointExtensions.Problem(Error.Validation("Members.InvalidRole", "Elija el rol."));
            var normalized = to.ToUpperInvariant();
            if (await db.TenantMembers.Where(m => m.TenantId == tenantId).Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.NormalizedEmail).AnyAsync(e => e == normalized, ct))
                return EndpointExtensions.Problem(Error.Conflict("Members.AlreadyMember", "Esa persona ya es miembro de este negocio."));

            var now = clock.GetUtcNow().UtcDateTime;
            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            var invite = new TenantInvite
            {
                Id = Guid.CreateVersion7(), TenantId = tenantId, Email = to, Role = role, TokenHash = Hash(token),
                InvitedBy = user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name ?? "", CreatedUtc = now, ExpiresUtc = now + InviteLifetime,
            };
            db.TenantInvites.Add(invite);
            await db.SaveChangesAsync(ct);

            var link = $"{PanelUrl(config)}/invite/{token}";
            var tenantName = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).SingleAsync(ct);
            await email.SendAsync(to, $"Le invitaron al panel de {tenantName} en Toki",
                $"{invite.InvitedBy} le invitó a ver el panel de {tenantName}. Abra este enlace antes de 7 días (sirve una sola vez): {link}", ct);
            return Results.Ok(new CreatedInviteResponse(ToResponse(invite), link));
        });

        members.MapDelete("/invites/{id:guid}", async (Guid id, ClaimsPrincipal user, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            var now = clock.GetUtcNow().UtcDateTime;
            return await db.TenantInvites.Where(i => i.Id == id && i.TenantId == tenantId && i.AcceptedUtc == null && i.RevokedUtc == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedUtc, now), ct) == 0
                ? EndpointExtensions.Problem(Error.NotFound("Members.InviteNotFound", "No encontramos esa invitación pendiente."))
                : Results.NoContent();
        });

        members.MapPut("/{userId}/role", async (string userId, ChangeRoleRequest request, ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            if (request.Role is not { } role || !Enum.IsDefined(role))
                return EndpointExtensions.Problem(Error.Validation("Members.InvalidRole", "Elija el rol."));
            var tenantId = user.GetTenantId();
            if (!await db.TenantMembers.AnyAsync(m => m.TenantId == tenantId && m.UserId == userId, ct)) return MemberNotFound();
            var changed = await KeepingAnOwner(db, tenantId, userId, keeps: role == MemberRole.Owner)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, role), ct);
            return changed == 0 ? LastOwner() : Results.NoContent();
        });

        members.MapDelete("/{userId}", async (string userId, ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            if (!await db.TenantMembers.AnyAsync(m => m.TenantId == tenantId && m.UserId == userId, ct)) return MemberNotFound();
            return await KeepingAnOwner(db, tenantId, userId, keeps: false).ExecuteDeleteAsync(ct) == 0 ? LastOwner() : Results.NoContent();
        });

        // The invitee has no session (or one in another tenant): these work by the token alone, which is 256 random bits.
        var invites = app.MapGroup("/api/invites/{token}");

        invites.MapGet("", async (string token, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
            await FindAsync(db, token, clock, ct) is { } invite
                ? Results.Ok(new InvitePreview(await db.Tenants.Where(t => t.Id == invite.TenantId).Select(t => t.Name).SingleAsync(ct), invite.Email, invite.Role, invite.ExpiresUtc))
                : InvalidInvite()).AllowAnonymous();

        // A new account for the invited e-mail, joined to the tenant in the same SaveChanges (no tenant of its own).
        invites.MapPost("/register", async (string token, AcceptInviteRegisterRequest request, CloudDbContext db, UserManager<CloudUser> users, TimeProvider clock,
            CancellationToken ct) =>
        {
            var invite = await FindAsync(db, token, clock, ct, tracked: true);
            if (invite is null) return InvalidInvite();
            if (await users.FindByEmailAsync(invite.Email) is not null)
                return EndpointExtensions.Problem(Error.Conflict("Invites.AccountExists", "Ya existe una cuenta con ese correo. Entre con ella para unirse."));

            var now = clock.GetUtcNow().UtcDateTime;
            var account = new CloudUser { UserName = invite.Email, Email = invite.Email, TenantId = invite.TenantId };
            db.TenantMembers.Add(new TenantMember { TenantId = invite.TenantId, UserId = account.Id, Role = invite.Role, JoinedUtc = now });
            invite.AcceptedUtc = now;
            IdentityResult result;
            try
            {
                result = await users.CreateAsync(account, request.Password ?? "");
            }
            catch (DbUpdateConcurrencyException)
            {
                return InvalidInvite(); // someone used or revoked it a moment ago
            }
            // Same shape as /api/auth/register: the panel turns Identity's codes into Spanish.
            return result.Succeeded ? Results.NoContent() : EndpointExtensions.Problem(Error.Validation("Invites.InvalidAccount", "Revise la contraseña.") with
            {
                Details = result.Errors.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()),
            });
        }).AllowAnonymous();

        // An existing account joins; its token then moves to the new tenant (the panel renews it).
        invites.MapPost("/accept", async (string token, ClaimsPrincipal user, CloudDbContext db, UserManager<CloudUser> users, TimeProvider clock, CancellationToken ct) =>
        {
            var account = await users.GetUserAsync(user);
            var invite = await FindAsync(db, token, clock, ct, tracked: true);
            if (account is null || invite is null) return InvalidInvite();
            if (!string.Equals(account.NormalizedEmail, invite.Email.ToUpperInvariant(), StringComparison.Ordinal))
                return EndpointExtensions.Problem(Error.Forbidden("Invites.WrongEmail", $"Esta invitación es para {invite.Email}. Entre con esa cuenta."));
            if (await db.TenantMembers.AnyAsync(m => m.TenantId == invite.TenantId && m.UserId == account.Id, ct))
                return EndpointExtensions.Problem(Error.Conflict("Members.AlreadyMember", "Usted ya es miembro de este negocio."));

            var now = clock.GetUtcNow().UtcDateTime;
            db.TenantMembers.Add(new TenantMember { TenantId = invite.TenantId, UserId = account.Id, Role = invite.Role, JoinedUtc = now });
            invite.AcceptedUtc = now;
            account.TenantId = invite.TenantId;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return InvalidInvite();
            }
            return Results.NoContent();
        }).RequireAuthorization();
    }

    /// <summary>TK-217 migration: tenants from before memberships have none; their account becomes the Owner. Runs at every start and only ever
    /// touches tenants with no member at all, which cannot happen otherwise (removing a member never takes the last Owner).</summary>
    public static async Task BackfillOwnersAsync(CloudDbContext db, CancellationToken ct)
    {
        var orphans = await db.Users.Where(u => !db.TenantMembers.Any(m => m.TenantId == u.TenantId))
            .Join(db.Tenants, u => u.TenantId, t => t.Id, (u, t) => new { u.Id, u.TenantId, t.CreatedUtc }).ToListAsync(ct);
        if (orphans.Count == 0) return;
        db.TenantMembers.AddRange(orphans.Select(o => new TenantMember { TenantId = o.TenantId, UserId = o.Id, Role = MemberRole.Owner, JoinedUtc = o.CreatedUtc }));
        await db.SaveChangesAsync(ct);
    }

    private static IQueryable<TenantInvite> PendingInvites(CloudDbContext db, Guid tenantId, DateTime now) =>
        db.TenantInvites.AsNoTracking().Where(i => i.TenantId == tenantId && i.AcceptedUtc == null && i.RevokedUtc == null && i.ExpiresUtc > now);

    private static async Task<TenantInvite?> FindAsync(CloudDbContext db, string token, TimeProvider clock, CancellationToken ct, bool tracked = false)
    {
        var hash = Hash(token);
        var now = clock.GetUtcNow().UtcDateTime;
        var query = tracked ? db.TenantInvites : db.TenantInvites.AsNoTracking();
        var invite = await query.SingleOrDefaultAsync(i => i.TokenHash == hash && i.AcceptedUtc == null && i.RevokedUtc == null && i.ExpiresUtc > now, ct);
        // A tenant disabled by Toki staff (TK-189) takes no new members either.
        return invite is not null && !await db.Tenants.AnyAsync(t => t.Id == invite.TenantId && t.DisabledUtc != null, ct) ? invite : null;
    }

    /// <summary>The member row, only if the change leaves an Owner: it stays one (<paramref name="keeps"/>), it is not one, or another Owner exists.
    /// One statement, so the check and the write cannot be split by a second request.</summary>
    /// <remarks>shortcut: with READ_COMMITTED_SNAPSHOT (Azure SQL's default) two Owners demoting each other in the same instant could both pass;
    /// Toki staff can restore an Owner by hand. Add a serializable transaction if it ever happens.</remarks>
    private static IQueryable<TenantMember> KeepingAnOwner(CloudDbContext db, Guid tenantId, string userId, bool keeps) =>
        db.TenantMembers.Where(m => m.TenantId == tenantId && m.UserId == userId
            && (keeps || m.Role != MemberRole.Owner || db.TenantMembers.Any(o => o.TenantId == tenantId && o.Role == MemberRole.Owner && o.UserId != userId)));

    private static InviteResponse ToResponse(TenantInvite i) => new(i.Id, i.Email, i.Role, i.InvitedBy, i.CreatedUtc, i.ExpiresUtc);

    /// <summary><c>Panel:Url</c>, or the first allowed panel origin.</summary>
    private static string PanelUrl(IConfiguration config) =>
        (config["Panel:Url"] ?? config.GetSection("Panel:Origins").Get<string[]>()?.FirstOrDefault() ?? "").TrimEnd('/');

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IResult InvalidInvite() =>
        EndpointExtensions.Problem(Error.NotFound("Invites.Invalid", "La invitación no es válida, ya se usó o venció. Pida una nueva al dueño."));

    private static IResult MemberNotFound() => EndpointExtensions.Problem(Error.NotFound("Members.NotFound", "No encontramos ese miembro."));

    private static IResult LastOwner() =>
        EndpointExtensions.Problem(Error.Conflict("Members.LastOwner", "El negocio necesita al menos un dueño. Nombre a otro dueño primero."));
}
