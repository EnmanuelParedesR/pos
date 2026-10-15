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
/// Changing the credit limit or wholesale level, or reactivating a blocked customer, needs fiado.gestionar.</summary>
internal static class GuardarCliente
{
    public sealed record Command(Guid? Id, string Nombre, string? RncCedula = null, string? Telefono = null, string? Direccion = null,
        bool CreditoFiscal = false, NivelPrecioCliente Nivel = NivelPrecioCliente.Detalle, decimal LimiteCredito = 0, bool Activo = true)
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
        }
    }

    internal sealed class Handler(ClientesDbContext db, IUserContext usuario, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // Lock first on an update, so the audit head stays the last lock (a sale to this customer takes them in that order).
            if (c.Id is { } id && await db.BloquearAsync(id, ct) == 0) return ClientesErrors.ClienteNoEncontrado;
            var cliente = c.Id is { } existente ? await db.Clientes.SingleAsync(x => x.Id == existente, ct) : null;
            var credito = (cliente?.LimiteCredito ?? 0) != c.LimiteCredito || (cliente?.Nivel ?? NivelPrecioCliente.Detalle) != c.Nivel
                          || (cliente is { Activo: false } && c.Activo);
            if (credito && !usuario.Tiene(Permisos.FiadoGestionar)) return ClientesErrors.SinPermisoCredito;
            var rnc = Rnc.Normalizar(c.RncCedula);
            if (rnc is not null && await db.Clientes.AnyAsync(x => x.RncCedula == rnc && x.Id != c.Id, ct)) return ClientesErrors.RncDuplicado;

            if (cliente is null) db.Clientes.Add(cliente = new Cliente { Id = Guid.CreateVersion7(), CreadoUtc = reloj.GetUtcNow().UtcDateTime });
            var antes = new { cliente.LimiteCredito, cliente.Nivel, cliente.Activo };
            cliente.Nombre = c.Nombre.Trim();
            cliente.RncCedula = rnc;
            cliente.Telefono = c.Telefono?.Trim();
            cliente.Direccion = c.Direccion?.Trim();
            cliente.CreditoFiscal = c.CreditoFiscal;
            cliente.Nivel = c.Nivel;
            cliente.LimiteCredito = c.LimiteCredito;
            cliente.Activo = c.Activo;
            await auditoria.RegistrarAsync("clientes.guardar", "Cliente", cliente.Id, new { antes, despues = c }, ct);
            return new Response(cliente.Id);
        }
    }

    internal sealed record Body(string Nombre, string? RncCedula = null, string? Telefono = null, string? Direccion = null, bool CreditoFiscal = false,
        NivelPrecioCliente Nivel = NivelPrecioCliente.Detalle, decimal LimiteCredito = 0, bool Activo = true);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/clientes", async (Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(null, b.Nombre, b.RncCedula, b.Telefono, b.Direccion, b.CreditoFiscal, b.Nivel, b.LimiteCredito, b.Activo), ct))
                    .ToHttp(r => Results.Created($"/api/clientes/{r.Id}", r)))
                .RequirePermiso(Permisos.ClientesGestionar).WithTags("Clientes");
            app.MapPut("/api/clientes/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Nombre, b.RncCedula, b.Telefono, b.Direccion, b.CreditoFiscal, b.Nivel, b.LimiteCredito, b.Activo), ct))
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
            // Blocked customers are listed only for whoever may reactivate them.
            app.MapGet("/api/clientes", async (string? buscar, bool? inactivos, IUserContext usuario, ClientesDbContext db, CancellationToken ct) =>
                {
                    var q = db.Clientes.AsNoTracking();
                    if (inactivos != true || !usuario.Tiene(Permisos.FiadoGestionar)) q = q.Where(c => c.Activo);
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

/// <summary>A customer pays on their credit at the register: the money enters the shift and goes to the oldest charges first.
/// Cash comes in whole pesos; centavos above the debt (under RD$ 1) stay in the drawer as rounding, like a sale's change.</summary>
internal static class RegistrarAbono
{
    /// <param name="AbonoId">Generated by the register (GUID v7): a retry after a network drop returns the same payment instead of a second one.</param>
    public sealed record Command(Guid ClienteId, Guid AbonoId, Guid TurnoId, decimal Monto, MedioPago Medio, string? Referencia = null) : ICommand<Response>;
    public sealed record Response(Guid AbonoId, decimal Aplicado, decimal DeudaRestante);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.AbonoId).NotEmpty();
            RuleFor(c => c.Monto).GreaterThan(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.Monto).Must(m => m == Math.Truncate(m)).When(c => c.Medio == MedioPago.Efectivo).WithMessage("El efectivo se recibe en pesos enteros.");
            RuleFor(c => c.Medio).Must(m => m is MedioPago.Efectivo or MedioPago.Tarjeta or MedioPago.Transferencia).WithMessage("Un abono se paga en efectivo, tarjeta o transferencia.");
            RuleFor(c => c.Referencia).MaximumLength(60);
        }
    }

    internal sealed class Handler(ClientesDbContext db, ICajaOperaciones caja, IUserContext usuario, IOutbox outbox, IAuditoria auditoria, TimeProvider reloj)
        : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await GuardadoAsync(c, ct) is { } ya) return ya;
            // Shift first, then the customer: the same lock order as a sale. A concurrent retry waits here and then finds the payment.
            if (await caja.ObtenerTurnoAbiertoAsync(c.TurnoId, ct) is { IsFailure: true } turno) return turno.Error!;
            if (await db.BloquearAsync(c.ClienteId, ct) == 0) return ClientesErrors.ClienteNoEncontrado;
            if (await GuardadoAsync(c, ct) is { } repetido) return repetido;

            var abiertos = await db.CargosAbiertosAsync(c.ClienteId, ct);
            var deuda = abiertos.Sum(x => x.Saldo);
            var aplicado = Math.Min(c.Monto, deuda);
            var redondeo = c.Monto - aplicado;
            if (redondeo > 0 && (c.Medio != MedioPago.Efectivo || redondeo >= 1)) return ClientesErrors.AbonoMayorQueDeuda;

            var abono = new AbonoFiado
            {
                Id = c.AbonoId, ClienteId = c.ClienteId, FechaUtc = reloj.GetUtcNow().UtcDateTime, Monto = aplicado, Origen = OrigenAbono.Caja,
                Medio = c.Medio, TurnoId = c.TurnoId, Referencia = c.Referencia?.Trim(), UsuarioId = usuario.UsuarioId,
            };
            abono.Aplicaciones.AddRange(Asignacion.EnOrden(aplicado, abiertos)
                .Select(x => new AplicacionAbono { AbonoId = abono.Id, CargoId = x.Item.Id, Monto = x.Monto }));
            if (await caja.RegistrarAbonoClienteAsync(c.TurnoId, abono.Id, c.Medio, aplicado, redondeo, ct) is { IsFailure: true } enCaja) return enCaja.Error!;
            db.Abonos.Add(abono);
            outbox.Agregar(new AbonoFiadoRegistradoV1(c.ClienteId, abono.Id, aplicado));
            await auditoria.RegistrarAsync("clientes.abono", "Cliente", c.ClienteId, new { Abono = abono.Id, c.Monto, aplicado, c.Medio, c.TurnoId }, ct);
            return new Response(abono.Id, aplicado, deuda - aplicado);
        }

        private async Task<Response?> GuardadoAsync(Command c, CancellationToken ct) =>
            await db.Abonos.AsNoTracking().FirstOrDefaultAsync(a => a.Id == c.AbonoId, ct) is { } a
                ? new Response(a.Id, a.Monto, (await db.CargosAbiertosAsync(a.ClienteId, ct)).Sum(x => x.Saldo))
                : null;
    }

    internal sealed record Body(Guid AbonoId, Guid TurnoId, decimal Monto, MedioPago Medio, string? Referencia = null);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/clientes/{id:guid}/abonos", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.AbonoId, b.TurnoId, b.Monto, b.Medio, b.Referencia), ct)).ToHttp())
                .RequirePermiso(Permisos.CajaOperar).WithTags("Clientes");
    }
}

/// <summary>Voids a payment typed wrong at the register, while its shift is open: the money leaves the drawer and the charges are open again.
/// Needs fiado.gestionar or a supervisor.</summary>
internal static class AnularAbono
{
    public sealed record Command(Guid AbonoId, string Motivo, Guid? AutorizacionSupervisorId) : ICommand<Ok>, IRequiereAutorizacion
    {
        public string Permiso => Permisos.FiadoGestionar;
        public Guid RecursoId => AbonoId;
    }

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Motivo).NotEmpty().MaximumLength(200);
    }

    internal sealed class Handler(ClientesDbContext db, ICajaOperaciones caja, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var clienteId = await db.Abonos.Where(a => a.Id == c.AbonoId && a.Origen == OrigenAbono.Caja).Select(a => (Guid?)a.ClienteId).FirstOrDefaultAsync(ct);
            if (clienteId is null) return ClientesErrors.AbonoNoEncontrado;
            // Shift (fails when it is closed), then the customer, where the state is checked again: a second void rolls back here.
            if (await caja.AnularMovimientosAsync(c.AbonoId, ct) is { IsFailure: true } enCaja) return enCaja.Error!;
            await db.BloquearAsync(clienteId.Value, ct);
            var abono = await db.Abonos.Include(a => a.Aplicaciones).SingleAsync(a => a.Id == c.AbonoId, ct);
            if (abono.Anulado) return ClientesErrors.AbonoYaAnulado;
            var cargos = abono.Aplicaciones.Select(a => a.CargoId).ToList();
            if (await db.Cargos.AnyAsync(x => cargos.Contains(x.Id) && x.Anulado, ct)) return ClientesErrors.AbonoConFacturaAnulada;

            abono.Anulado = true;
            abono.AnuladoUtc = reloj.GetUtcNow().UtcDateTime;
            abono.MotivoAnulacion = c.Motivo.Trim();
            await auditoria.RegistrarAsync("clientes.anular-abono", "Cliente", abono.ClienteId, new { Abono = abono.Id, abono.Monto, c.Motivo }, ct);
            return Ok.Valor;
        }
    }

    internal sealed record Body(string Motivo, Guid? AutorizacionSupervisorId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/clientes/abonos/{id:guid}/anular", async (Guid id, Body b, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Motivo, b.AutorizacionSupervisorId), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.CajaOperar).WithTags("Clientes");
    }
}

/// <summary>Customer statement with the running debt. Voided sales and payments show the original line and its reversal,
/// so the running debt always ends at the debt on the customer card.</summary>
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
                    var lineas = new List<(DateTime Fecha, string Tipo, string? Doc, decimal Cargo, decimal Abono)>();
                    foreach (var x in await db.Cargos.Where(x => x.ClienteId == id).AsNoTracking().ToListAsync(ct))
                    {
                        lineas.Add((x.FechaUtc, "Fiado", x.Documento, x.Monto, 0));
                        if (x.AnuladoUtc is not { } anulado) continue;
                        lineas.Add((anulado, "Venta anulada", x.Documento, 0, x.Monto));
                        if (x.PagadoAlAnular > 0) lineas.Add((anulado, "Lo pagado de la venta anulada", x.Documento, x.PagadoAlAnular, 0));
                    }
                    foreach (var a in await db.Abonos.Where(x => x.ClienteId == id).AsNoTracking().ToListAsync(ct))
                    {
                        lineas.Add((a.FechaUtc, a.Origen switch { OrigenAbono.Caja => "Abono", OrigenAbono.Devolucion => "Devolución", _ => "Pago de venta anulada" },
                            a.Referencia, 0, a.Monto));
                        if (a.AnuladoUtc is { } anulado) lineas.Add((anulado, "Abono anulado", a.Referencia, a.Monto, 0));
                    }
                    var deuda = 0m;
                    var movimientos = lineas.OrderBy(m => m.Fecha).Select(m => new Movimiento(m.Fecha, m.Tipo, m.Doc, m.Cargo, m.Abono, deuda += m.Cargo - m.Abono)).ToList();
                    var abierta = (await db.CargosAbiertosAsync(id, ct)).Sum(x => x.Saldo);
                    return Results.Ok(new Response(c.Id, c.Nombre, abierta, await db.SaldoFavorAsync(id, ct), c.LimiteCredito, movimientos));
                })
                .RequirePermiso(Permisos.ClientesGestionar).WithTags("Clientes");
    }
}

/// <summary>Accounts receivable by customer, aged by the business day of each unpaid charge: 0–30, 31–60, 61–90 and over 90 days.</summary>
internal static class CuentasPorCobrar
{
    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/clientes/por-cobrar", async (IClientesReportes reportes, CancellationToken ct) => Results.Ok(await reportes.PorCobrarAsync(ct)))
                .RequirePermiso(Permisos.FiadoGestionar).WithTags("Clientes");
    }
}

internal sealed class ClientesReportes(ClientesDbContext db, TimeProvider reloj) : IClientesReportes
{
    public async Task<IReadOnlyList<CuentaPorCobrarCliente>> PorCobrarAsync(CancellationToken ct)
    {
        var hoy = DiaNegocio.De(reloj.GetUtcNow().UtcDateTime);
        var clientes = await db.Clientes.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        return (await db.CargosAbiertosAsync(null, ct)).GroupBy(a => a.Cargo.ClienteId).Select(g =>
        {
            decimal Rango(int desde, int hasta) => g.Where(a => hoy.DayNumber - DiaNegocio.De(a.Cargo.FechaUtc).DayNumber is var d && d >= desde && d <= hasta).Sum(a => a.Saldo);
            var cliente = clientes[g.Key];
            return new CuentaPorCobrarCliente(g.Key, cliente.Nombre, Rango(int.MinValue, 30), Rango(31, 60), Rango(61, 90), Rango(91, int.MaxValue), g.Sum(a => a.Saldo),
                cliente.LimiteCredito);
        }).OrderByDescending(i => i.Total).ToList();
    }
}
