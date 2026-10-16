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
    /// <summary>True when e-mail really goes out. Then an invite link travels only by e-mail (R8 #1): the inviter never sees it, so they cannot
    /// create the account of someone else's e-mail with a password of their choice.</summary>
    bool Delivers { get; }

    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>When <c>Email:Provider</c> is not set (development, tests): nothing is sent; the owner copies the invite link from the panel and hands it
/// over (R8 #1: an account made from such a link is not marked e-mail-confirmed). Production sends through <see cref="AcsEmailSender"/> (TK-241).
/// The body (it has the link) is logged only at Debug.</summary>
internal sealed class LoggingEmailSender(ILogger<LoggingEmailSender> log) : IEmailSender
{
    public bool Delivers => false;

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        log.LogInformation("E-mail not sent (no provider configured) to {To}: {Subject}", to, subject);
        log.LogDebug("E-mail body: {Body}", body);
        return Task.CompletedTask;
    }
}

/// <param name="CanViewCosts">TK-201: Owners always; a ReadOnly member only when an Owner allowed it.</param>
public sealed record MemberResponse(string UserId, string? Email, MemberRole Role, DateTime JoinedUtc, bool CanViewCosts);
public sealed record InviteResponse(Guid Id, string Email, MemberRole Role, string InvitedBy, DateTime CreatedUtc, DateTime ExpiresUtc);
public sealed record MembersResponse(IReadOnlyList<MemberResponse> Members, IReadOnlyList<InviteResponse> Invites);
public sealed record CreateInviteRequest(string? Email, MemberRole? Role);
/// <param name="Link">The single-use link, shown only now (the database keeps its hash); null when it went out by e-mail (R8 #1).</param>
public sealed record CreatedInviteResponse(InviteResponse Invite, string? Link);
public sealed record ChangeRoleRequest(MemberRole? Role);
public sealed record ChangeCostsRequest(bool? CanViewCosts);
/// <summary>What the invite page shows before joining.</summary>
public sealed record InvitePreview(string TenantName, string Email, MemberRole Role, DateTime ExpiresUtc);
/// <summary>R8 #12: the token travels in the body, never in a URL that request logs keep.</summary>
public sealed record InviteTokenRequest(string? Token);
public sealed record AcceptInviteRegisterRequest(string? Token, string? Password);

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
                .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new MemberResponse(m.UserId, u.Email, m.Role, m.JoinedUtc, m.Role == MemberRole.Owner || m.CanViewCosts)).ToListAsync(ct);
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
                LinkShown = !email.Delivers,
            };
            db.TenantInvites.Add(invite);
            await db.SaveChangesAsync(ct);

            // R8 #12: the token goes after "#", which browsers never send to a server, so no request log (panel host, API) keeps it.
            var link = $"{PanelUrl(config)}/invite#{token}";
            var tenantName = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).SingleAsync(ct);
            await email.SendAsync(to, $"Le invitaron al panel de {tenantName} en Toki",
                $"{invite.InvitedBy} le invitó a ver el panel de {tenantName}. Abra este enlace antes de 7 días (sirve una sola vez): {link}", ct);
            return Results.Ok(new CreatedInviteResponse(ToResponse(invite), invite.LinkShown ? link : null));
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
            var changed = await SerializedAsync(db, () => KeepingAnOwner(db, tenantId, userId, keeps: role == MemberRole.Owner)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, role), ct), ct);
            return changed == 0 ? LastOwner() : Results.NoContent();
        });

        // TK-201: an Owner lets a ReadOnly member (an accountant) see cost and profit. Owners always see them, so the flag only matters for ReadOnly.
        members.MapPut("/{userId}/costs", async (string userId, ChangeCostsRequest request, ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            if (request.CanViewCosts is not { } allowed)
                return EndpointExtensions.Problem(Error.Validation("Members.InvalidCosts", "Indique si puede ver costos y ganancias."));
            var tenantId = user.GetTenantId();
            return await db.TenantMembers.Where(m => m.TenantId == tenantId && m.UserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.CanViewCosts, allowed), ct) == 0
                ? MemberNotFound()
                : Results.NoContent();
        });

        members.MapDelete("/{userId}", async (string userId, ClaimsPrincipal user, CloudDbContext db, CancellationToken ct) =>
        {
            var tenantId = user.GetTenantId();
            if (!await db.TenantMembers.AnyAsync(m => m.TenantId == tenantId && m.UserId == userId, ct)) return MemberNotFound();
            return await SerializedAsync(db, () => KeepingAnOwner(db, tenantId, userId, keeps: false).ExecuteDeleteAsync(ct), ct) == 0
                ? LastOwner()
                : Results.NoContent();
        });

        // The invitee has no session (or one in another tenant): these work by the token alone, which is 256 random bits.
        // R8 #12: always in the body (POST), never in the path, so request logs and Application Insights never see it.
        var invites = app.MapGroup("/api/invites");

        invites.MapPost("/preview", async (InviteTokenRequest request, CloudDbContext db, TimeProvider clock, CancellationToken ct) =>
            await FindAsync(db, request.Token, clock, ct) is { } invite
                ? Results.Ok(new InvitePreview(await db.Tenants.Where(t => t.Id == invite.TenantId).Select(t => t.Name).SingleAsync(ct), invite.Email, invite.Role, invite.ExpiresUtc))
                : InvalidInvite()).AllowAnonymous();

        // A new account for the invited e-mail, joined to the tenant in the same SaveChanges (no tenant of its own).
        // R9 #21: anyone can register any address in /api/auth/register, which used to block the invite. A link that arrived by e-mail proves the
        // address, so an unconfirmed account with it becomes this person's: new password (a new security stamp: other sessions cannot refresh) and
        // confirmed. It keeps what it had, since it may be their own earlier sign-up. A confirmed account, or a link the inviter saw, proves nothing.
        invites.MapPost("/register", async (AcceptInviteRegisterRequest request, CloudDbContext db, UserManager<CloudUser> users, TimeProvider clock,
            CancellationToken ct) =>
        {
            var invite = await FindAsync(db, request.Token, clock, ct, tracked: true);
            if (invite is null) return InvalidInvite();
            var existing = await users.FindByEmailAsync(invite.Email);
            if (existing is not null && (existing.EmailConfirmed || invite.LinkShown))
                return EndpointExtensions.Problem(Error.Conflict("Invites.AccountExists", "Ya existe una cuenta con ese correo. Entre con ella para unirse."));
            if (existing is not null && await db.TenantMembers.AnyAsync(m => m.TenantId == invite.TenantId && m.UserId == existing.Id, ct))
                return EndpointExtensions.Problem(Error.Conflict("Members.AlreadyMember", "Usted ya es miembro de este negocio."));

            var now = clock.GetUtcNow().UtcDateTime;
            // R8 #1: the e-mail counts as confirmed only when the link reached it by e-mail; a link the inviter saw proves nothing.
            var account = existing ?? new CloudUser { UserName = invite.Email, Email = invite.Email };
            (account.TenantId, account.EmailConfirmed) = (invite.TenantId, !invite.LinkShown);
            db.TenantMembers.Add(new TenantMember { TenantId = invite.TenantId, UserId = account.Id, Role = invite.Role, JoinedUtc = now });
            invite.AcceptedUtc = now;
            IdentityResult result;
            try
            {
                result = existing is null
                    ? await users.CreateAsync(account, request.Password ?? "")
                    : await users.ResetPasswordAsync(account, await users.GeneratePasswordResetTokenAsync(account), request.Password ?? "");
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
        invites.MapPost("/accept", async (InviteTokenRequest request, ClaimsPrincipal user, CloudDbContext db, UserManager<CloudUser> users, TimeProvider clock,
            CancellationToken ct) =>
        {
            var account = await users.GetUserAsync(user);
            var invite = await FindAsync(db, request.Token, clock, ct, tracked: true);
            if (account is null || invite is null) return InvalidInvite();
            if (!string.Equals(account.NormalizedEmail, invite.Email.ToUpperInvariant(), StringComparison.Ordinal))
                return EndpointExtensions.Problem(Error.Forbidden("Invites.WrongEmail", $"Esta invitación es para {invite.Email}. Entre con esa cuenta."));
            if (await db.TenantMembers.AnyAsync(m => m.TenantId == invite.TenantId && m.UserId == account.Id, ct))
                return EndpointExtensions.Problem(Error.Conflict("Members.AlreadyMember", "Usted ya es miembro de este negocio."));

            var now = clock.GetUtcNow().UtcDateTime;
            db.TenantMembers.Add(new TenantMember { TenantId = invite.TenantId, UserId = account.Id, Role = invite.Role, JoinedUtc = now });
            invite.AcceptedUtc = now;
            account.TenantId = invite.TenantId;
            account.EmailConfirmed |= !invite.LinkShown; // TK-231: the link reached that inbox by e-mail (R8 #1)
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

    private static async Task<TenantInvite?> FindAsync(CloudDbContext db, string? token, TimeProvider clock, CancellationToken ct, bool tracked = false)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var hash = Hash(token);
        var now = clock.GetUtcNow().UtcDateTime;
        var query = tracked ? db.TenantInvites : db.TenantInvites.AsNoTracking();
        var invite = await query.SingleOrDefaultAsync(i => i.TokenHash == hash && i.AcceptedUtc == null && i.RevokedUtc == null && i.ExpiresUtc > now, ct);
        // A tenant disabled by Toki staff (TK-189) takes no new members either.
        return invite is not null && !await db.Tenants.AnyAsync(t => t.Id == invite.TenantId && t.DisabledUtc != null, ct) ? invite : null;
    }

    /// <summary>The member row, only if the change leaves an Owner: it stays one (<paramref name="keeps"/>), it is not one, or another Owner exists.
    /// Run it through <see cref="SerializedAsync"/>.</summary>
    private static IQueryable<TenantMember> KeepingAnOwner(CloudDbContext db, Guid tenantId, string userId, bool keeps) =>
        db.TenantMembers.Where(m => m.TenantId == tenantId && m.UserId == userId
            && (keeps || m.Role != MemberRole.Owner || db.TenantMembers.Any(o => o.TenantId == tenantId && o.Role == MemberRole.Owner && o.UserId != userId)));

    /// <summary>The "another Owner exists" check and the write in one SERIALIZABLE transaction. Under READ_COMMITTED_SNAPSHOT (Azure SQL's default)
    /// the check read a snapshot, so two Owners demoting or removing each other at once could both pass and leave none. Serializable reads lock
    /// what they read: the second request waits and then sees no other Owner, or SQL Server picks it as a deadlock victim and the execution strategy
    /// runs it again, with the same result. Sqlite takes the write lock when the transaction begins (BEGIN IMMEDIATE), so writers queue.</summary>
    private static Task<int> SerializedAsync(CloudDbContext db, Func<Task<int>> write, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
            var changed = await write();
            await tx.CommitAsync(ct);
            return changed;
        });

    private static InviteResponse ToResponse(TenantInvite i) => new(i.Id, i.Email, i.Role, i.InvitedBy, i.CreatedUtc, i.ExpiresUtc);

    /// <summary><c>Panel:Url</c>, or the first allowed panel origin.</summary>
    internal static string PanelUrl(IConfiguration config) =>
        (config["Panel:Url"] ?? config.GetSection("Panel:Origins").Get<string[]>()?.FirstOrDefault() ?? "").TrimEnd('/');

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IResult InvalidInvite() =>
        EndpointExtensions.Problem(Error.NotFound("Invites.Invalid", "La invitación no es válida, ya se usó o venció. Pida una nueva al dueño."));

    private static IResult MemberNotFound() => EndpointExtensions.Problem(Error.NotFound("Members.NotFound", "No encontramos ese miembro."));

    private static IResult LastOwner() =>
        EndpointExtensions.Problem(Error.Conflict("Members.LastOwner", "El negocio necesita al menos un dueño. Nombre a otro dueño primero."));
}
