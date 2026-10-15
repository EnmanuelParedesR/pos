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
using Toki.Clientes.Contracts;
using Toki.Clientes.Domain;
using Toki.SharedKernel;

namespace Toki.Clientes.Features;

/// <summary>Creates (Id null) or updates a customer. Quick add from the register only needs a name.
/// Changing the credit limit or wholesale level needs fiado.gestionar.</summary>
internal static class GuardarCliente
{
    public sealed record Command(Guid? Id, string Nombre, string? RncCedula = null, string? Telefono = null, string? Direccion = null,
        bool CreditoFiscal = false, NivelPrecioCliente Nivel = NivelPrecioCliente.Detalle, decimal LimiteCredito = 0, int DiasCredito = 30, bool Activo = true)
        : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Nombre).NotEmpty().MaximumLength(150);
            RuleFor(c => c.RncCedula).Must(r => Rnc.EsValido(Rnc.Normalizar(r))).When(c => !string.IsNullOrWhiteSpace(c.RncCedula))
                .WithMessage("El RNC debe tener 9 dígitos o la cédula 11.");
            RuleFor(c => c.RncCedula).NotEmpty().When(c => c.CreditoFiscal).WithMessage("Para crédito fiscal hace falta el RNC o la cédula.");
            RuleFor(c => c.Telefono).MaximumLength(30);
            RuleFor(c => c.Direccion).MaximumLength(200);
            RuleFor(c => c.Nivel).IsInEnum();
            RuleFor(c => c.LimiteCredito).GreaterThanOrEqualTo(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.DiasCredito).InclusiveBetween(0, 365);
        }
    }

    internal sealed class Handler(ClientesDbContext db, IUserContext usuario, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var cliente = c.Id is { } id ? await db.Clientes.FirstOrDefaultAsync(x => x.Id == id, ct) : null;
            if (c.Id is not null && cliente is null) return ClientesErrors.ClienteNoEncontrado;
            var credito = (cliente?.LimiteCredito ?? 0) != c.LimiteCredito || (cliente?.Nivel ?? NivelPrecioCliente.Detalle) != c.Nivel;
            if (credito && !usuario.Tiene(Permisos.FiadoGestionar))
                return Error.Forbidden("Clientes.SinPermisoCredito", "Dar crédito o precio mayorista necesita el permiso de fiado.");
            var rnc = Rnc.Normalizar(c.RncCedula);
            if (rnc is not null && await db.Clientes.AnyAsync(x => x.RncCedula == rnc && x.Id != c.Id, ct)) return ClientesErrors.RncDuplicado;

            if (cliente is null) db.Clientes.Add(cliente = new Cliente { Id = Guid.CreateVersion7(), CreadoUtc = reloj.GetUtcNow().UtcDateTime });
            var antes = new { cliente.LimiteCredito, cliente.Nivel };
            cliente.Nombre = c.Nombre.Trim();
            cliente.RncCedula = rnc;
            cliente.Telefono = c.Telefono?.Trim();
            cliente.Direccion = c.Direccion?.Trim();
            cliente.CreditoFiscal = c.CreditoFiscal;
            cliente.Nivel = c.Nivel;
            cliente.LimiteCredito = c.LimiteCredito;
            cliente.DiasCredito = c.DiasCredito;
            cliente.Activo = c.Activo;
            await auditoria.RegistrarAsync("clientes.guardar", "Cliente", cliente.Id, new { antes, despues = c }, ct);
            return new Response(cliente.Id);
        }
    }

    internal sealed record Body(string Nombre, string? RncCedula = null, string? Telefono = null, string? Direccion = null, bool CreditoFiscal = false,
        NivelPrecioCliente Nivel = NivelPrecioCliente.Detalle, decimal LimiteCredito = 0, int DiasCredito = 30, bool Activo = true);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/clientes", async (Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(null, b.Nombre, b.RncCedula, b.Telefono, b.Direccion, b.CreditoFiscal, b.Nivel, b.LimiteCredito, b.DiasCredito, b.Activo), ct))
                    .ToHttp(r => Results.Created($"/api/clientes/{r.Id}", r)))
                .RequirePermiso(Permisos.ClientesGestionar).WithTags("Clientes");
            app.MapPut("/api/clientes/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Nombre, b.RncCedula, b.Telefono, b.Direccion, b.CreditoFiscal, b.Nivel, b.LimiteCredito, b.DiasCredito, b.Activo), ct))
                    .ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ClientesGestionar).WithTags("Clientes");
        }
    }
}

internal static class ConsultarClientes
{
    public sealed record Item(Guid Id, string Nombre, string? RncCedula, string? Telefono, NivelPrecioCliente Nivel, decimal LimiteCredito, bool Activo);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/clientes", async (string? buscar, ClientesDbContext db, CancellationToken ct) =>
                {
                    var q = db.Clientes.AsNoTracking().Where(c => c.Activo);
                    if (!string.IsNullOrWhiteSpace(buscar))
                    {
                        var rnc = Rnc.Normalizar(buscar);
                        q = q.Where(c => c.Nombre.Contains(buscar.Trim()) || c.RncCedula == rnc || c.Telefono == buscar.Trim());
                    }
                    return Results.Ok(await q.OrderBy(c => c.Nombre).Take(50)
                        .Select(c => new Item(c.Id, c.Nombre, c.RncCedula, c.Telefono, c.Nivel, c.LimiteCredito, c.Activo)).ToListAsync(ct));
                })
                .RequirePermiso(Permisos.ClientesGestionar).WithTags("Clientes");
            app.MapGet("/api/clientes/{id:guid}", async (Guid id, ICuentasClientes cuentas, CancellationToken ct) =>
                    await cuentas.ObtenerAsync(id, ct) is { } c ? Results.Ok(c) : EndpointExtensions.Problema(ClientesErrors.ClienteNoEncontrado))
                .RequirePermiso(Permisos.ClientesGestionar).WithTags("Clientes");
        }
    }
}

/// <summary>A customer pays on their credit at the register: the money enters the shift and goes to the oldest charges first.</summary>
internal static class RegistrarAbono
{
    public sealed record Command(Guid ClienteId, Guid TurnoId, decimal Monto, MedioPago Medio, string? Referencia = null) : ICommand<Response>;
    public sealed record Response(Guid AbonoId, decimal DeudaRestante);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Monto).GreaterThan(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.Medio).Must(m => m is MedioPago.Efectivo or MedioPago.Tarjeta or MedioPago.Transferencia).WithMessage("Un abono se paga en efectivo, tarjeta o transferencia.");
            RuleFor(c => c.Referencia).MaximumLength(60);
        }
    }

    internal sealed class Handler(ClientesDbContext db, ICajaOperaciones caja, IUserContext usuario, IOutbox outbox, IAuditoria auditoria, TimeProvider reloj)
        : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var abono = new AbonoFiado
            {
                Id = Guid.CreateVersion7(), ClienteId = c.ClienteId, FechaUtc = reloj.GetUtcNow().UtcDateTime, Monto = c.Monto, Origen = OrigenAbono.Caja,
                Medio = c.Medio, TurnoId = c.TurnoId, Referencia = c.Referencia?.Trim(), UsuarioId = usuario.UsuarioId,
            };
            // Shift first, then the customer: the same lock order as a sale.
            var enCaja = await caja.RegistrarAbonoClienteAsync(c.TurnoId, abono.Id, c.Medio, c.Monto, ct);
            if (enCaja.IsFailure) return enCaja.Error!;
            if (await db.BloquearAsync(c.ClienteId, ct) == 0) return ClientesErrors.ClienteNoEncontrado;

            var abiertos = await db.CargosAbiertosAsync(c.ClienteId, ct);
            var deuda = abiertos.Sum(x => x.Saldo);
            if (c.Monto > deuda) return ClientesErrors.AbonoMayorQueDeuda;

            var resto = c.Monto;
            foreach (var (cargo, saldo) in abiertos)
            {
                if (resto == 0) break;
                var monto = Math.Min(resto, saldo);
                resto -= monto;
                abono.Aplicaciones.Add(new AplicacionAbono { AbonoId = abono.Id, CargoId = cargo.Id, Monto = monto });
            }
            db.Abonos.Add(abono);
            outbox.Agregar(new AbonoFiadoRegistradoV1(c.ClienteId, abono.Id, c.Monto));
            await auditoria.RegistrarAsync("clientes.abono", "Cliente", c.ClienteId, new { Abono = abono.Id, c.Monto, c.Medio, c.TurnoId }, ct);
            return new Response(abono.Id, deuda - c.Monto);
        }
    }

    internal sealed record Body(Guid TurnoId, decimal Monto, MedioPago Medio, string? Referencia = null);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/clientes/{id:guid}/abonos", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.TurnoId, b.Monto, b.Medio, b.Referencia), ct)).ToHttp())
                .RequirePermiso(Permisos.CajaOperar).WithTags("Clientes");
    }
}

/// <summary>Customer statement: credit sales, payments and balance in favor, with the running debt.</summary>
internal static class EstadoCuentaCliente
{
    public sealed record Movimiento(DateTime FechaUtc, string Tipo, string? Documento, decimal Cargo, decimal Abono, decimal Deuda);
    public sealed record Response(Guid ClienteId, string Nombre, decimal Deuda, decimal SaldoFavor, decimal LimiteCredito, IReadOnlyList<Movimiento> Movimientos);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/clientes/{id:guid}/estado-cuenta", async (Guid id, ClientesDbContext db, CancellationToken ct) =>
                {
                    var c = await db.Clientes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (c is null) return EndpointExtensions.Problema(ClientesErrors.ClienteNoEncontrado);
                    var cargos = (await db.Cargos.Where(x => x.ClienteId == id && !x.Anulado).AsNoTracking().ToListAsync(ct))
                        .Select(x => (x.FechaUtc, Tipo: "Fiado", Doc: (string?)x.Documento, Cargo: x.Monto, Abono: 0m));
                    var abonos = (await db.Abonos.Where(x => x.ClienteId == id).AsNoTracking().ToListAsync(ct))
                        .Select(x => (x.FechaUtc, Tipo: x.Origen == OrigenAbono.Caja ? "Abono" : "Devolución", Doc: x.Referencia, Cargo: 0m, Abono: x.Monto));
                    var deuda = 0m;
                    var movimientos = cargos.Concat(abonos).OrderBy(m => m.FechaUtc)
                        .Select(m => new Movimiento(m.FechaUtc, m.Tipo, m.Doc, m.Cargo, m.Abono, deuda += m.Cargo - m.Abono)).ToList();
                    return Results.Ok(new Response(c.Id, c.Nombre, deuda, await db.SaldoFavorAsync(id, ct), c.LimiteCredito, movimientos));
                })
                .RequirePermiso(Permisos.ClientesGestionar).WithTags("Clientes");
    }
}

/// <summary>Accounts receivable by customer, aged by the age of each unpaid charge: 0–30, 31–60, 61–90 and over 90 days.</summary>
internal static class CuentasPorCobrar
{
    public sealed record Item(Guid ClienteId, string Cliente, decimal De0a30, decimal De31a60, decimal De61a90, decimal MasDe90, decimal Total, decimal LimiteCredito);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/clientes/por-cobrar", async (ClientesDbContext db, TimeProvider reloj, CancellationToken ct) =>
                {
                    var hoy = DiaNegocio.De(reloj.GetUtcNow().UtcDateTime);
                    var clientes = await db.Clientes.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
                    var ids = await db.Cargos.Where(c => !c.Anulado).Select(c => c.ClienteId).Distinct().ToListAsync(ct);
                    var items = new List<Item>();
                    foreach (var id in ids)
                    {
                        var abiertos = await db.CargosAbiertosAsync(id, ct);
                        if (abiertos.Count == 0) continue;
                        decimal Rango(int desde, int hasta) => abiertos.Where(a => hoy.DayNumber - DiaNegocio.De(a.Cargo.FechaUtc).DayNumber is var d && d >= desde && d <= hasta).Sum(a => a.Saldo);
                        var cliente = clientes[id];
                        items.Add(new Item(id, cliente.Nombre, Rango(0, 30), Rango(31, 60), Rango(61, 90), Rango(91, int.MaxValue), abiertos.Sum(a => a.Saldo), cliente.LimiteCredito));
                    }
                    return Results.Ok(items.OrderByDescending(i => i.Total));
                })
                .RequirePermiso(Permisos.FiadoGestionar).WithTags("Clientes");
    }
}
