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
using Toki.Fiscal.Contracts;
using Toki.Inventario.Contracts;
using Toki.SharedKernel;
using Toki.Ventas.Contracts;
using Toki.Ventas.Domain;

namespace Toki.Ventas.Features;

/// <summary>Voids a whole sale while its shift is still open: money out of the shift, stock back, and a credit note (an issued e-CF is never deleted).
/// Needs ventas.anular or a supervisor. After the shift closes, use a return.</summary>
internal static class AnularVenta
{
    public sealed record Command(Guid VentaId, string Motivo, Guid? AutorizacionSupervisorId) : ICommand<Response>, IRequiereAutorizacion
    {
        public string Permiso => Permisos.VentasAnular;
        public Guid RecursoId => VentaId;
    }

    public sealed record Response(Guid VentaId, string ENcfNotaCredito, decimal Total);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Motivo).NotEmpty().MaximumLength(200);
    }

    internal sealed class Handler(VentasDbContext db, ICajaOperaciones caja, IMovimientosInventario inventario, IComprobantesFiscales fiscal,
        ICuentasClientes clientes, IUserContext usuario, IOutbox outbox, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // Lock order: sale row → shift → stock → fiscal sequence → customer → audit.
            var venta = await db.BloquearVentaAsync(c.VentaId, ct);
            if (venta is null) return VentasErrors.VentaNoEncontrada;
            if (venta.Estado == EstadoVenta.Anulada) return VentasErrors.YaAnulada;
            if (venta.Lineas.Any(l => l.CantidadDevuelta > 0)) return VentasErrors.ConDevoluciones;

            var turno = await caja.ObtenerTurnoAbiertoAsync(venta.TurnoId, ct);
            if (turno.IsFailure) return turno.Error!.Code == "Caja.TurnoCerrado" ? VentasErrors.FueraDeTurno : turno.Error;

            var entrada = await inventario.RegistrarEntradaAsync(TipoMovimiento.AnulacionVenta, venta.Id,
                venta.Lineas.Select(l => new LineaMovimiento(l.ProductoId, Math.Round(l.Cantidad * l.Factor, 3), l.CostoUnitario / l.Factor)).ToList(),
                actualizarCosto: false, ct);
            if (entrada.IsFailure) return entrada.Error!;
            var cobros = await caja.AnularCobrosAsync(venta.Id, ct);
            if (cobros.IsFailure) return cobros.Error!;

            var nota = await fiscal.EmitirAsync(new DocumentoAEmitir(TipoEcf.NotaCredito34, venta.Id, venta.RncCliente, venta.ClienteNombre,
                venta.Lineas.Select(l => new LineaFiscal(l.Descripcion, l.Cantidad, l.PrecioUnitario, l.Descuento, l.TasaItbis, l.Itbis, l.Total)).ToList(),
                venta.Itbis, venta.Total, venta.ENcf, CodigoModificacion.Anulacion), ct);
            if (nota.IsFailure) return nota.Error!;
            if (venta.ClienteId is not null) await clientes.AnularVentaAsync(venta.Id, ct);

            venta.Estado = EstadoVenta.Anulada;
            venta.AnuladaUtc = reloj.GetUtcNow().UtcDateTime;
            venta.MotivoAnulacion = c.Motivo.Trim();
            venta.AnuladaPorId = usuario.UsuarioId;
            venta.ENcfAnulacion = nota.Value.ENcf;
            outbox.Agregar(new VentaAnuladaV1(venta.Id, nota.Value.ENcf, venta.MotivoAnulacion, usuario.SupervisorId));
            await auditoria.RegistrarAsync("ventas.anular", "Venta", venta.Id, new { venta.Numero, venta.ENcf, venta.Total, c.Motivo, NotaCredito = nota.Value.ENcf }, ct);
            return new Response(venta.Id, nota.Value.ENcf, venta.Total);
        }
    }

    internal sealed record Body(string Motivo, Guid? AutorizacionSupervisorId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/ventas/{id:guid}/anular", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Motivo, b.AutorizacionSupervisorId), ct)).ToHttp())
                .RequirePermiso(Permisos.VentasRegistrar).WithTags("Ventas");
    }
}

/// <summary>Partial return against the original receipt: by line and quantity, stock back (unless it cannot be resold),
/// money back from the current shift, and a credit note. Needs ventas.devolver or a supervisor.</summary>
internal static class DevolverVenta
{
    /// <param name="Reponer">False when the returned goods go to waste (meat that cannot be sold again).</param>
    public sealed record Linea(Guid LineaVentaId, decimal Cantidad, bool Reponer = true);

    /// <param name="DevolucionId">Generated by the register; repeating the request returns the same return.</param>
    /// <param name="TurnoId">The shift that pays the money back (today's, not necessarily the sale's).</param>
    public sealed record Command(Guid VentaId, Guid DevolucionId, Guid TurnoId, IReadOnlyList<Linea> Lineas, MedioPago Medio, string Motivo,
        Guid? AutorizacionSupervisorId) : ICommand<Response>, IRequiereAutorizacion
    {
        public string Permiso => Permisos.VentasDevolver;
        public Guid RecursoId => VentaId;
    }

    public sealed record Response(Guid DevolucionId, string ENcfNotaCredito, decimal Total, decimal Itbis, MedioPago Medio);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.DevolucionId).NotEmpty();
            RuleFor(c => c.Lineas).NotEmpty();
            RuleForEach(c => c.Lineas).ChildRules(l => l.RuleFor(x => x.Cantidad).GreaterThan(0).PrecisionScale(19, 3, true));
            RuleFor(c => c.Lineas).Must(l => l.Select(x => x.LineaVentaId).Distinct().Count() == l.Count).WithMessage("Hay líneas repetidas.");
            RuleFor(c => c.Medio).Must(m => m is MedioPago.Efectivo or MedioPago.Tarjeta or MedioPago.Transferencia or MedioPago.SaldoFavor)
                .WithMessage("La devolución se paga en efectivo, a la tarjeta, por transferencia o a la cuenta del cliente.");
            RuleFor(c => c.Motivo).NotEmpty().MaximumLength(200);
        }
    }

    internal sealed class Handler(VentasDbContext db, ICajaOperaciones caja, IMovimientosInventario inventario, IComprobantesFiscales fiscal,
        ICuentasClientes clientes, IUserContext usuario, IOutbox outbox, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await YaRegistradaAsync(c.DevolucionId, ct) is { } ya) return ya;

            // Lock order: sale row → shift → stock → fiscal sequence → customer → audit. The sale lock makes returns and voids of one sale run one at a time.
            var venta = await db.BloquearVentaAsync(c.VentaId, ct);
            if (venta is null) return VentasErrors.VentaNoEncontrada;
            if (await YaRegistradaAsync(c.DevolucionId, ct) is { } repetida) return repetida;
            if (venta.Estado == EstadoVenta.Anulada) return VentasErrors.YaAnulada;

            var turno = await caja.ObtenerTurnoAbiertoAsync(c.TurnoId, ct);
            if (turno.IsFailure) return turno.Error!;

            var ids = c.Lineas.Select(l => l.LineaVentaId).ToList();
            var yaDevuelto = await db.Set<LineaDevolucion>().Where(l => ids.Contains(l.LineaVentaId))
                .GroupBy(l => l.LineaVentaId).Select(g => new { g.Key, Total = g.Sum(x => x.Total), Itbis = g.Sum(x => x.Itbis) })
                .ToDictionaryAsync(x => x.Key, x => (x.Total, x.Itbis), ct);

            var devolucion = new Devolucion
            {
                Id = c.DevolucionId, VentaId = venta.Id, TurnoId = c.TurnoId, Medio = c.Medio, Motivo = c.Motivo.Trim(),
                FechaUtc = reloj.GetUtcNow().UtcDateTime, UsuarioId = usuario.UsuarioId, SupervisorId = usuario.SupervisorId,
            };
            var lineasFiscales = new List<LineaFiscal>();
            var reponer = new List<LineaMovimiento>();
            foreach (var l in c.Lineas.OrderBy(l => l.LineaVentaId))
            {
                var lv = venta.Lineas.FirstOrDefault(x => x.Id == l.LineaVentaId);
                if (lv is null) return VentasErrors.VentaNoEncontrada;
                var pendiente = lv.Cantidad - lv.CantidadDevuelta;
                if (l.Cantidad > pendiente) return VentasErrors.CantidadDevolucion;
                if (lv.Unidad == UnidadMedida.Unidad && lv.PresentacionId is null && l.Cantidad != Math.Truncate(l.Cantidad))
                    return Error.Validation("Ventas.CantidadInvalida", $"{lv.Descripcion} se devuelve por unidad.");

                // The last piece returned takes whatever is left, so the line never refunds more (or other ITBIS) than it charged.
                var (montoPrevio, itbisPrevio) = yaDevuelto.GetValueOrDefault(lv.Id);
                var ultima = l.Cantidad == pendiente;
                var monto = ultima ? lv.Total - montoPrevio : Dinero.Redondear(lv.Total * l.Cantidad / lv.Cantidad);
                var itbis = ultima ? lv.Itbis - itbisPrevio : Itbis.Incluido(monto, lv.TasaItbis);
                lv.CantidadDevuelta += l.Cantidad;
                devolucion.Lineas.Add(new LineaDevolucion { DevolucionId = devolucion.Id, LineaVentaId = lv.Id, Cantidad = l.Cantidad, Total = monto, Itbis = itbis, Repuesto = l.Reponer });
                // DGII checks Precio × Cantidad − Descuento = MontoItem; the returned amount is already net of the sale's discounts.
                var descuento = Math.Max(0, Dinero.Redondear(lv.PrecioUnitario * l.Cantidad) - monto);
                lineasFiscales.Add(new LineaFiscal(lv.Descripcion, l.Cantidad, lv.PrecioUnitario, descuento, lv.TasaItbis, itbis, monto));
                if (l.Reponer) reponer.Add(new LineaMovimiento(lv.ProductoId, Math.Round(l.Cantidad * lv.Factor, 3), lv.CostoUnitario / lv.Factor));
            }
            devolucion.Total = devolucion.Lineas.Sum(l => l.Total);
            devolucion.Itbis = devolucion.Lineas.Sum(l => l.Itbis);

            if (c.Medio == MedioPago.SaldoFavor && venta.ClienteId is null) return VentasErrors.MedioNoDisponible;
            if (c.Medio != MedioPago.SaldoFavor)
            {
                // Money goes back the way it came in: a card sale is never refunded in cash.
                var cobrado = venta.Pagos.Where(p => p.Medio == c.Medio).Sum(p => p.Monto);
                var devueltoAsi = await db.Devoluciones.Where(d => d.VentaId == venta.Id && d.Medio == c.Medio).Select(d => d.Total).ToListAsync(ct);
                if (devolucion.Total > cobrado - devueltoAsi.Sum()) return VentasErrors.MedioExcedido(c.Medio, Math.Max(0, cobrado - devueltoAsi.Sum()));
            }

            if (reponer.Count > 0)
            {
                var entrada = await inventario.RegistrarEntradaAsync(TipoMovimiento.Devolucion, devolucion.Id, reponer, actualizarCosto: false, ct);
                if (entrada.IsFailure) return entrada.Error!;
            }
            if (c.Medio != MedioPago.SaldoFavor)
            {
                // shortcut: a cash refund may carry centavos the drawer cannot pay; round refunds like sales if blind counts show it.
                var reembolso = await caja.RegistrarReembolsoAsync(c.TurnoId, devolucion.Id, c.Medio, devolucion.Total, ct);
                if (reembolso.IsFailure) return reembolso.Error!;
            }

            var nota = await fiscal.EmitirAsync(new DocumentoAEmitir(TipoEcf.NotaCredito34, devolucion.Id, venta.RncCliente, venta.ClienteNombre,
                lineasFiscales, devolucion.Itbis, devolucion.Total, venta.ENcf, CodigoModificacion.CorreccionMontos), ct);
            if (nota.IsFailure) return nota.Error!;
            devolucion.ENcf = nota.Value.ENcf;

            // To the customer account: pays down this sale's unpaid credit first, the rest is balance in favor.
            if (c.Medio == MedioPago.SaldoFavor) await clientes.AcreditarDevolucionAsync(venta.ClienteId!.Value, venta.Id, devolucion.Id, devolucion.Total, ct);

            db.Devoluciones.Add(devolucion);
            outbox.Agregar(new DevolucionRegistradaV1(devolucion.Id, venta.Id, devolucion.ENcf, c.Medio.ToString(), devolucion.Total, devolucion.Itbis));
            await auditoria.RegistrarAsync("ventas.devolver", "Venta", venta.Id,
                new { Devolucion = devolucion.Id, devolucion.Total, c.Medio, c.Motivo, NotaCredito = devolucion.ENcf }, ct);
            return new Response(devolucion.Id, devolucion.ENcf, devolucion.Total, devolucion.Itbis, c.Medio);
        }

        private async Task<Response?> YaRegistradaAsync(Guid devolucionId, CancellationToken ct) =>
            await db.Devoluciones.AsNoTracking().Where(d => d.Id == devolucionId)
                .Select(d => new Response(d.Id, d.ENcf, d.Total, d.Itbis, d.Medio)).FirstOrDefaultAsync(ct);
    }

    internal sealed record Body(Guid DevolucionId, Guid TurnoId, IReadOnlyList<Linea> Lineas, MedioPago Medio, string Motivo, Guid? AutorizacionSupervisorId);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/ventas/{id:guid}/devoluciones", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.DevolucionId, b.TurnoId, b.Lineas, b.Medio, b.Motivo, b.AutorizacionSupervisorId), ct)).ToHttp())
                .RequirePermiso(Permisos.VentasRegistrar).WithTags("Ventas");
    }
}
