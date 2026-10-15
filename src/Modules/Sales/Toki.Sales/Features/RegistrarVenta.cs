using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.CashRegister.Contracts;
using Toki.Customers.Contracts;
using Toki.Settings.Contracts;
using Toki.Fiscal.Contracts;
using Toki.Inventory.Contracts;
using Toki.SharedKernel;
using Toki.Sales.Contracts;
using Toki.Sales.Domain;

namespace Toki.Sales.Features;

/// <summary>A sale in one transaction: totals and ITBIS, stock out, payments into the shift, e-CF, outbox and audit.
/// <see cref="Command.VentaId"/> comes from the register; repeating the request returns the same sale.</summary>
internal static class RegistrarVenta
{
    /// <param name="PrecioUnitario">Only when the cashier changes the price (needs ventas.cambiar-precio).</param>
    /// <param name="CodigoEtiqueta">Scale label as scanned. The server reads the weight or the amount from it; <see cref="Cantidad"/> is then ignored.</param>
    public sealed record Linea(Guid ProductoId, Guid? PresentacionId, decimal Cantidad, decimal? PrecioUnitario = null, Descuento? Descuento = null,
        string? Nota = null, string? CodigoEtiqueta = null);

    public sealed record Pago(MedioPago Medio, decimal Monto, string? Referencia = null);

    /// <param name="TotalEsperado">The total the cashier saw. If the server computes another one, nothing is saved and the new total is returned.</param>
    /// <param name="Autorizaciones">One-time supervisor authorizations (for a big discount and/or a price change), bound to <see cref="VentaId"/>.</param>
    public sealed record Command(
        Guid VentaId, Guid TurnoId, IReadOnlyList<Linea> Lineas, IReadOnlyList<Pago> Pagos, decimal TotalEsperado,
        TipoEcf TipoComprobante = TipoEcf.Consumo32, Guid? ClienteId = null, string? RncCliente = null, string? NombreCliente = null,
        NivelPrecio Nivel = NivelPrecio.Detalle, Descuento? DescuentoGlobal = null, IReadOnlyList<Guid>? Autorizaciones = null) : ICommand<Response>;

    public sealed record Response(Guid VentaId, long Numero, string ENcf, decimal Total, decimal Cambio, ComprobanteImpresion Comprobante);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.VentaId).NotEmpty();
            RuleFor(c => c.Lineas).NotEmpty().Must(l => l.Count <= 300).WithMessage("Una venta tiene como máximo 300 líneas.");
            RuleForEach(c => c.Lineas).ChildRules(l =>
            {
                l.RuleFor(x => x.Cantidad).GreaterThan(0).LessThanOrEqualTo(100_000).PrecisionScale(19, 3, true);
                l.RuleFor(x => x.PrecioUnitario).GreaterThan(0).PrecisionScale(19, 2, true).When(x => x.PrecioUnitario is not null);
                l.RuleFor(x => x.Nota).MaximumLength(100);
                l.RuleFor(x => x.CodigoEtiqueta).MaximumLength(20);
            });
            RuleFor(c => c.Pagos).NotEmpty();
            RuleForEach(c => c.Pagos).ChildRules(p => { p.RuleFor(x => x.Medio).IsInEnum(); p.RuleFor(x => x.Referencia).MaximumLength(60); });
            RuleFor(c => c.TipoComprobante).Must(t => t is TipoEcf.Consumo32 or TipoEcf.CreditoFiscal31).WithMessage("Tipo de comprobante no válido para una venta.");
            RuleFor(c => c.RncCliente).Must(r => Rnc.EsValido(Rnc.Normalizar(r))).When(c => c.TipoComprobante == TipoEcf.CreditoFiscal31 && c.ClienteId is null)
                .WithMessage("El crédito fiscal necesita el RNC o la cédula del cliente.");
            RuleFor(c => c.NombreCliente).NotEmpty().When(c => c.TipoComprobante == TipoEcf.CreditoFiscal31 && c.ClienteId is null);
            RuleFor(c => c.NombreCliente).MaximumLength(150);
            RuleFor(c => c.ClienteId).NotNull().When(c => c.Pagos.Any(p => p.Medio is MedioPago.Fiado or MedioPago.SaldoFavor))
                .WithMessage("El fiado y el saldo a favor necesitan un cliente.");
            RuleFor(c => c.Nivel).IsInEnum();
        }
    }

    internal sealed class Handler(
        VentasDbContext db, ICajaOperaciones caja, ICatalogoConsultas catalogo, IMovimientosInventario inventario, IComprobantesFiscales fiscal,
        IConfiguracionConsultas configuracion, ICuentasClientes clientes, IAutorizacionesSupervisor autorizaciones, IUserContext usuario, IOutbox outbox,
        IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var negocio = await configuracion.ObtenerNegocioAsync(ct);
            // A retry of a saved sale gets the same answer, even after the shift closed.
            if (await db.VentaCompletaAsync(c.VentaId, ct) is { } yaGuardada) return Repetida(yaGuardada, c, negocio);

            // Lock the shift: a concurrent retry of this same sale waits here and then finds it saved.
            var turno = await caja.ObtenerTurnoAbiertoAsync(c.TurnoId, ct);
            if (turno.IsFailure) return turno.Error!;
            if (await db.VentaCompletaAsync(c.VentaId, ct) is { } existente) return Repetida(existente, c, negocio);

            var etiquetas = c.Lineas.Where(l => l.CodigoEtiqueta is not null).Select(l => l.CodigoEtiqueta).ToList();
            if (etiquetas.Count != etiquetas.Distinct().Count()) return VentasErrors.EtiquetaRepetida;

            ClienteParaVenta? cliente = null;
            if (c.ClienteId is { } clienteId)
            {
                cliente = await clientes.ObtenerAsync(clienteId, ct);
                if (cliente is not { Activo: true }) return Error.NotFound("Clientes.ClienteNoEncontrado", "El cliente no existe o está desactivado.");
            }
            var rnc = Rnc.Normalizar(c.RncCliente) ?? cliente?.RncCedula;
            var nombreCliente = c.NombreCliente?.Trim() ?? cliente?.Nombre;

            var productos = (await catalogo.ObtenerParaVentaAsync(c.Lineas.Select(l => (l.ProductoId, l.PresentacionId)).ToList(), ct))
                .ToDictionary(p => (p.ProductoId, p.PresentacionId));
            var permisos = new HashSet<string>();
            var entradas = new List<(Linea Linea, ProductoParaVenta Producto, decimal Precio, decimal Cantidad, decimal? MontoFijo)>();
            foreach (var l in c.Lineas)
            {
                if (!productos.TryGetValue((l.ProductoId, l.PresentacionId), out var p) || !p.Activo) return VentasErrors.ProductoNoDisponible;
                var lista = c.Nivel == NivelPrecio.Mayorista ? p.PrecioMayorista ?? p.Precio : p.Precio;
                var precio = l.PrecioUnitario ?? lista;
                var cantidad = l.Cantidad;
                decimal? montoFijo = null;
                if (l.CodigoEtiqueta is { } codigo)
                {
                    var etiqueta = await catalogo.LeerEtiquetaAsync(codigo, ct);
                    if (etiqueta.IsFailure) return etiqueta.Error!;
                    if (etiqueta.Value.ProductoId != l.ProductoId || l.PresentacionId is not null) return VentasErrors.EtiquetaNoCoincide;
                    if (etiqueta.Value.Peso is { } peso) cantidad = peso;
                    // A price label fixes the amount; the quantity follows from the price (for stock).
                    if (etiqueta.Value.Monto is { } monto) (montoFijo, cantidad) = (monto, Math.Round(monto / precio, 3, MidpointRounding.AwayFromZero));
                }
                if (p.Unidad == UnidadMedida.Unidad && p.PresentacionId is null && cantidad != Math.Truncate(cantidad))
                    return Error.Validation("Ventas.CantidadInvalida", $"{p.Nombre} se vende por unidad.");

                var mayoristaSinCliente = c.Nivel == NivelPrecio.Mayorista && cliente?.Nivel != NivelPrecioCliente.Mayorista;
                if (precio != lista || mayoristaSinCliente) permisos.Add(Permisos.VentasCambiarPrecio);
                entradas.Add((l, p, precio, cantidad, montoFijo));
            }

            var totales = Calculadora.Calcular(
                entradas.Select(e => new LineaEntrada(e.Precio, e.Cantidad, e.Producto.TasaItbis, e.Linea.Descuento, e.MontoFijo)).ToList(),
                c.DescuentoGlobal);
            if (totales.IsFailure) return totales.Error!;
            var t = totales.Value;
            if (t.Total != c.TotalEsperado) return VentasErrors.TotalCambio(t.Total);
            if ((c.TipoComprobante == TipoEcf.CreditoFiscal31 || t.Total >= LimitesEcf.ConsumoIdentificado) && !Rnc.EsValido(rnc)) return VentasErrors.RncRequerido;

            // Per line, after every discount: a big discount on one item, or a line sold under its cost, needs a supervisor.
            for (var i = 0; i < entradas.Count; i++)
            {
                var linea = t.Lineas[i];
                var costo = Dinero.Redondear(entradas[i].Producto.CostoBase * entradas[i].Producto.Factor * entradas[i].Cantidad);
                if (linea.Descuento * 100 > linea.Bruto * negocio.TopeDescuentoPct) permisos.Add(Permisos.VentasDescuentoMayor);
                if (linea.Total - linea.Itbis < costo) permisos.Add(Permisos.VentasCambiarPrecio);
            }

            var pagos = Pagos.Aplicar(t.Total, c.Pagos.Select(p => new PagoEntrada(p.Medio, p.Monto, p.Referencia?.Trim())).ToList());
            if (pagos.IsFailure) return pagos.Error!;
            var fiado = pagos.Value.Pagos.Where(p => p.Medio == MedioPago.Fiado).Sum(p => p.Monto);
            var saldoFavor = pagos.Value.Pagos.Where(p => p.Medio == MedioPago.SaldoFavor).Sum(p => p.Monto);
            if (fiado > 0 && cliente!.Deuda + fiado > cliente.LimiteCredito) permisos.Add(Permisos.FiadoExcederLimite);

            // shortcut: one ventas.cambiar-precio authorization covers every line of the sale; bind it to the expected total if supervisors ask for it.
            var autorizado = await AutorizarAsync(permisos, c, ct);
            if (autorizado.IsFailure) return autorizado.Error!;

            // Lock order, the same in every handler: shift → stock (by ProductoId) → fiscal sequence → customer → audit.
            var salida = await inventario.RegistrarSalidaAsync(TipoMovimiento.Venta, c.VentaId,
                entradas.Select(e => new LineaMovimiento(e.Producto.ProductoId, Math.Round(e.Cantidad * e.Producto.Factor, 3))).ToList(), ct);
            if (salida.IsFailure) return salida.Error!;

            await caja.RegistrarCobroAsync(c.TurnoId, c.VentaId, pagos.Value.Pagos.Select(p => new CobroMedio(p.Medio, p.Monto, p.Referencia)).ToList(),
                pagos.Value.Redondeo, ct);

            var lineasFiscales = entradas.Select((e, i) => new LineaFiscal(e.Producto.Nombre, e.Cantidad, e.Precio, t.Lineas[i].Descuento,
                e.Producto.TasaItbis, t.Lineas[i].Itbis, t.Lineas[i].Total)).ToList();
            var comprobante = await fiscal.EmitirAsync(new DocumentoAEmitir(c.TipoComprobante, c.VentaId, rnc, nombreCliente, lineasFiscales, t.Itbis, t.Total,
                TipoPago: fiado > 0 ? TipoPagoEcf.Credito : TipoPagoEcf.Contado), ct);
            if (comprobante.IsFailure) return comprobante.Error!;

            // The customer's limit is re-checked under the customer's lock.
            if (saldoFavor > 0 && await clientes.UsarSaldoFavorAsync(cliente!.Id, c.VentaId, saldoFavor, ct) is { IsFailure: true } sinSaldo) return sinSaldo.Error!;
            if (fiado > 0 && await clientes.CargarFiadoAsync(cliente!.Id, c.VentaId, comprobante.Value.ENcf, fiado,
                    permitirExceso: permisos.Contains(Permisos.FiadoExcederLimite), ct) is { IsFailure: true } limite)
                return limite.Error!;

            var venta = new Venta
            {
                Id = c.VentaId, TurnoId = c.TurnoId, CajaId = turno.Value.CajaId, Caja = turno.Value.Caja, CajeroId = usuario.UsuarioId, Cajero = usuario.Nombre,
                ClienteId = c.ClienteId, ClienteNombre = nombreCliente, RncCliente = rnc, TipoComprobante = c.TipoComprobante,
                ENcf = comprobante.Value.ENcf, CodigoSeguridad = comprobante.Value.CodigoSeguridad, UrlQr = comprobante.Value.UrlQr, Nivel = c.Nivel,
                Bruto = t.Bruto, Descuento = t.Descuento, Subtotal = t.Subtotal, Itbis = t.Itbis, Total = t.Total, Cambio = pagos.Value.Cambio,
                Redondeo = pagos.Value.Redondeo, Estado = EstadoVenta.Completada, FechaUtc = reloj.GetUtcNow().UtcDateTime, SupervisorId = usuario.SupervisorId,
                Lineas = entradas.Select((e, i) => new LineaVenta
                {
                    Id = Guid.CreateVersion7(), VentaId = c.VentaId, Orden = i + 1, ProductoId = e.Producto.ProductoId, PresentacionId = e.Producto.PresentacionId,
                    CategoriaId = e.Producto.CategoriaId, Descripcion = e.Producto.Nombre, Unidad = e.Producto.Unidad, Cantidad = e.Cantidad, Factor = e.Producto.Factor,
                    PrecioLista = c.Nivel == NivelPrecio.Mayorista ? e.Producto.PrecioMayorista ?? e.Producto.Precio : e.Producto.Precio, PrecioUnitario = e.Precio,
                    Bruto = t.Lineas[i].Bruto, Descuento = t.Lineas[i].Descuento, TasaItbis = e.Producto.TasaItbis, Itbis = t.Lineas[i].Itbis,
                    Total = t.Lineas[i].Total, CostoUnitario = Math.Round(e.Producto.CostoBase * e.Producto.Factor, 4), Nota = e.Linea.Nota?.Trim(),
                    CodigoEtiqueta = e.Linea.CodigoEtiqueta,
                }).ToList(),
                Pagos = pagos.Value.Pagos.Select(p => new PagoVenta { Id = Guid.CreateVersion7(), VentaId = c.VentaId, Medio = p.Medio, Monto = p.Monto, Recibido = p.Recibido, Referencia = p.Referencia }).ToList(),
            };
            db.Ventas.Add(venta);
            await db.SaveChangesAsync(ct); // assigns the receipt number

            outbox.Agregar(new VentaRegistradaV1(venta.Id, venta.Numero, venta.TurnoId, venta.CajeroId, venta.ClienteId, venta.ENcf, venta.Total, venta.Itbis,
                venta.Descuento, venta.Lineas.Select(l => new LineaVentaV1(l.ProductoId, l.PresentacionId, l.Descripcion, l.Cantidad, l.Total, l.Itbis, l.CostoUnitario)).ToList(),
                venta.Pagos.Select(p => new PagoVentaV1(p.Medio.ToString(), p.Monto)).ToList()));
            await auditoria.RegistrarAsync("ventas.registrar", "Venta", venta.Id,
                new { venta.Numero, venta.ENcf, venta.Total, venta.Descuento, PorcentajeDescuento = t.PorcentajeDescuento, Permisos = permisos }, ct);
            return Respuesta(venta, negocio);
        }

        /// <summary>Each escalated permission the cashier lacks must be covered by one of the supervisor authorizations sent.</summary>
        private async Task<Result> AutorizarAsync(HashSet<string> permisos, Command c, CancellationToken ct)
        {
            var disponibles = c.Autorizaciones?.Distinct().ToList() ?? [];
            foreach (var permiso in permisos.Where(p => !usuario.Tiene(p)).Order())
            {
                Guid? supervisor = null;
                foreach (var id in disponibles)
                {
                    var r = await autorizaciones.ConsumirAsync(id, permiso, c.VentaId, ct);
                    if (r.IsFailure) continue;
                    supervisor = r.Value;
                    disponibles.Remove(id);
                    break;
                }
                if (supervisor is null) return AutorizacionErrors.Requerida(permiso);
                usuario.SupervisorId = supervisor;
            }
            return Result.Success();
        }

        /// <summary>A retry gets the saved sale; a different cart under the same id (a register bug) must not be "paid" with the old sale.</summary>
        private static Result<Response> Repetida(Venta v, Command c, DatosNegocio negocio) =>
            v.Total == c.TotalEsperado ? Respuesta(v, negocio) : VentasErrors.IdRepetido;

        private static Response Respuesta(Venta v, DatosNegocio negocio) =>
            new(v.Id, v.Numero, v.ENcf, v.Total, v.Cambio, ComprobanteImpresion.De(v, negocio));
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/ventas", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/ventas/{r.VentaId}", r)))
                .RequirePermiso(Permisos.VentasRegistrar).WithTags("Ventas");
    }
}
