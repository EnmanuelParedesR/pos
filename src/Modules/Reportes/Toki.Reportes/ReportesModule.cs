using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Security;
using Toki.Caja.Contracts;
using Toki.Clientes.Contracts;
using Toki.Compras.Contracts;
using Toki.Inventario.Contracts;
using Toki.SharedKernel;
using Toki.Ventas.Contracts;

namespace Toki.Reportes;

/// <summary>Reports module: no tables of its own (ADR-0004). It reads the other modules' report contracts and exports them.</summary>
public static class ReportesModule
{
}

/// <summary>Business days to report on (UTC−4, both included) and the output: json (default), csv or xlsx.</summary>
internal readonly record struct Periodo(DateOnly Desde, DateOnly Hasta, string? Formato)
{
    /// <summary>Reports aggregate in memory (see VentasReportes); a year is the largest range.</summary>
    public const int MaxDias = 366;

    public IResult? Invalido() => Hasta < Desde || Hasta.DayNumber - Desde.DayNumber >= MaxDias
        ? EndpointExtensions.Problema(Error.Validation("Reportes.PeriodoInvalido", $"El período va de 'desde' a 'hasta' y tiene como máximo {MaxDias} días."))
        : null;

    public string Nombre(string reporte) => $"{reporte}-{Desde:yyyyMMdd}-{Hasta:yyyyMMdd}";
}

internal sealed record VentaCategoria(Guid CategoriaId, string Categoria, decimal NetoSinItbis, decimal Costo, decimal Utilidad);

internal sealed class ReportesEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/reportes").RequirePermiso(Permisos.ReportesVer).WithTags("Reportes");

        g.MapGet("/ventas/por-dia", async ([AsParameters] Periodo p, IVentasReportes ventas, CancellationToken ct) =>
            p.Invalido() ?? Exportar.Como(await ventas.PorDiaAsync(p.Desde, p.Hasta, ct), p.Formato, p.Nombre("ventas-por-dia")));

        g.MapGet("/ventas/por-producto", async ([AsParameters] Periodo p, IVentasReportes ventas, CancellationToken ct) =>
            p.Invalido() ?? Exportar.Como(await ventas.PorProductoAsync(p.Desde, p.Hasta, ct), p.Formato, p.Nombre("ventas-por-producto")));

        g.MapGet("/ventas/por-categoria", async ([AsParameters] Periodo p, IVentasReportes ventas, IInventarioReportes inventario, CancellationToken ct) =>
        {
            if (p.Invalido() is { } invalido) return invalido;
            var nombres = await inventario.CategoriasAsync(ct);
            var filas = (await ventas.PorProductoAsync(p.Desde, p.Hasta, ct)).GroupBy(x => x.CategoriaId)
                .Select(c => new VentaCategoria(c.Key, nombres.GetValueOrDefault(c.Key, "?"), c.Sum(x => x.NetoSinItbis), c.Sum(x => x.Costo), c.Sum(x => x.Utilidad)))
                .OrderByDescending(c => c.NetoSinItbis).ToList();
            return Exportar.Como(filas, p.Formato, p.Nombre("ventas-por-categoria"));
        });

        g.MapGet("/ventas/por-cajero", async ([AsParameters] Periodo p, IVentasReportes ventas, CancellationToken ct) =>
            p.Invalido() ?? Exportar.Como(await ventas.PorCajeroAsync(p.Desde, p.Hasta, ct), p.Formato, p.Nombre("ventas-por-cajero")));

        g.MapGet("/turnos", async ([AsParameters] Periodo p, ICajaReportes caja, CancellationToken ct) =>
            p.Invalido() ?? Exportar.Como(await caja.TurnosAsync(p.Desde, p.Hasta, ct), p.Formato, p.Nombre("turnos")));

        g.MapGet("/inventario/valor", async (string? formato, IInventarioReportes inventario, TimeProvider reloj, CancellationToken ct) =>
            Exportar.Como(await inventario.ValorAsync(ct), formato, $"inventario-{DiaNegocio.De(reloj.GetUtcNow().UtcDateTime):yyyyMMdd}"));

        // shortcut: rows of the 607 and 606 as CSV/Excel to paste into the DGII tool; the official TXT layout comes with the real e-CF provider (TK-055).
        g.MapGet("/dgii/607", async ([AsParameters] Periodo p, IVentasReportes ventas, CancellationToken ct) =>
            p.Invalido() ?? Exportar.Como(await ventas.Documentos607Async(p.Desde, p.Hasta, ct), p.Formato, p.Nombre("607")));
        g.MapGet("/dgii/606", async ([AsParameters] Periodo p, IComprasReportes compras, CancellationToken ct) =>
            p.Invalido() ?? Exportar.Como(await compras.Documentos606Async(p.Desde, p.Hasta, ct), p.Formato, p.Nombre("606")));

        g.MapGet("/por-cobrar", async (string? formato, IClientesReportes clientes, TimeProvider reloj, CancellationToken ct) =>
            Exportar.Como(await clientes.PorCobrarAsync(ct), formato, $"por-cobrar-{DiaNegocio.De(reloj.GetUtcNow().UtcDateTime):yyyyMMdd}"));
        g.MapGet("/por-pagar", async (string? formato, IComprasReportes compras, TimeProvider reloj, CancellationToken ct) =>
            Exportar.Como(await compras.PorPagarAsync(ct), formato, $"por-pagar-{DiaNegocio.De(reloj.GetUtcNow().UtcDateTime):yyyyMMdd}"));
    }
}
