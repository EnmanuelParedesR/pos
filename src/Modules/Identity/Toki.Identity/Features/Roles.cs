using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Identity.Domain;
using Toki.Identity.Persistence;
using Toki.SharedKernel;

namespace Toki.Identity.Features;

internal static class ListRoles
{
    public sealed record Item(Guid Id, string Name, IReadOnlyList<string> Permissions);
    public sealed record Response(IReadOnlyList<Item> Roles, IReadOnlyList<string> AvailablePermissions);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/identity/roles", async (IdentityDbContext db, CancellationToken ct) =>
                {
                    var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
                    return Results.Ok(new Response(roles.Select(r => new Item(r.Id, r.Name, r.Effective)).ToList(), Permissions.All));
                })
                .RequirePermission(Permissions.UsersManage).WithTags("Identity");
    }
}

/// <summary>TK-215: what the acting user may not hand out. Administrador has everything, so it gives anything.</summary>
internal static class Escalation
{
    public static List<string> NotHeld(IUserContext me, IEnumerable<string> permissions) => [.. permissions.Where(p => !me.Has(p)).Distinct()];
}

/// <summary>Creates a role (Id null) or replaces a role's name and permissions. The Administrador role always keeps every permission.
/// Nobody adds or removes a permission they do not have (TK-215, R11 A15); one the role already had may stay, since keeping it gives nothing new.</summary>
internal static class SaveRole
{
    public sealed record Command(Guid? Id, string Name, IReadOnlyList<string> Permissions) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Name).NotEmpty().MaximumLength(60);
            RuleForEach(c => c.Permissions).Must(p => BuildingBlocks.Security.Permissions.All.Contains(p)).WithMessage("Permiso desconocido: {PropertyValue}.");
        }
    }

    internal sealed class Handler(IdentityDbContext db, IAuditLog audit, IUserContext me) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (c.Id == Role.AdministratorId) return Error.Validation("Identity.ProtectedRole", "El rol Administrador no se puede editar.");
            var role = c.Id is { } id ? await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) : null;
            if (c.Id is not null && role is null) return IdentityErrors.RoleNotFound;
            var before = role is null ? null : new { role.Name, Permissions = role.Permissions.ToList() };
            // R11 A15: nor takes away one they do not have (that would change what others can do beyond the actor's own reach).
            var changed = c.Permissions.Except(before?.Permissions ?? []).Concat((before?.Permissions ?? []).Except(c.Permissions));
            if (Escalation.NotHeld(me, changed) is { Count: > 0 } missing) return IdentityErrors.PermissionsNotHeld(missing);
            if (role is null)
            {
                role = new Role { Id = Guid.CreateVersion7() };
                db.Roles.Add(role);
            }
            role.Name = c.Name.Trim();
            role.Permissions = c.Permissions.Distinct().ToList();
            await audit.RecordAsync("identity.save-role", "Role", role.Id, new { before, after = new { role.Name, role.Permissions } }, ct);
            return new Response(role.Id);
        }
    }

    internal sealed record Body(string Name, IReadOnlyList<string> Permissions);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/identity/roles", async (Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(null, b.Name, b.Permissions), ct)).ToHttp(r => Results.Created($"/api/identity/roles/{r.Id}", r)))
                .RequirePermission(Permissions.UsersManage).WithTags("Identity");
            app.MapPut("/api/identity/roles/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Name, b.Permissions), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.UsersManage).WithTags("Identity");
        }
    }
}

/// <summary>TK-215: deletes a role nobody has (active or not). Administrador never; a role with permissions the user lacks neither.</summary>
internal static class DeleteRole
{
    public sealed record Command(Guid Id) : ICommand<Ok>;

    internal sealed class Handler(IdentityDbContext db, IAuditLog audit, IUserContext me) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            if (c.Id == Role.AdministratorId) return Error.Validation("Identity.ProtectedRole", "El rol Administrador no se puede borrar.");
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == c.Id, ct);
            if (role is null) return IdentityErrors.RoleNotFound;
            if (Escalation.NotHeld(me, role.Permissions) is { Count: > 0 } missing) return IdentityErrors.PermissionsNotHeld(missing);
            if (await db.Users.AnyAsync(u => u.RoleId == c.Id, ct)) return IdentityErrors.RoleInUse;
            db.Roles.Remove(role);
            await audit.RecordAsync("identity.delete-role", "Role", role.Id, new { before = new { role.Name, role.Permissions }, after = (object?)null }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapDelete("/api/identity/roles/{id:guid}", async (Guid id, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.UsersManage).WithTags("Identity");
    }
}
