using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Identity.Contracts;
using Toki.Identity.Domain;
using Toki.Identity.Persistence;
using Toki.SharedKernel;

namespace Toki.Identity.Features;

internal static class CreateUser
{
    public sealed record Command(string Name, string Code, string Pin, Guid RoleId) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(100);
            RuleFor(c => c.Code).NotEmpty().MaximumLength(20);
            RuleFor(c => c.Pin).Must(Domain.Pin.IsValid).WithMessage("El PIN debe tener de 4 a 8 dígitos.");
        }
    }

    internal sealed class Handler(IdentityDbContext db, IOutbox outbox, IAuditLog audit, IUserContext me) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == c.RoleId, ct);
            if (role is null) return IdentityErrors.RoleNotFound;
            if (c.RoleId == Role.AdministratorId && !await db.IsAdministratorAsync(me.UserId, ct)) return IdentityErrors.AdministratorOnly;
            if (Escalation.NotHeld(me, role.Effective) is { Count: > 0 } missing) return IdentityErrors.PermissionsNotHeld(missing);
            if (await db.Users.AnyAsync(u => u.Code == User.NormalizeCode(c.Code), ct)) return IdentityErrors.DuplicateCode;

            var user = new User { Id = Guid.CreateVersion7(), Name = c.Name.Trim(), Code = User.NormalizeCode(c.Code), RoleId = c.RoleId };
            user.ChangePin(c.Pin);
            db.Users.Add(user);
            outbox.Add(new UserProfileChangedV1(user.Id, user.Name, user.IsActive));
            await audit.RecordAsync("identity.create-user", "User", user.Id, new { before = (object?)null, after = new { user.Name, user.Code, user.RoleId, user.IsActive } }, ct);
            return new Response(user.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identity/users", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/identity/users/{r.Id}", r)))
                .RequirePermission(Permissions.UsersManage).WithTags("Identity");
    }
}

internal static class UpdateUser
{
    /// <param name="Pin">Optional new PIN (an administrator resets a forgotten PIN; it also clears the lockout).</param>
    public sealed record Command(Guid Id, string Name, Guid RoleId, bool IsActive, string? Pin) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(100);
            RuleFor(c => c.Pin).Must(Domain.Pin.IsValid).When(c => c.Pin is not null).WithMessage("El PIN debe tener de 4 a 8 dígitos.");
        }
    }

    internal sealed class Handler(IdentityDbContext db, IOutbox outbox, IAuditLog audit, IUserContext me, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == c.Id, ct);
            if (user is null) return IdentityErrors.UserNotFound;
            var roles = await db.Roles.AsNoTracking().Where(r => r.Id == c.RoleId || r.Id == user.RoleId).ToListAsync(ct);
            if (roles.All(r => r.Id != c.RoleId)) return IdentityErrors.RoleNotFound;
            if (user.Id == me.UserId && (!c.IsActive || c.RoleId != user.RoleId))
                return Error.Validation("Identity.CannotLockSelf", "No puede desactivarse ni cambiarse el rol a sí mismo.");
            if ((c.RoleId == Role.AdministratorId || user.RoleId == Role.AdministratorId) && !await db.IsAdministratorAsync(me.UserId, ct))
                return IdentityErrors.AdministratorOnly;
            // TK-215: the new role and the current one (a PIN reset would let the actor sign in as someone with more permissions).
            if (Escalation.NotHeld(me, roles.SelectMany(r => r.Effective)) is { Count: > 0 } missing) return IdentityErrors.PermissionsNotHeld(missing);
            // At least one active Administrador always remains: only an active Administrador changes one, and never themselves (above).

            var before = new { user.Name, user.RoleId, user.IsActive };
            user.Name = c.Name.Trim();
            user.RoleId = c.RoleId;
            user.IsActive = c.IsActive;
            if (c.Pin is not null) user.ChangePin(c.Pin);
            if (!c.IsActive || c.Pin is not null)
            {
                var now = clock.GetUtcNow().UtcDateTime;
                await db.Sessions.Where(s => s.UserId == c.Id && s.ClosedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ClosedUtc, now), ct);
            }
            outbox.Add(new UserProfileChangedV1(user.Id, user.Name, user.IsActive));
            await audit.RecordAsync("identity.update-user", "User", user.Id,
                new { before, after = new { user.Name, user.RoleId, user.IsActive }, PinChanged = c.Pin is not null }, ct);
            return Ok.Value;
        }
    }

    internal sealed record Body(string Name, Guid RoleId, bool IsActive, string? Pin);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/identity/users/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Name, b.RoleId, b.IsActive, b.Pin), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.UsersManage).WithTags("Identity");
    }
}

internal static class ListUsers
{
    public sealed record Item(Guid Id, string Name, string Code, Guid RoleId, string Role, bool IsActive, bool IsLocked);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/identity/users", async (IdentityDbContext db, TimeProvider clock, CancellationToken ct) =>
                {
                    var now = clock.GetUtcNow().UtcDateTime;
                    var items = await (from u in db.Users
                                       join r in db.Roles on u.RoleId equals r.Id
                                       orderby u.Name
                                       select new Item(u.Id, u.Name, u.Code, r.Id, r.Name, u.IsActive, u.LockedUntilUtc > now))
                        .AsNoTracking().ToListAsync(ct);
                    return Results.Ok(items);
                })
                .RequirePermission(Permissions.UsersManage).WithTags("Identity");
    }
}

/// <summary>Active users for the login screen of a paired device (names and codes, never PINs).</summary>
internal static class ListCashiers
{
    public sealed record Item(Guid Id, string Name, string Code);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/identity/users/active", async (HttpRequest req, IdentityDbContext db, CancellationToken ct) =>
                {
                    var hash = Token.Hash(req.Headers[IdentityModule.DeviceHeader].ToString());
                    if (!await db.Devices.AnyAsync(d => d.TokenHash == hash && !d.IsRevoked, ct))
                        return EndpointExtensions.Problem(IdentityErrors.DeviceNotPaired);
                    return Results.Ok(await db.Users.Where(u => u.IsActive).OrderBy(u => u.Name)
                        .Select(u => new Item(u.Id, u.Name, u.Code)).AsNoTracking().ToListAsync(ct));
                })
                .AllowAnonymous().WithTags("Identity");
    }
}
