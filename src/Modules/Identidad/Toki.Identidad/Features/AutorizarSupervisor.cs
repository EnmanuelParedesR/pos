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

/// <summary>A supervisor types code + PIN at the register to allow one action (void, big discount, cash out…).
/// The result is a one-time authorization valid 2 minutes, bound to the action, the resource and this device.</summary>
internal static class AutorizarSupervisor
{
    public sealed record Command(string Codigo, string Pin, string Permiso, Guid RecursoId) : ICommand<Response>;

    /// <summary>A wrong PIN is a successful command so the failed attempt is saved; <see cref="Fallo"/> carries the error.</summary>
    public sealed record Response(Guid? AutorizacionId, string? Supervisor, DateTime? ExpiraUtc, Error? Fallo);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Codigo).NotEmpty().MaximumLength(20);
            RuleFor(c => c.Pin).NotEmpty().MaximumLength(8);
            RuleFor(c => c.Permiso).Must(p => Permisos.Todos.Contains(p)).WithMessage("Permiso desconocido.");
        }
    }

    internal sealed class Handler(IdentidadDbContext db, IUserContext yo, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (yo.DispositivoId is not { } dispositivoId) return IdentidadErrors.DispositivoNoEmparejado;
            var supervisor = await db.Usuarios.FirstOrDefaultAsync(u => u.Codigo == c.Codigo.Trim(), ct);
            if (supervisor is null) return IdentidadErrors.CredencialesInvalidas;

            var ahora = reloj.GetUtcNow().UtcDateTime;
            var pin = supervisor.IntentarPin(c.Pin, ahora);
            if (pin != ResultadoPin.Correcto)
            {
                await auditoria.RegistrarAsync("identidad.pin-fallido", "Usuario", supervisor.Id, new { supervisor.Codigo, c.Permiso, Resultado = pin }, ct);
                return new Response(null, null, null, pin == ResultadoPin.Bloqueado ? IdentidadErrors.UsuarioBloqueado : IdentidadErrors.CredencialesInvalidas);
            }

            var permisos = await db.Roles.Where(r => r.Id == supervisor.RolId).Select(r => r.Permisos).SingleAsync(ct);
            if (!permisos.Contains(c.Permiso)) return new Response(null, null, null, IdentidadErrors.SupervisorSinPermiso);

            var autorizacion = new AutorizacionSupervisor
            {
                Id = Guid.CreateVersion7(), SupervisorId = supervisor.Id, SolicitanteId = yo.UsuarioId, DispositivoId = dispositivoId,
                Permiso = c.Permiso, RecursoId = c.RecursoId, ExpiraUtc = ahora + AutorizacionSupervisor.Duracion,
            };
            db.Autorizaciones.Add(autorizacion);
            return new Response(autorizacion.Id, supervisor.Nombre, autorizacion.ExpiraUtc, null);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identidad/autorizaciones", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                {
                    var result = await h.Handle(c, ct);
                    if (result.IsFailure) return EndpointExtensions.Problema(result.Error!);
                    return result.Value.Fallo is { } fallo ? EndpointExtensions.Problema(fallo) : Results.Ok(result.Value);
                })
                .RequireAuthorization().WithTags("Identidad");
    }
}
