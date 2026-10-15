using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Compras.Contracts;
using Toki.Compras.Domain;
using Toki.SharedKernel;

namespace Toki.Compras.Features;

/// <summary>A supplier invoice without merchandise (electricity, rent, transport) with its NCF and ITBIS. On credit it becomes a payable.</summary>
internal static class RegistrarGasto
{
    public sealed record Command(Guid SuplidorId, string Ncf, DateOnly Fecha, string Concepto, TipoGasto Tipo, decimal Subtotal, decimal Itbis,
        Condicion Condicion, int? DiasCredito = null) : ICommand<Response>;
    public sealed record Response(Guid Id, decimal Total);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Ncf).Must(Domain.Ncf.EsValido).WithMessage("El NCF no es válido (B01… de 11 caracteres o E31… de 13).");
            RuleFor(c => c.Concepto).NotEmpty().MaximumLength(150);
            RuleFor(c => c.Tipo).IsInEnum();
            RuleFor(c => c.Subtotal).GreaterThan(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.Itbis).GreaterThanOrEqualTo(0).PrecisionScale(19, 2, true).LessThanOrEqualTo(c => c.Subtotal * 0.18m + 0.01m)
                .WithMessage("El ITBIS no puede pasar del 18 % del subtotal.");
            RuleFor(c => c.Condicion).IsInEnum();
            RuleFor(c => c.DiasCredito).InclusiveBetween(0, 365).When(c => c.DiasCredito is not null);
        }
    }

    internal sealed class Handler(ComprasDbContext db, IUserContext usuario, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var suplidor = await db.Suplidores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == c.SuplidorId && s.Activo, ct);
            if (suplidor is null) return ComprasErrors.SuplidorNoEncontrado;
            var ncf = c.Ncf.Trim().ToUpperInvariant();
            if (await db.Gastos.AnyAsync(g => g.SuplidorId == c.SuplidorId && g.Ncf == ncf && !g.Anulado, ct)) return ComprasErrors.NcfRepetido;

            var gasto = new Gasto
            {
                Id = Guid.CreateVersion7(), SuplidorId = suplidor.Id, SuplidorNombre = suplidor.Nombre, RncSuplidor = suplidor.Rnc, Ncf = ncf, Fecha = c.Fecha,
                Concepto = c.Concepto.Trim(), Tipo = c.Tipo, Condicion = c.Condicion, Subtotal = c.Subtotal, Itbis = c.Itbis, Total = c.Subtotal + c.Itbis,
                CreadoUtc = reloj.GetUtcNow().UtcDateTime, UsuarioId = usuario.UsuarioId,
            };
            if (c.Condicion == Condicion.Credito)
            {
                gasto.Vence = c.Fecha.AddDays(c.DiasCredito ?? suplidor.DiasCredito);
                db.CuentasPorPagar.Add(new CuentaPorPagar
                {
                    Id = Guid.CreateVersion7(), SuplidorId = suplidor.Id, Tipo = TipoDocumento.Gasto, DocumentoId = gasto.Id, Ncf = ncf,
                    Fecha = c.Fecha, Vence = gasto.Vence.Value, Total = gasto.Total,
                });
            }
            db.Gastos.Add(gasto);
            await auditoria.RegistrarAsync("compras.gasto", "Gasto", gasto.Id, c, ct);
            return new Response(gasto.Id, gasto.Total);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/compras/gastos", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/compras/gastos/{r.Id}", r)))
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
            app.MapGet("/api/compras/gastos", async (DateOnly desde, DateOnly hasta, ComprasDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.Gastos.Where(g => g.Fecha >= desde && g.Fecha <= hasta).OrderBy(g => g.Fecha).AsNoTracking().ToListAsync(ct)))
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
        }
    }
}

/// <summary>Pays a supplier. The amount goes to the oldest open invoices first (or to the ones chosen) and never beyond what is owed.</summary>
internal static class PagarSuplidor
{
    public sealed record Command(Guid SuplidorId, decimal Monto, MedioPagoSuplidor Medio, DateOnly Fecha, string? Referencia = null,
        IReadOnlyList<Guid>? Cuentas = null) : ICommand<Response>;
    public sealed record Aplicado(Guid CuentaId, string Ncf, decimal Monto, decimal SaldoRestante);
    public sealed record Response(Guid PagoId, IReadOnlyList<Aplicado> Aplicaciones);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Monto).GreaterThan(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.Medio).IsInEnum();
            RuleFor(c => c.Referencia).MaximumLength(60);
        }
    }

    internal sealed class Handler(ComprasDbContext db, IUserContext usuario, IOutbox outbox, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await db.BloquearSuplidorAsync(c.SuplidorId, ct) == 0) return ComprasErrors.SuplidorNoEncontrado;
            var abiertas = await db.AbiertasAsync(c.SuplidorId, ct);
            if (c.Cuentas is { Count: > 0 } elegidas) abiertas = abiertas.Where(a => elegidas.Contains(a.Cuenta.Id)).ToList();
            if (c.Monto > abiertas.Sum(a => a.Saldo)) return ComprasErrors.PagoMayorQueDeuda;

            var pago = new PagoSuplidor
            {
                Id = Guid.CreateVersion7(), SuplidorId = c.SuplidorId, Fecha = c.Fecha, Monto = c.Monto, Medio = c.Medio, Referencia = c.Referencia?.Trim(),
                CreadoUtc = reloj.GetUtcNow().UtcDateTime, UsuarioId = usuario.UsuarioId,
            };
            var resto = c.Monto;
            var aplicados = new List<Aplicado>();
            foreach (var (cuenta, saldo) in abiertas)
            {
                if (resto == 0) break;
                var monto = Math.Min(resto, saldo);
                resto -= monto;
                pago.Aplicaciones.Add(new AplicacionPago { PagoId = pago.Id, CuentaId = cuenta.Id, Monto = monto });
                aplicados.Add(new Aplicado(cuenta.Id, cuenta.Ncf, monto, saldo - monto));
            }
            db.Pagos.Add(pago);
            outbox.Agregar(new PagoSuplidorRegistradoV1(pago.Id, c.SuplidorId, c.Monto));
            await auditoria.RegistrarAsync("compras.pago-suplidor", "Suplidor", c.SuplidorId, new { Pago = pago.Id, c.Monto, c.Medio, c.Referencia }, ct);
            return new Response(pago.Id, aplicados);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/compras/pagos", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp())
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
    }
}

/// <summary>Supplier statement: invoices and payments in date order with the running balance.</summary>
internal static class EstadoCuentaSuplidor
{
    public sealed record Movimiento(DateOnly Fecha, string Tipo, string Documento, decimal Cargo, decimal Abono, decimal Saldo, DateOnly? Vence);
    public sealed record Response(Guid SuplidorId, string Suplidor, decimal Saldo, IReadOnlyList<Movimiento> Movimientos);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/compras/suplidores/{id:guid}/estado-cuenta", async (Guid id, ComprasDbContext db, CancellationToken ct) =>
                {
                    var s = await db.Suplidores.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                    if (s is null) return EndpointExtensions.Problema(ComprasErrors.SuplidorNoEncontrado);
                    var cargos = (await db.CuentasPorPagar.Where(x => x.SuplidorId == id && !x.Anulada).AsNoTracking().ToListAsync(ct))
                        .Select(x => (x.Fecha, Orden: 0, Tipo: x.Tipo.ToString(), Doc: x.Ncf, Cargo: x.Total, Abono: 0m, Vence: (DateOnly?)x.Vence));
                    var abonos = (await db.Pagos.Where(p => p.SuplidorId == id).AsNoTracking().ToListAsync(ct))
                        .Select(p => (p.Fecha, Orden: 1, Tipo: "Pago", Doc: p.Referencia ?? p.Medio.ToString(), Cargo: 0m, Abono: p.Monto, Vence: (DateOnly?)null));
                    var saldo = 0m;
                    var movimientos = cargos.Concat(abonos).OrderBy(m => m.Fecha).ThenBy(m => m.Orden)
                        .Select(m => new Movimiento(m.Fecha, m.Tipo, m.Doc, m.Cargo, m.Abono, saldo += m.Cargo - m.Abono, m.Vence)).ToList();
                    return Results.Ok(new Response(s.Id, s.Nombre, saldo, movimientos));
                })
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
    }
}

/// <summary>Accounts payable by supplier, aged by due date: current, 1–30, 31–60, 61–90 and over 90 days overdue.</summary>
internal static class CuentasPorPagarAntiguedad
{
    public sealed record Item(Guid SuplidorId, string Suplidor, decimal PorVencer, decimal Vencido1a30, decimal Vencido31a60, decimal Vencido61a90, decimal VencidoMas90, decimal Total);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/compras/por-pagar", async (ComprasDbContext db, TimeProvider reloj, CancellationToken ct) =>
                {
                    var hoy = DiaNegocio.De(reloj.GetUtcNow().UtcDateTime);
                    var suplidores = await db.Suplidores.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Nombre, ct);
                    var ids = await db.CuentasPorPagar.Where(c => !c.Anulada).Select(c => c.SuplidorId).Distinct().ToListAsync(ct);
                    var items = new List<Item>();
                    foreach (var id in ids)
                    {
                        var abiertas = await db.AbiertasAsync(id, ct);
                        if (abiertas.Count == 0) continue;
                        decimal Rango(int desde, int hasta) => abiertas.Where(a => hoy.DayNumber - a.Cuenta.Vence.DayNumber is var d && d >= desde && d <= hasta).Sum(a => a.Saldo);
                        items.Add(new Item(id, suplidores.GetValueOrDefault(id, "?"), Rango(int.MinValue, 0), Rango(1, 30), Rango(31, 60), Rango(61, 90),
                            Rango(91, int.MaxValue), abiertas.Sum(a => a.Saldo)));
                    }
                    return Results.Ok(items.OrderByDescending(i => i.Total));
                })
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
    }
}
