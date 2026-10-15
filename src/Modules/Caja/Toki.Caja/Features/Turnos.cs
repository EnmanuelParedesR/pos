using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Caja.Contracts;
using Toki.Caja.Domain;
using Toki.SharedKernel;

namespace Toki.Caja.Features;

internal static class Cajas
{
    public sealed record Crear(string Nombre) : ICommand<Response>;
    public sealed record Response(Guid Id);
    public sealed record Item(Guid Id, string Nombre, bool Activa, Guid? TurnoAbiertoId, string? Cajero);

    internal sealed class Validator : AbstractValidator<Crear>
    {
        public Validator() => RuleFor(c => c.Nombre).NotEmpty().MaximumLength(60);
    }

    internal sealed class Handler(CajaDbContext db, IAuditoria auditoria) : ICommandHandler<Crear, Response>
    {
        public async Task<Result<Response>> Handle(Crear c, CancellationToken ct)
        {
            var caja = new CajaFisica { Id = Guid.CreateVersion7(), Nombre = c.Nombre.Trim() };
            db.Cajas.Add(caja);
            await auditoria.RegistrarAsync("caja.crear-caja", "Caja", caja.Id, c, ct);
            return new Response(caja.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/caja/cajas", async (Crear c, ICommandHandler<Crear, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/caja/cajas/{r.Id}", r)))
                .RequirePermiso(Permisos.ConfiguracionGestionar).WithTags("Caja");
            app.MapGet("/api/caja/cajas", async (CajaDbContext db, CancellationToken ct) =>
                    Results.Ok(await (from c in db.Cajas
                                      join t in db.Turnos.Where(t => t.Estado == EstadoTurno.Abierto) on c.Id equals t.CajaId into abiertos
                                      from t in abiertos.DefaultIfEmpty()
                                      orderby c.Nombre
                                      select new Item(c.Id, c.Nombre, c.Activa, t == null ? null : t.Id, t == null ? null : t.CajeroNombre))
                        .AsNoTracking().ToListAsync(ct)))
                .RequireAuthorization().WithTags("Caja");
        }
    }
}

/// <summary>A cashier opens a shift on a physical register with the opening float.</summary>
internal static class AbrirTurno
{
    public sealed record Command(Guid CajaId, decimal FondoInicial) : ICommand<Response>;
    public sealed record Response(Guid TurnoId);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.FondoInicial).GreaterThanOrEqualTo(0).PrecisionScale(19, 2, true);
    }

    internal sealed class Handler(CajaDbContext db, IUserContext usuario, IOutbox outbox, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (!await db.Cajas.AnyAsync(x => x.Id == c.CajaId && x.Activa, ct)) return CajaErrors.CajaNoEncontrada;
            if (await db.Turnos.AnyAsync(t => t.CajaId == c.CajaId && t.Estado == EstadoTurno.Abierto, ct)) return CajaErrors.CajaOcupada;
            if (await db.Turnos.AnyAsync(t => t.CajeroId == usuario.UsuarioId && t.Estado == EstadoTurno.Abierto, ct)) return CajaErrors.CajeroConTurno;

            var turno = new Turno
            {
                Id = Guid.CreateVersion7(), CajaId = c.CajaId, CajeroId = usuario.UsuarioId, CajeroNombre = usuario.Nombre,
                AbiertoUtc = reloj.GetUtcNow().UtcDateTime, FondoInicial = c.FondoInicial, Estado = EstadoTurno.Abierto,
            };
            db.Turnos.Add(turno);
            outbox.Agregar(new TurnoAbiertoV1(turno.Id, turno.CajaId, turno.CajeroId, turno.FondoInicial));
            await auditoria.RegistrarAsync("caja.abrir-turno", "Turno", turno.Id, c, ct);
            return new Response(turno.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/caja/turnos", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/caja/turnos/{r.TurnoId}", r)))
                .RequirePermiso(Permisos.CajaOperar).WithTags("Caja");
    }
}

/// <summary>The caller's open shift (what the register shows after login). 404 when there is none.</summary>
internal static class TurnoActual
{
    public sealed record Response(Guid TurnoId, Guid CajaId, string Caja, decimal FondoInicial, DateTime AbiertoUtc);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/caja/turnos/actual", async (IUserContext usuario, CajaDbContext db, CancellationToken ct) =>
                {
                    var t = await (from turno in db.Turnos
                                   join caja in db.Cajas on turno.CajaId equals caja.Id
                                   where turno.CajeroId == usuario.UsuarioId && turno.Estado == EstadoTurno.Abierto
                                   select new Response(turno.Id, caja.Id, caja.Nombre, turno.FondoInicial, turno.AbiertoUtc)).AsNoTracking().FirstOrDefaultAsync(ct);
                    return t is null ? EndpointExtensions.Problema(Error.NotFound("Caja.SinTurno", "No tiene un turno abierto.")) : Results.Ok(t);
                })
                .RequirePermiso(Permisos.CajaOperar).WithTags("Caja");
    }
}

/// <summary>Cash in, withdrawal (cash drop) or petty expense. Withdrawals and expenses need a supervisor.</summary>
internal static class RegistrarMovimiento
{
    public enum Tipo { Entrada, Retiro, Gasto }

    public sealed record Command(Guid TurnoId, Tipo TipoMovimiento, decimal Monto, MotivoMovimiento Motivo, string? Nota, Guid? AutorizacionSupervisorId)
        : ICommand<Response>, IRequiereAutorizacion
    {
        public string Permiso => TipoMovimiento == Tipo.Entrada ? Permisos.CajaOperar : Permisos.CajaRetirar;
        public Guid RecursoId => TurnoId;
    }

    public sealed record Response(Guid MovimientoId);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.TipoMovimiento).IsInEnum();
            RuleFor(c => c.Motivo).IsInEnum();
            RuleFor(c => c.Monto).GreaterThan(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.Nota).MaximumLength(200);
            RuleFor(c => c.Nota).NotEmpty().When(c => c.Motivo == MotivoMovimiento.Otro).WithMessage("Explique el motivo.");
        }
    }

    internal sealed class Handler(ICajaOperaciones operaciones, IAuditoria auditoria) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var turno = await operaciones.ObtenerTurnoAbiertoAsync(c.TurnoId, ct);
            if (turno.IsFailure) return turno.Error!;
            var ops = (CajaOperaciones)operaciones;
            if (c.TipoMovimiento != Tipo.Entrada && await ops.EfectivoEsperadoAsync(c.TurnoId, ct) < c.Monto) return CajaErrors.SinEfectivo;

            var tipo = c.TipoMovimiento switch
            {
                Tipo.Entrada => TipoMovimientoCaja.Entrada,
                Tipo.Retiro => TipoMovimientoCaja.Retiro,
                _ => TipoMovimientoCaja.Gasto,
            };
            var m = ops.Agregar(c.TurnoId, tipo, MedioPago.Efectivo, c.Monto, null, null, c.Motivo, c.Nota?.Trim());
            await auditoria.RegistrarAsync("caja." + tipo.ToString().ToLowerInvariant(), "Turno", c.TurnoId, new { c.Monto, c.Motivo, c.Nota }, ct);
            return new Response(m.Id);
        }
    }

    internal sealed record Body(Tipo TipoMovimiento, decimal Monto, MotivoMovimiento Motivo, string? Nota, Guid? AutorizacionSupervisorId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/caja/turnos/{id:guid}/movimientos", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.TipoMovimiento, b.Monto, b.Motivo, b.Nota, b.AutorizacionSupervisorId), ct)).ToHttp())
                .RequirePermiso(Permisos.CajaOperar).WithTags("Caja");
    }
}

/// <summary>Blind closing: the cashier counts bills and coins without seeing the expected cash; a supervisor approves.
/// Returns the Z report with the difference.</summary>
internal static class CerrarTurno
{
    public sealed record Command(Guid TurnoId, IReadOnlyList<ConteoDenominacion> Conteo, Guid? AutorizacionSupervisorId) : ICommand<ReporteZ>, IRequiereAutorizacion
    {
        public string Permiso => Permisos.CajaAprobarCierre;
        public Guid RecursoId => TurnoId;
    }

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Conteo).NotNull();
            RuleForEach(c => c.Conteo).ChildRules(d =>
            {
                d.RuleFor(x => x.Denominacion).Must(x => Denominaciones.Dop.Contains(x)).WithMessage("Esa denominación no existe.");
                d.RuleFor(x => x.Cantidad).GreaterThanOrEqualTo(0);
            });
            RuleFor(c => c.Conteo).Must(c => c.Select(x => x.Denominacion).Distinct().Count() == c.Count).WithMessage("Hay denominaciones repetidas.");
        }
    }

    internal sealed class Handler(CajaDbContext db, ICajaOperaciones operaciones, IUserContext usuario, IOutbox outbox, IAuditoria auditoria, TimeProvider reloj)
        : ICommandHandler<Command, ReporteZ>
    {
        public async Task<Result<ReporteZ>> Handle(Command c, CancellationToken ct)
        {
            var abierto = await operaciones.ObtenerTurnoAbiertoAsync(c.TurnoId, ct); // locks the shift: in-flight sales finish first
            if (abierto.IsFailure) return abierto.Error!;

            var turno = await db.Turnos.SingleAsync(t => t.Id == c.TurnoId, ct);
            var movimientos = await db.Movimientos.Where(m => m.TurnoId == c.TurnoId).AsNoTracking().ToListAsync(ct);
            var conteo = c.Conteo.Where(x => x.Cantidad > 0).OrderByDescending(x => x.Denominacion).ToList();
            var ultimoZ = await db.Turnos.Where(t => t.CajaId == turno.CajaId && t.NumeroZ != null).MaxAsync(t => t.NumeroZ, ct) ?? 0;

            turno.Estado = EstadoTurno.Cerrado;
            turno.CerradoUtc = reloj.GetUtcNow().UtcDateTime;
            turno.EfectivoEsperado = CuadreTurno.EfectivoEsperado(turno.FondoInicial, movimientos);
            turno.EfectivoContado = CuadreTurno.Contado(conteo);
            turno.Diferencia = turno.EfectivoContado - turno.EfectivoEsperado;
            turno.SupervisorCierreId = usuario.SupervisorId ?? usuario.UsuarioId;
            turno.Conteo = JsonSerializer.Serialize(conteo, Json.Opciones);
            turno.NumeroZ = ultimoZ + 1;

            outbox.Agregar(new TurnoCerradoV1(turno.Id, turno.CajaId, turno.CajeroId, turno.NumeroZ.Value, turno.EfectivoEsperado.Value,
                turno.EfectivoContado.Value, turno.Diferencia.Value));
            await auditoria.RegistrarAsync("caja.cerrar-turno", "Turno", turno.Id,
                new { turno.NumeroZ, turno.EfectivoEsperado, turno.EfectivoContado, turno.Diferencia }, ct);
            return ReporteZ.Construir(turno, abierto.Value.Caja, movimientos, conteo);
        }
    }

    internal sealed record Body(IReadOnlyList<ConteoDenominacion> Conteo, Guid? AutorizacionSupervisorId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/caja/turnos/{id:guid}/cerrar", async (Guid id, Body b, ICommandHandler<Command, ReporteZ> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Conteo, b.AutorizacionSupervisorId), ct)).ToHttp())
                .RequirePermiso(Permisos.CajaOperar).WithTags("Caja");
    }
}

/// <summary>Z report of a closed shift (its cashier or a supervisor), or X report of an open one (supervisor only: the count stays blind).</summary>
internal static class ObtenerReporteZ
{
    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/caja/turnos/{id:guid}/z", async (Guid id, IUserContext usuario, CajaDbContext db, CancellationToken ct) =>
                {
                    var t = await db.Turnos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (t is null) return EndpointExtensions.Problema(CajaErrors.TurnoNoEncontrado);
                    var supervisor = usuario.Tiene(Permisos.CajaAprobarCierre) || usuario.Tiene(Permisos.ReportesVer);
                    if (!supervisor && (t.CajeroId != usuario.UsuarioId || t.Estado == EstadoTurno.Abierto))
                        return Results.Forbid();
                    var caja = await db.Cajas.Where(c => c.Id == t.CajaId).Select(c => c.Nombre).SingleAsync(ct);
                    var movimientos = await db.Movimientos.Where(m => m.TurnoId == id).AsNoTracking().ToListAsync(ct);
                    var conteo = t.Conteo is null ? null : JsonSerializer.Deserialize<List<ConteoDenominacion>>(t.Conteo, Json.Opciones);
                    return Results.Ok(ReporteZ.Construir(t, caja, movimientos, conteo));
                })
                .RequireAuthorization().WithTags("Caja");
    }
}

internal static class ListarTurnos
{
    public sealed record Item(Guid Id, string Caja, string Cajero, DateTime AbiertoUtc, DateTime? CerradoUtc, int? NumeroZ,
        decimal FondoInicial, decimal? EfectivoEsperado, decimal? EfectivoContado, decimal? Diferencia);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/caja/turnos", async (DateOnly desde, DateOnly hasta, CajaDbContext db, CancellationToken ct) =>
                {
                    var (d, h) = DiaNegocio.RangoUtc(desde, hasta);
                    return Results.Ok(await (from t in db.Turnos
                                             join c in db.Cajas on t.CajaId equals c.Id
                                             where t.AbiertoUtc >= d && t.AbiertoUtc < h
                                             orderby t.AbiertoUtc
                                             select new Item(t.Id, c.Nombre, t.CajeroNombre, t.AbiertoUtc, t.CerradoUtc, t.NumeroZ, t.FondoInicial,
                                                 t.EfectivoEsperado, t.EfectivoContado, t.Diferencia)).AsNoTracking().ToListAsync(ct));
                })
                .RequirePermiso(Permisos.ReportesVer).WithTags("Caja");
    }
}

internal static class ListarDenominaciones
{
    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/caja/denominaciones", () => Results.Ok(Denominaciones.Dop)).RequireAuthorization().WithTags("Caja");
    }
}
