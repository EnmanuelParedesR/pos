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
using Toki.Configuracion.Contracts;
using Toki.Fiscal.Contracts;
using Toki.SharedKernel;
using Toki.Ventas.Domain;

namespace Toki.Ventas.Features;

/// <summary>Reprint: the same print model as the original sale (also for a voided one, marked as voided).</summary>
internal static class ObtenerComprobante
{
    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/ventas/{id:guid}/comprobante", async (Guid id, VentasDbContext db, IConfiguracionConsultas config, CancellationToken ct) =>
                    await db.VentaCompletaAsync(id, ct) is { } v
                        ? Results.Ok(ComprobanteImpresion.De(v, await config.ObtenerNegocioAsync(ct)))
                        : EndpointExtensions.Problema(VentasErrors.VentaNoEncontrada))
                .RequirePermiso(Permisos.VentasRegistrar).WithTags("Ventas");
    }
}

internal static class ObtenerVenta
{
    public sealed record LineaDto(Guid Id, string Descripcion, decimal Cantidad, decimal PrecioUnitario, decimal Descuento, decimal Total,
        decimal Itbis, decimal CantidadDevuelta, string? Nota);
    public sealed record PagoDto(MedioPago Medio, decimal Monto, decimal? Recibido, string? Referencia);
    public sealed record DevolucionDto(Guid Id, DateTime FechaUtc, decimal Total, MedioPago Medio, string ENcf, string Motivo);
    public sealed record Response(Guid Id, long Numero, string ENcf, TipoEcf TipoComprobante, DateTime FechaUtc, string Estado, string Caja, string Cajero,
        Guid? ClienteId, string? Cliente, decimal Descuento, decimal Itbis, decimal Total, decimal Cambio, string? MotivoAnulacion, string? ENcfAnulacion,
        IReadOnlyList<LineaDto> Lineas, IReadOnlyList<PagoDto> Pagos, IReadOnlyList<DevolucionDto> Devoluciones);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/ventas/{id:guid}", async (Guid id, VentasDbContext db, CancellationToken ct) =>
                {
                    var v = await db.VentaCompletaAsync(id, ct);
                    if (v is null) return EndpointExtensions.Problema(VentasErrors.VentaNoEncontrada);
                    var devoluciones = await db.Devoluciones.Where(d => d.VentaId == id).OrderBy(d => d.FechaUtc)
                        .Select(d => new DevolucionDto(d.Id, d.FechaUtc, d.Total, d.Medio, d.ENcf, d.Motivo)).AsNoTracking().ToListAsync(ct);
                    return Results.Ok(new Response(v.Id, v.Numero, v.ENcf, v.TipoComprobante, v.FechaUtc, v.Estado.ToString(), v.Caja, v.Cajero, v.ClienteId,
                        v.ClienteNombre, v.Descuento, v.Itbis, v.Total, v.Cambio, v.MotivoAnulacion, v.ENcfAnulacion,
                        v.Lineas.Select(l => new LineaDto(l.Id, l.Descripcion, l.Cantidad, l.PrecioUnitario, l.Descuento, l.Total, l.Itbis, l.CantidadDevuelta, l.Nota)).ToList(),
                        v.Pagos.Select(p => new PagoDto(p.Medio, p.Monto, p.Recibido, p.Referencia)).ToList(), devoluciones));
                })
                .RequirePermiso(Permisos.VentasRegistrar).WithTags("Ventas");
    }
}

/// <summary>Sales of a shift (the register's list for reprints and voids), or of a date range for whoever may see reports.
/// A cashier without reports only sees their own sales.</summary>
internal static class ListarVentas
{
    public const int TamanoPagina = 100;

    public sealed record Item(Guid Id, long Numero, string ENcf, DateTime FechaUtc, string Estado, string Cajero, string? Cliente, decimal Total);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/ventas", async (Guid? turnoId, DateOnly? desde, DateOnly? hasta, string? buscar, int? pagina, IUserContext usuario,
                    VentasDbContext db, CancellationToken ct) =>
                {
                    var q = db.Ventas.AsNoTracking();
                    if (!usuario.Tiene(Permisos.ReportesVer)) q = q.Where(v => v.CajeroId == usuario.UsuarioId);
                    if (turnoId is { } t) q = q.Where(v => v.TurnoId == t);
                    if (desde is not null || hasta is not null)
                    {
                        var (d, h) = DiaNegocio.RangoUtc(desde ?? hasta!.Value, hasta ?? desde!.Value);
                        q = q.Where(v => v.FechaUtc >= d && v.FechaUtc < h);
                    }
                    if (!string.IsNullOrWhiteSpace(buscar)) q = q.Where(v => v.ENcf == buscar.Trim() || v.Numero.ToString() == buscar.Trim());
                    var items = await q.OrderByDescending(v => v.Numero).Skip((Math.Max(pagina ?? 1, 1) - 1) * TamanoPagina).Take(TamanoPagina)
                        .Select(v => new Item(v.Id, v.Numero, v.ENcf, v.FechaUtc, v.Estado == EstadoVenta.Anulada ? "Anulada" : "Completada", v.Cajero, v.ClienteNombre, v.Total))
                        .ToListAsync(ct);
                    return Results.Ok(items);
                })
                .RequirePermiso(Permisos.VentasRegistrar).WithTags("Ventas");
    }
}

/// <summary>Parks a cart to serve the next customer. The register sends its draft as JSON and gets it back later.</summary>
internal static class VentasEnEspera
{
    public sealed record Guardar(Guid Id, Guid TurnoId, string Etiqueta, JsonElement Contenido, decimal Total) : ICommand<Ok>;
    public sealed record Quitar(Guid Id, Guid TurnoId) : ICommand<Ok>;
    public sealed record Item(Guid Id, string Etiqueta, decimal Total, DateTime CreadaUtc, JsonElement Contenido);

    internal sealed class Validator : AbstractValidator<Guardar>
    {
        public Validator()
        {
            RuleFor(c => c.Id).NotEmpty();
            RuleFor(c => c.Etiqueta).NotEmpty().MaximumLength(60);
            RuleFor(c => c.Contenido.GetRawText()).MaximumLength(ModuleDbContext.TextoLargo).WithMessage("La venta en espera es demasiado grande.");
        }
    }

    internal sealed class GuardarHandler(VentasDbContext db, ICajaOperaciones caja, IUserContext usuario, TimeProvider reloj) : ICommandHandler<Guardar, Ok>
    {
        public async Task<Result<Ok>> Handle(Guardar c, CancellationToken ct)
        {
            var turno = await caja.ObtenerTurnoAbiertoAsync(c.TurnoId, ct);
            if (turno.IsFailure) return turno.Error!;
            // Only this shift's cart: an id from another register never overwrites theirs (it fails as a duplicate instead).
            var espera = await db.EnEspera.FirstOrDefaultAsync(e => e.Id == c.Id && e.TurnoId == c.TurnoId, ct);
            if (espera is null) db.EnEspera.Add(espera = new VentaEnEspera { Id = c.Id, CreadaUtc = reloj.GetUtcNow().UtcDateTime });
            espera.TurnoId = c.TurnoId;
            espera.UsuarioId = usuario.UsuarioId;
            espera.Etiqueta = c.Etiqueta.Trim();
            espera.Contenido = c.Contenido.GetRawText();
            espera.Total = c.Total;
            return Ok.Valor;
        }
    }

    /// <summary>A closed shift's parked carts are no longer usable: they go when Caja publishes the closing.</summary>
    internal sealed class PurgarAlCerrarTurno(VentasDbContext db) : IManejadorEvento<TurnoCerradoV1>
    {
        public Task ManejarAsync(TurnoCerradoV1 evento, CancellationToken ct) => db.EnEspera.Where(e => e.TurnoId == evento.TurnoId).ExecuteDeleteAsync(ct);
    }

    internal sealed class QuitarHandler(VentasDbContext db, ICajaOperaciones caja) : ICommandHandler<Quitar, Ok>
    {
        public async Task<Result<Ok>> Handle(Quitar c, CancellationToken ct)
        {
            var turno = await caja.ObtenerTurnoAbiertoAsync(c.TurnoId, ct);
            if (turno.IsFailure) return turno.Error!;
            await db.EnEspera.Where(e => e.Id == c.Id && e.TurnoId == c.TurnoId).ExecuteDeleteAsync(ct);
            return Ok.Valor;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPut("/api/ventas/en-espera", async (Guardar c, ICommandHandler<Guardar, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.VentasRegistrar).WithTags("Ventas");
            app.MapDelete("/api/ventas/en-espera/{id:guid}", async (Guid id, Guid turnoId, ICommandHandler<Quitar, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Quitar(id, turnoId), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.VentasRegistrar).WithTags("Ventas");
            app.MapGet("/api/ventas/en-espera", async (Guid turnoId, VentasDbContext db, CancellationToken ct) =>
                {
                    var items = await db.EnEspera.Where(e => e.TurnoId == turnoId).OrderBy(e => e.CreadaUtc).AsNoTracking().ToListAsync(ct);
                    return Results.Ok(items.Select(e => new Item(e.Id, e.Etiqueta, e.Total, e.CreadaUtc, JsonDocument.Parse(e.Contenido).RootElement)));
                })
                .RequirePermiso(Permisos.VentasRegistrar).WithTags("Ventas");
        }
    }
}
