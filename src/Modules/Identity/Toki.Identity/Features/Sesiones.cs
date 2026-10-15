using System.Security.Claims;
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

/// <summary>Cashier logs in on a paired device with code + PIN.</summary>
internal static class IniciarSesion
{
    public sealed record Command(string Codigo, string Pin) : ICommand<Response>
    {
        public string TokenDispositivo { get; init; } = "";
    }

    /// <summary>A wrong PIN is a successful command (the failed attempt must be saved); <see cref="Fallo"/> carries the error.</summary>
    public sealed record Response(string? Token, Guid? UsuarioId, string? Nombre, DateTime? ExpiraUtc, Error? Fallo);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Codigo).NotEmpty().MaximumLength(20);
            RuleFor(c => c.Pin).NotEmpty().MaximumLength(8);
        }
    }

    internal sealed class Handler(IdentidadDbContext db, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var dispositivoHash = Token.Hash(c.TokenDispositivo);
            var dispositivo = await db.Dispositivos.AsNoTracking().FirstOrDefaultAsync(d => d.TokenHash == dispositivoHash && !d.Revocado, ct);
            if (dispositivo is null) return IdentidadErrors.DispositivoNoEmparejado;

            var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Codigo == Usuario.NormalizarCodigo(c.Codigo), ct);
            if (usuario is null) return IdentidadErrors.CredencialesInvalidas;

            var ahora = reloj.GetUtcNow().UtcDateTime;
            var resultado = usuario.IntentarPin(c.Pin, ahora);
            if (resultado != ResultadoPin.Correcto)
            {
                await auditoria.RegistrarAsync("identidad.pin-fallido", "Usuario", usuario.Id, new { usuario.Codigo, Dispositivo = dispositivo.Id, Resultado = resultado }, ct);
                return new Response(null, null, null, null, resultado == ResultadoPin.Bloqueado ? IdentidadErrors.UsuarioBloqueado : IdentidadErrors.CredencialesInvalidas);
            }

            var token = Token.Nuevo();
            var sesion = new Sesion
            {
                Id = Guid.CreateVersion7(), UsuarioId = usuario.Id, DispositivoId = dispositivo.Id,
                TokenHash = Token.Hash(token), CreadaUtc = ahora, ExpiraUtc = ahora + Sesion.Duracion,
            };
            db.Sesiones.Add(sesion);
            return new Response(token, usuario.Id, usuario.Nombre, sesion.ExpiraUtc, null);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identidad/sesiones", async (Command c, HttpRequest req, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                {
                    var result = await h.Handle(c with { TokenDispositivo = req.Headers[IdentidadModule.HeaderDispositivo].ToString() }, ct);
                    if (result.IsFailure) return EndpointExtensions.Problema(result.Error!);
                    return result.Value.Fallo is { } fallo ? EndpointExtensions.Problema(fallo) : Results.Ok(result.Value);
                })
                .AllowAnonymous().WithTags("Identidad");
    }
}

internal static class CerrarSesion
{
    public sealed record Command(Guid SesionId) : ICommand<Ok>;

    internal sealed class Handler(IdentidadDbContext db, TimeProvider reloj) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var ahora = reloj.GetUtcNow().UtcDateTime;
            await db.Sesiones.Where(s => s.Id == c.SesionId && s.CerradaUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CerradaUtc, ahora), ct);
            return Ok.Valor;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapDelete("/api/identidad/sesiones/actual", async (ClaimsPrincipal user, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(Guid.Parse(user.FindFirstValue(TokiClaims.Sesion)!)), ct)).ToHttp(_ => Results.NoContent()))
                .RequireAuthorization().WithTags("Identidad");
    }
}

/// <summary>Who am I and what can I do. The UI shows or hides menus with this; the server still checks every call.</summary>
internal static class Yo
{
    public sealed record Response(Guid UsuarioId, string Nombre, Guid? DispositivoId, IReadOnlyList<string> Permisos);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/identidad/yo", (ClaimsPrincipal user, IUserContext u) =>
                    Results.Ok(new Response(u.UsuarioId, u.Nombre, u.DispositivoId, user.FindAll(TokiClaims.Permiso).Select(c => c.Value).ToList())))
                .RequireAuthorization().WithTags("Identidad");
    }
}

/// <summary>A user changes their own PIN.</summary>
internal static class CambiarMiPin
{
    public sealed record Command(string PinActual, string PinNuevo) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.PinActual).NotEmpty().MaximumLength(8);
            RuleFor(c => c.PinNuevo).Must(Domain.Pin.EsValido).WithMessage("El PIN debe tener de 4 a 8 dígitos.");
        }
    }

    internal sealed class Handler(IdentidadDbContext db, IUserContext yo, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var usuario = await db.Usuarios.FirstAsync(u => u.Id == yo.UsuarioId, ct);
            if (!Domain.Pin.Verificar(usuario.PinHash, c.PinActual)) return IdentidadErrors.CredencialesInvalidas;
            usuario.CambiarPin(c.PinNuevo);
            // A new PIN ends every other session of this user (someone else may have been using it).
            var ahora = reloj.GetUtcNow().UtcDateTime;
            await db.Sesiones.Where(s => s.UsuarioId == usuario.Id && s.CerradaUtc == null && s.Id != yo.SesionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CerradaUtc, ahora), ct);
            await auditoria.RegistrarAsync("identidad.cambiar-pin", "Usuario", usuario.Id, new { }, ct);
            return Ok.Valor;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identidad/yo/pin", async (Command c, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequireAuthorization().WithTags("Identidad");
    }
}
