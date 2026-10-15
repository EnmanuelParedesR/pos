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
                    return Results.Ok(new Response(roles.Select(r => new Item(r.Id, r.Name, r.Permissions)).ToList(), Permissions.All));
                })
                .RequirePermission(Permissions.UsersManage).WithTags("Identity");
    }
}

/// <summary>Creates a role (Id null) or replaces a role's name and permissions. The Administrador role always keeps every permission.</summary>
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

    internal sealed class Handler(IdentityDbContext db, IAuditLog audit) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (c.Id == Role.AdministratorId) return Error.Validation("Identity.ProtectedRole", "El rol Administrador no se puede editar.");
            var role = c.Id is { } id ? await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) : null;
            if (c.Id is not null && role is null) return IdentityErrors.RoleNotFound;
            if (role is null)
            {
                role = new Role { Id = Guid.CreateVersion7() };
                db.Roles.Add(role);
            }
            role.Name = c.Name.Trim();
            role.Permissions = c.Permissions.Distinct().ToList();
            await audit.RecordAsync("identity.save-role", "Role", role.Id, new { role.Name, role.Permissions }, ct);
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
