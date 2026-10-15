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

/// <summary>An administrator opens a 10-minute pairing window for a new register.</summary>
internal static class CrearCodigoEmparejamiento
{
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(10);

    public sealed record Command(string NombreDispositivo) : ICommand<Response>;
    public sealed record Response(string Codigo, DateTime ExpiraUtc);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.NombreDispositivo).NotEmpty().MaximumLength(100);
    }

    internal sealed class Handler(IdentidadDbContext db, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // shortcut: 8 random digits valid 10 minutes, no attempt counter; fine on a shop LAN, replace with mTLS pairing (TK-091).
            var codigo = Token.CodigoCorto() + Token.CodigoCorto()[..2];
            var expira = reloj.GetUtcNow().UtcDateTime + Vigencia;
            db.CodigosEmparejamiento.Add(new CodigoEmparejamiento
            {
                Id = Guid.CreateVersion7(), NombreDispositivo = c.NombreDispositivo.Trim(), CodigoHash = Token.Hash(codigo), ExpiraUtc = expira,
            });
            return Task.FromResult<Result<Response>>(new Response(codigo, expira));
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identidad/dispositivos/codigos", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp())
                .RequirePermiso(Permisos.DispositivosGestionar).WithTags("Identidad");
    }
}

/// <summary>The new register sends the code and receives its device secret.</summary>
internal static class EmparejarDispositivo
{
    public sealed record Command(string Codigo) : ICommand<Response>;
    public sealed record Response(Guid DispositivoId, string Nombre, string Token);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Codigo).NotEmpty().MaximumLength(20);
    }

    internal sealed class Handler(IdentidadDbContext db, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var ahora = reloj.GetUtcNow().UtcDateTime;
            var hash = Token.Hash(c.Codigo.Trim());
            var vigente = db.CodigosEmparejamiento.Where(x => x.CodigoHash == hash && x.UsadoUtc == null && x.ExpiraUtc > ahora);
            var codigo = await vigente.AsNoTracking().FirstOrDefaultAsync(ct);
            // Claim it with a conditional update: two registers racing with the same code cannot both pair.
            if (codigo is null || await vigente.ExecuteUpdateAsync(s => s.SetProperty(x => x.UsadoUtc, ahora), ct) == 0)
                return IdentidadErrors.CodigoEmparejamientoInvalido;

            var token = Token.Nuevo();
            var dispositivo = new Dispositivo { Id = Guid.CreateVersion7(), Nombre = codigo.NombreDispositivo, TokenHash = Token.Hash(token), CreadoUtc = ahora };
            db.Dispositivos.Add(dispositivo);
            await auditoria.RegistrarAsync("identidad.emparejar", "Dispositivo", dispositivo.Id, new { dispositivo.Nombre }, ct);
            return new Response(dispositivo.Id, dispositivo.Nombre, token);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identidad/dispositivos/emparejar", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp())
                .AllowAnonymous().WithTags("Identidad");
    }
}

internal static class ListarDispositivos
{
    public sealed record Item(Guid Id, string Nombre, bool Revocado, DateTime CreadoUtc);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/identidad/dispositivos", async (IdentidadDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.Dispositivos.OrderBy(d => d.Nombre)
                        .Select(d => new Item(d.Id, d.Nombre, d.Revocado, d.CreadoUtc)).AsNoTracking().ToListAsync(ct)))
                .RequirePermiso(Permisos.DispositivosGestionar).WithTags("Identidad");
    }
}

/// <summary>Revoking is immediate: its sessions close and its secret stops working.</summary>
internal static class RevocarDispositivo
{
    public sealed record Command(Guid Id) : ICommand<Ok>;

    internal sealed class Handler(IdentidadDbContext db, IAuditoria auditoria, IUserContext yo, TimeProvider reloj) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            if (c.Id == yo.DispositivoId) return Error.Validation("Identidad.NoAutoRevocar", "No puede revocar el equipo que está usando.");
            var dispositivo = await db.Dispositivos.FirstOrDefaultAsync(d => d.Id == c.Id, ct);
            if (dispositivo is null) return IdentidadErrors.DispositivoNoEncontrado;
            dispositivo.Revocado = true;
            var ahora = reloj.GetUtcNow().UtcDateTime;
            await db.Sesiones.Where(s => s.DispositivoId == c.Id && s.CerradaUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.CerradaUtc, ahora), ct);
            await auditoria.RegistrarAsync("identidad.revocar", "Dispositivo", dispositivo.Id, new { dispositivo.Nombre }, ct);
            return Ok.Valor;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/identidad/dispositivos/{id:guid}/revocar", async (Guid id, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.DispositivosGestionar).WithTags("Identidad");
    }
}
