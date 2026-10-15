using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Identidad.Domain;
using Toki.Identidad.Persistence;
using Toki.SharedKernel;

namespace Toki.Identidad.Features;

internal static class CrearUsuario
{
    public sealed record Command(string Nombre, string Codigo, string Pin, Guid RolId) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Nombre).NotEmpty().MaximumLength(100);
            RuleFor(c => c.Codigo).NotEmpty().MaximumLength(20);
            RuleFor(c => c.Pin).Must(Domain.Pin.EsValido).WithMessage("El PIN debe tener de 4 a 8 dígitos.");
        }
    }

    internal sealed class Handler(IdentidadDbContext db, IAuditoria auditoria, IUserContext yo) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (!await db.Roles.AnyAsync(r => r.Id == c.RolId, ct)) return IdentidadErrors.RolNoEncontrado;
            if (c.RolId == Rol.AdministradorId && !await db.EsAdministradorAsync(yo.UsuarioId, ct)) return IdentidadErrors.SoloAdministrador;
            if (await db.Usuarios.AnyAsync(u => u.Codigo == Usuario.NormalizarCodigo(c.Codigo), ct)) return IdentidadErrors.CodigoDuplicado;

            var usuario = new Usuario { Id = Guid.CreateVersion7(), Nombre = c.Nombre.Trim(), Codigo = Usuario.NormalizarCodigo(c.Codigo), RolId = c.RolId };
            usuario.CambiarPin(c.Pin);
            db.Usuarios.Add(usuario);
            await auditoria.RegistrarAsync("identidad.crear-usuario", "Usuario", usuario.Id, new { usuario.Nombre, usuario.Codigo, usuario.RolId }, ct);
            return new Response(usuario.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identidad/usuarios", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/identidad/usuarios/{r.Id}", r)))
                .RequirePermiso(Permisos.UsuariosGestionar).WithTags("Identidad");
    }
}

internal static class ActualizarUsuario
{
    /// <param name="Pin">Optional new PIN (an administrator resets a forgotten PIN; it also clears the lockout).</param>
    public sealed record Command(Guid Id, string Nombre, Guid RolId, bool Activo, string? Pin) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Nombre).NotEmpty().MaximumLength(100);
            RuleFor(c => c.Pin).Must(Domain.Pin.EsValido).When(c => c.Pin is not null).WithMessage("El PIN debe tener de 4 a 8 dígitos.");
        }
    }

    internal sealed class Handler(IdentidadDbContext db, IAuditoria auditoria, IUserContext yo, TimeProvider reloj) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == c.Id, ct);
            if (usuario is null) return IdentidadErrors.UsuarioNoEncontrado;
            if (!await db.Roles.AnyAsync(r => r.Id == c.RolId, ct)) return IdentidadErrors.RolNoEncontrado;
            if (usuario.Id == yo.UsuarioId && (!c.Activo || c.RolId != usuario.RolId))
                return Error.Validation("Identidad.NoAutoBloqueo", "No puede desactivarse ni cambiarse el rol a sí mismo.");
            if ((c.RolId == Rol.AdministradorId || usuario.RolId == Rol.AdministradorId) && !await db.EsAdministradorAsync(yo.UsuarioId, ct))
                return IdentidadErrors.SoloAdministrador;

            usuario.Nombre = c.Nombre.Trim();
            usuario.RolId = c.RolId;
            usuario.Activo = c.Activo;
            if (c.Pin is not null) usuario.CambiarPin(c.Pin);
            if (!c.Activo || c.Pin is not null)
            {
                var ahora = reloj.GetUtcNow().UtcDateTime;
                await db.Sesiones.Where(s => s.UsuarioId == c.Id && s.CerradaUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CerradaUtc, ahora), ct);
            }
            await auditoria.RegistrarAsync("identidad.actualizar-usuario", "Usuario", usuario.Id,
                new { usuario.Nombre, usuario.RolId, usuario.Activo, CambioPin = c.Pin is not null }, ct);
            return Ok.Valor;
        }
    }

    internal sealed record Body(string Nombre, Guid RolId, bool Activo, string? Pin);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPut("/api/identidad/usuarios/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Nombre, b.RolId, b.Activo, b.Pin), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.UsuariosGestionar).WithTags("Identidad");
    }
}

internal static class ListarUsuarios
{
    public sealed record Item(Guid Id, string Nombre, string Codigo, Guid RolId, string Rol, bool Activo, bool Bloqueado);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/identidad/usuarios", async (IdentidadDbContext db, TimeProvider reloj, CancellationToken ct) =>
                {
                    var ahora = reloj.GetUtcNow().UtcDateTime;
                    var items = await (from u in db.Usuarios
                                       join r in db.Roles on u.RolId equals r.Id
                                       orderby u.Nombre
                                       select new Item(u.Id, u.Nombre, u.Codigo, r.Id, r.Nombre, u.Activo, u.BloqueadoHastaUtc > ahora))
                        .AsNoTracking().ToListAsync(ct);
                    return Results.Ok(items);
                })
                .RequirePermiso(Permisos.UsuariosGestionar).WithTags("Identidad");
    }
}

/// <summary>Active users for the login screen of a paired device (names and codes, never PINs).</summary>
internal static class ListarCajeros
{
    public sealed record Item(Guid Id, string Nombre, string Codigo);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/identidad/usuarios/activos", async (HttpRequest req, IdentidadDbContext db, CancellationToken ct) =>
                {
                    var hash = Token.Hash(req.Headers[IdentidadModule.HeaderDispositivo].ToString());
                    if (!await db.Dispositivos.AnyAsync(d => d.TokenHash == hash && !d.Revocado, ct))
                        return EndpointExtensions.Problema(IdentidadErrors.DispositivoNoEmparejado);
                    return Results.Ok(await db.Usuarios.Where(u => u.Activo).OrderBy(u => u.Nombre)
                        .Select(u => new Item(u.Id, u.Nombre, u.Codigo)).AsNoTracking().ToListAsync(ct));
                })
                .AllowAnonymous().WithTags("Identidad");
    }
}
