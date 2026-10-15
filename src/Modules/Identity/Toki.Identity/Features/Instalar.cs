using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.Identity.Domain;
using Toki.Identity.Persistence;
using Toki.SharedKernel;

namespace Toki.Identity.Features;

/// <summary>First run: creates the administrator and pairs the main PC. Only works while there are no users.</summary>
internal static class Instalar
{
    public sealed record Command(string NombreAdministrador, string CodigoAdministrador, string Pin, string NombreDispositivo)
        : ICommand<Response>;

    public sealed record Response(Guid UsuarioId, Guid DispositivoId, string TokenDispositivo);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.NombreAdministrador).NotEmpty().MaximumLength(100);
            RuleFor(c => c.CodigoAdministrador).NotEmpty().MaximumLength(20);
            RuleFor(c => c.Pin).Must(Domain.Pin.EsValido).WithMessage("El PIN debe tener de 4 a 8 dígitos.");
            RuleFor(c => c.NombreDispositivo).NotEmpty().MaximumLength(100);
        }
    }

    internal sealed class Handler(IdentidadDbContext db, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // Touching the Administrador role row first takes its write lock, so two installs cannot both see "no users".
            await db.Roles.Where(r => r.Id == Rol.AdministradorId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Nombre, r => r.Nombre), ct);
            if (await db.Usuarios.AnyAsync(ct)) return IdentidadErrors.YaInstalado;

            var admin = new Usuario { Id = Guid.CreateVersion7(), Nombre = c.NombreAdministrador.Trim(), Codigo = Usuario.NormalizarCodigo(c.CodigoAdministrador), RolId = Rol.AdministradorId };
            admin.CambiarPin(c.Pin);
            var token = Token.Nuevo();
            var dispositivo = new Dispositivo
            {
                Id = Guid.CreateVersion7(), Nombre = c.NombreDispositivo.Trim(), TokenHash = Token.Hash(token), CreadoUtc = reloj.GetUtcNow().UtcDateTime,
            };
            db.Usuarios.Add(admin);
            db.Dispositivos.Add(dispositivo);
            await auditoria.RegistrarAsync("identidad.instalar", "Usuario", admin.Id, new { admin.Nombre, admin.Codigo, Dispositivo = dispositivo.Nombre }, ct);
            return new Response(admin.Id, dispositivo.Id, token);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identidad/instalacion", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/identidad/usuarios/{r.UsuarioId}", r)))
                .AllowAnonymous().WithTags("Identidad");
    }
}
