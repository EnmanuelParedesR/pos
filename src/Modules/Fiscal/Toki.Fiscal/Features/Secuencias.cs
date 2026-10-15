using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Fiscal.Contracts;
using Toki.Fiscal.Domain;
using Toki.SharedKernel;

namespace Toki.Fiscal.Features;

/// <summary>Loads an e-NCF range authorized by the DGII.</summary>
internal static class CargarSecuencia
{
    public sealed record Command(TipoEcf Tipo, long Desde, long Hasta, DateOnly VenceEl) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Tipo).IsInEnum();
            RuleFor(c => c.Desde).GreaterThan(0);
            RuleFor(c => c.Hasta).GreaterThanOrEqualTo(c => c.Desde).LessThanOrEqualTo(9_999_999_999);
        }
    }

    internal sealed class Handler(FiscalDbContext db, IAuditoria auditoria, TimeProvider reloj) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await db.Secuencias.AnyAsync(s => s.Tipo == c.Tipo && s.Desde <= c.Hasta && c.Desde <= s.Hasta, ct)) return FiscalErrors.RangoSolapado;
            var s = new Secuencia
            {
                Id = Guid.CreateVersion7(), Tipo = c.Tipo, Desde = c.Desde, Hasta = c.Hasta, Siguiente = c.Desde, VenceEl = c.VenceEl,
                CreadaUtc = reloj.GetUtcNow().UtcDateTime,
            };
            db.Secuencias.Add(s);
            await auditoria.RegistrarAsync("fiscal.cargar-secuencia", "Secuencia", s.Id, c, ct);
            return new Response(s.Id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/fiscal/secuencias", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(r => Results.Created($"/api/fiscal/secuencias/{r.Id}", r)))
                .RequirePermiso(Permisos.FiscalGestionar).WithTags("Fiscal");
    }
}

/// <summary>Ranges with usage and the two alerts: 80 % used, or 30 days to expire.</summary>
internal static class ListarSecuencias
{
    public sealed record Item(Guid Id, TipoEcf Tipo, string Desde, string Hasta, string Siguiente, long Disponibles, decimal PorcentajeUsado,
        DateOnly VenceEl, bool Activa, bool AlertaUso, bool AlertaVencimiento);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/secuencias", async (FiscalDbContext db, TimeProvider reloj, CancellationToken ct) =>
                {
                    var hoy = DiaNegocio.De(reloj.GetUtcNow().UtcDateTime);
                    var secuencias = await db.Secuencias.AsNoTracking().OrderBy(s => s.Tipo).ThenBy(s => s.Desde).ToListAsync(ct);
                    return Results.Ok(secuencias.Select(s => new Item(s.Id, s.Tipo, ENcf.Formatear(s.Tipo, s.Desde), ENcf.Formatear(s.Tipo, s.Hasta),
                        ENcf.Formatear(s.Tipo, s.Siguiente), Math.Max(0, s.Hasta - s.Siguiente + 1), s.PorcentajeUsado, s.VenceEl, s.Activa,
                        s.PorcentajeUsado >= 80, s.VenceEl.DayNumber - hoy.DayNumber <= 30)));
                })
                .RequirePermiso(Permisos.FiscalGestionar).WithTags("Fiscal");
    }
}

/// <summary>Issued e-CF, newest first, optionally only those still waiting to be sent.</summary>
internal static class ListarDocumentos
{
    public sealed record Item(Guid Id, string ENcf, TipoEcf Tipo, Guid ReferenciaId, decimal Total, DateTime FechaFirmaUtc, EstadoEnvio Estado,
        DateTime FechaLimiteEnvioUtc, string? UltimoError);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/documentos", async (bool? soloPendientes, int? ultimos, FiscalDbContext db, CancellationToken ct) =>
                {
                    var q = db.Documentos.AsNoTracking();
                    if (soloPendientes == true) q = q.Where(d => d.Estado == EstadoEnvio.Pendiente);
                    return Results.Ok(await q.OrderByDescending(d => d.FechaFirmaUtc).Take(Math.Clamp(ultimos ?? 100, 1, 1000))
                        .Select(d => new Item(d.Id, d.ENcf, d.Tipo, d.ReferenciaId, d.Total, d.FechaFirmaUtc, d.Estado, d.FechaLimiteEnvioUtc, d.UltimoError))
                        .ToListAsync(ct));
                })
                .RequirePermiso(Permisos.FiscalGestionar).WithTags("Fiscal");
    }
}
