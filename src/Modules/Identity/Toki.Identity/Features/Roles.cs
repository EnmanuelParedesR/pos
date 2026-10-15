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

internal static class ListarRoles
{
    public sealed record Item(Guid Id, string Nombre, IReadOnlyList<string> Permisos);
    public sealed record Response(IReadOnlyList<Item> Roles, IReadOnlyList<string> PermisosDisponibles);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/identidad/roles", async (IdentidadDbContext db, CancellationToken ct) =>
                {
                    var roles = await db.Roles.AsNoTracking().OrderBy(r => r.Nombre).ToListAsync(ct);
                    return Results.Ok(new Response(roles.Select(r => new Item(r.Id, r.Nombre, r.Permisos)).ToList(), Permisos.Todos));
                })
                .RequirePermiso(Permisos.UsuariosGestionar).WithTags("Identidad");
    }
}

/// <summary>Creates a role (Id null) or replaces a role's name and permissions. The Administrador role always keeps every permission.</summary>
internal static class GuardarRol
{
    public sealed record Command(Guid? Id, string Nombre, IReadOnlyList<string> Permisos) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Nombre).NotEmpty().MaximumLength(60);
            RuleForEach(c => c.Permisos).Must(p => BuildingBlocks.Security.Permisos.Todos.Contains(p)).WithMessage("Permiso desconocido: {PropertyValue}.");
        }
    }

    internal sealed class Handler(IdentidadDbContext db, IAuditoria auditoria) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (c.Id == Rol.AdministradorId) return Error.Validation("Identidad.RolProtegido", "El rol Administrador no se puede editar.");
            var rol = c.Id is { } id ? await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) : null;
            if (c.Id is not null && rol is null) return IdentidadErrors.RolNoEncontrado;
            if (rol is null)
            {
                rol = new Rol { Id = Guid.CreateVersion7() };
                db.Roles.Add(rol);
            }
            rol.Nombre = c.Nombre.Trim();
            rol.Permisos = c.Permisos.Distinct().ToList();
            await auditoria.RegistrarAsync("identidad.guardar-rol", "Rol", rol.Id, new { rol.Nombre, rol.Permisos }, ct);
            return new Response(rol.Id);
        }
    }

    internal sealed record Body(string Nombre, IReadOnlyList<string> Permisos);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/identidad/roles", async (Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(null, b.Nombre, b.Permisos), ct)).ToHttp(r => Results.Created($"/api/identidad/roles/{r.Id}", r)))
                .RequirePermiso(Permisos.UsuariosGestionar).WithTags("Identidad");
            app.MapPut("/api/identidad/roles/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Nombre, b.Permisos), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.UsuariosGestionar).WithTags("Identidad");
        }
    }
}
