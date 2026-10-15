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
using Toki.Inventario.Contracts;
using Toki.SharedKernel;

namespace Toki.Compras.Features;

/// <summary>Creates or replaces a purchase draft. Nothing moves until it is applied.</summary>
internal static class GuardarCompra
{
    /// <param name="CostoUnitario">Cost of one purchased unit (of the presentation when given), before ITBIS.</param>
    public sealed record Linea(Guid ProductoId, Guid? PresentacionId, decimal Cantidad, decimal CostoUnitario, TasaItbis? TasaItbis = null);

    /// <param name="Nueva">True for POST: <see cref="Id"/> comes from the client, and a retry returns the saved draft.</param>
    /// <param name="ItbisFactura">ITBIS and total as printed on the invoice (optional): they win over Toki's line math when within RD$ 1.</param>
    public sealed record Command(Guid Id, bool Nueva, Guid SuplidorId, string Ncf, DateOnly FechaFactura, Condicion Condicion, IReadOnlyList<Linea> Lineas,
        Guid? AlmacenId = null, int? DiasCredito = null, string? Notas = null, decimal? ItbisFactura = null, decimal? TotalFactura = null) : ICommand<Response>;

    public sealed record Response(Guid Id, decimal Subtotal, decimal Itbis, decimal Total);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator(TimeProvider reloj)
        {
            RuleFor(c => c.Id).NotEmpty();
            RuleFor(c => c.FechaFactura).Must(f => FechaDocumento.EsValida(f, reloj)).WithMessage(FechaDocumento.Mensaje);
            RuleFor(c => c.ItbisFactura).GreaterThanOrEqualTo(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.TotalFactura).GreaterThan(0).PrecisionScale(19, 2, true);
            RuleFor(c => c.Ncf).Must(Domain.Ncf.EsValido).WithMessage("El NCF no es válido (B01… de 11 caracteres o E31… de 13).");
            RuleFor(c => c.Condicion).IsInEnum();
            RuleFor(c => c.Lineas).NotEmpty().Must(l => l.Count <= 500);
            RuleForEach(c => c.Lineas).ChildRules(l =>
            {
                l.RuleFor(x => x.Cantidad).GreaterThan(0).PrecisionScale(19, 3, true);
                l.RuleFor(x => x.CostoUnitario).GreaterThanOrEqualTo(0).PrecisionScale(19, 4, true);
                l.RuleFor(x => x.TasaItbis).IsInEnum();
            });
            RuleFor(c => c.DiasCredito).InclusiveBetween(0, 365).When(c => c.DiasCredito is not null);
            RuleFor(c => c.Notas).MaximumLength(500);
        }
    }

    internal sealed class Handler(ComprasDbContext db, ICatalogoConsultas catalogo, IUserContext usuario, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            // Lock first: an edit and an apply of the same draft run one at a time (the apply claims the same row).
            var compra = await db.BloquearCompraAsync(c.Id, ct);
            if (c.Nueva && compra is not null) return new Response(compra.Id, compra.Subtotal, compra.Itbis, compra.Total);
            if (!c.Nueva && compra is null) return ComprasErrors.CompraNoEncontrada;
            if (compra is { Estado: not EstadoCompra.Borrador }) return ComprasErrors.NoEsBorrador;

            var suplidor = await db.Suplidores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == c.SuplidorId && s.Activo, ct);
            if (suplidor is null) return ComprasErrors.SuplidorNoEncontrado;
            if (suplidor.Rnc is null) return ComprasErrors.SuplidorSinRnc;
            var ncf = c.Ncf.Trim().ToUpperInvariant();
            if (await db.Compras.AnyAsync(x => x.SuplidorId == c.SuplidorId && x.Ncf == ncf && x.Estado != EstadoCompra.Anulada && x.Id != c.Id, ct)
                || await db.Gastos.AnyAsync(g => g.SuplidorId == c.SuplidorId && g.Ncf == ncf && !g.Anulado, ct))
                return ComprasErrors.NcfRepetido;

            if (compra is null)
            {
                compra = new Compra { Id = c.Id, Estado = EstadoCompra.Borrador, CreadaUtc = reloj.GetUtcNow().UtcDateTime, UsuarioId = usuario.UsuarioId };
                db.Compras.Add(compra);
            }

            var productos = (await catalogo.ObtenerParaVentaAsync(c.Lineas.Select(l => (l.ProductoId, l.PresentacionId)).ToList(), ct))
                .ToDictionary(p => (p.ProductoId, p.PresentacionId));
            var lineas = new List<LineaCompra>();
            foreach (var (l, i) in c.Lineas.Select((l, i) => (l, i)))
            {
                if (!productos.TryGetValue((l.ProductoId, l.PresentacionId), out var p)) return ComprasErrors.ProductoNoEncontrado;
                lineas.Add(new LineaCompra
                {
                    Id = Guid.CreateVersion7(), CompraId = compra.Id, Orden = i + 1, ProductoId = p.ProductoId, Descripcion = p.Nombre, Cantidad = l.Cantidad,
                    Factor = p.Factor, CostoUnitario = l.CostoUnitario, TasaItbis = l.TasaItbis ?? p.TasaItbis,
                });
            }

            db.RemoveRange(compra.Lineas);
            compra.Lineas = lineas;
            compra.SuplidorId = suplidor.Id;
            compra.SuplidorNombre = suplidor.Nombre;
            compra.RncSuplidor = suplidor.Rnc;
            compra.Ncf = ncf;
            compra.FechaFactura = c.FechaFactura;
            compra.Condicion = c.Condicion;
            compra.Vence = c.Condicion == Condicion.Credito ? c.FechaFactura.AddDays(c.DiasCredito ?? suplidor.DiasCredito) : null;
            compra.AlmacenId = c.AlmacenId;
            compra.Notas = c.Notas?.Trim();
            if (!compra.Recalcular(c.ItbisFactura, c.TotalFactura)) return ComprasErrors.TotalFacturaLejos;
            return new Response(compra.Id, compra.Subtotal, compra.Itbis, compra.Total);
        }
    }

    /// <param name="Id">Generated by the client (GUID v7) on POST: a retry returns the same draft.</param>
    internal sealed record Body(Guid SuplidorId, string Ncf, DateOnly FechaFactura, Condicion Condicion, IReadOnlyList<Linea> Lineas,
        Guid? AlmacenId = null, int? DiasCredito = null, string? Notas = null, decimal? ItbisFactura = null, decimal? TotalFactura = null, Guid? Id = null);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            static Command Comando(Guid id, bool nueva, Body b) =>
                new(id, nueva, b.SuplidorId, b.Ncf, b.FechaFactura, b.Condicion, b.Lineas, b.AlmacenId, b.DiasCredito, b.Notas, b.ItbisFactura, b.TotalFactura);
            app.MapPost("/api/compras", async (Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(Comando(b.Id ?? Guid.CreateVersion7(), true, b), ct)).ToHttp(r => Results.Created($"/api/compras/{r.Id}", r)))
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
            app.MapPut("/api/compras/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(Comando(id, false, b), ct)).ToHttp())
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
        }
    }
}

/// <summary>Receives the goods: stock in at the purchase cost (weighted average updated) and, on credit, a payable.</summary>
internal static class AplicarCompra
{
    public sealed record Command(Guid Id) : ICommand<Ok>;

    internal sealed class Handler(ComprasDbContext db, IMovimientosInventario inventario, IOutbox outbox, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            // Claim the draft: two applies of the same purchase cannot both put stock in.
            var ahora = reloj.GetUtcNow().UtcDateTime;
            if (await db.Compras.Where(x => x.Id == c.Id && x.Estado == EstadoCompra.Borrador)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Estado, EstadoCompra.Aplicada).SetProperty(x => x.AplicadaUtc, ahora), ct) == 0)
                return await db.Compras.AnyAsync(x => x.Id == c.Id, ct) ? ComprasErrors.NoEsBorrador : ComprasErrors.CompraNoEncontrada;
            var compra = await db.Compras.Include(x => x.Lineas).AsNoTracking().SingleAsync(x => x.Id == c.Id, ct);

            var entrada = await inventario.RegistrarEntradaAsync(TipoMovimiento.Compra, compra.Id,
                compra.Lineas.Select(l => new LineaMovimiento(l.ProductoId, Math.Round(l.Cantidad * l.Factor, 3), Math.Round(l.CostoUnitario / l.Factor, 4))).ToList(),
                actualizarCosto: true, ct, compra.AlmacenId);
            if (entrada.IsFailure) return entrada.Error!;

            if (compra.Condicion == Condicion.Credito)
                db.CuentasPorPagar.Add(new CuentaPorPagar
                {
                    Id = Guid.CreateVersion7(), SuplidorId = compra.SuplidorId, Tipo = TipoDocumento.Compra, DocumentoId = compra.Id, Ncf = compra.Ncf,
                    Fecha = compra.FechaFactura, Vence = compra.Vence ?? compra.FechaFactura, Total = compra.Total,
                });
            outbox.Agregar(new CompraRegistradaV1(compra.Id, compra.SuplidorId, compra.Ncf, compra.Subtotal, compra.Itbis, compra.Total, compra.Condicion == Condicion.Credito));
            await auditoria.RegistrarAsync("compras.aplicar", "Compra", compra.Id, new { compra.Numero, compra.Ncf, compra.Total, compra.Condicion }, ct);
            return Ok.Valor;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/compras/{id:guid}/aplicar", async (Guid id, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
    }
}

/// <summary>Voids a purchase. An applied one takes its stock back out (never below zero), takes its cost back out of the average,
/// and needs no payments on it. Lock order: purchase row → supplier → stock → audit.</summary>
internal static class AnularCompra
{
    public sealed record Command(Guid Id, string Motivo) : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator() => RuleFor(c => c.Motivo).NotEmpty().MaximumLength(200);
    }

    internal sealed class Handler(ComprasDbContext db, IMovimientosInventario inventario, IOutbox outbox, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var compra = await db.BloquearCompraAsync(c.Id, ct);
            if (compra is null) return ComprasErrors.CompraNoEncontrada;
            if (compra.Estado == EstadoCompra.Anulada) return ComprasErrors.YaAnulado;

            if (compra.Estado == EstadoCompra.Aplicada)
            {
                await db.BloquearSuplidorAsync(compra.SuplidorId, ct);
                var cuenta = await db.CuentasPorPagar.FirstOrDefaultAsync(x => x.DocumentoId == compra.Id, ct);
                if (cuenta is not null && await db.TienePagosAsync(cuenta.Id, ct)) return ComprasErrors.ConPagos;
                if (cuenta is not null) cuenta.Anulada = true;

                // The same cost per base unit the apply put in, so the average goes back to what it was.
                var salida = await inventario.RegistrarSalidaAsync(TipoMovimiento.AnulacionCompra, compra.Id,
                    compra.Lineas.Select(l => new LineaMovimiento(l.ProductoId, Math.Round(l.Cantidad * l.Factor, 3), Math.Round(l.CostoUnitario / l.Factor, 4))).ToList(),
                    ct, compra.AlmacenId, soloConExistencia: true, revertirCosto: true);
                if (salida.IsFailure) return salida.Error!;
                outbox.Agregar(new CompraAnuladaV1(compra.Id, c.Motivo.Trim()));
            }
            compra.Estado = EstadoCompra.Anulada;
            compra.AnuladaUtc = reloj.GetUtcNow().UtcDateTime;
            compra.MotivoAnulacion = c.Motivo.Trim();
            await auditoria.RegistrarAsync("compras.anular", "Compra", compra.Id, new { compra.Numero, compra.Ncf, compra.Total, c.Motivo }, ct);
            return Ok.Valor;
        }
    }

    internal sealed record Body(string Motivo);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/compras/{id:guid}/anular", async (Guid id, Body b, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Motivo), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
    }
}

internal static class ConsultarCompras
{
    public sealed record Item(Guid Id, long Numero, string Suplidor, string Ncf, DateOnly FechaFactura, string Estado, string Condicion, decimal Total);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/compras/{id:guid}", async (Guid id, ComprasDbContext db, CancellationToken ct) =>
                    await db.Compras.Include(c => c.Lineas.OrderBy(l => l.Orden)).AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) is { } compra
                        ? Results.Ok(compra)
                        : EndpointExtensions.Problema(ComprasErrors.CompraNoEncontrada))
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
            app.MapGet("/api/compras", async (DateOnly? desde, DateOnly? hasta, Guid? suplidorId, ComprasDbContext db, CancellationToken ct) =>
                {
                    var q = db.Compras.AsNoTracking();
                    if (desde is { } d) q = q.Where(c => c.FechaFactura >= d);
                    if (hasta is { } h) q = q.Where(c => c.FechaFactura <= h);
                    if (suplidorId is { } s) q = q.Where(c => c.SuplidorId == s);
                    return Results.Ok(await q.OrderByDescending(c => c.Numero).Take(500)
                        .Select(c => new Item(c.Id, c.Numero, c.SuplidorNombre, c.Ncf, c.FechaFactura, c.Estado.ToString(), c.Condicion.ToString(), c.Total)).ToListAsync(ct));
                })
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
        }
    }
}
