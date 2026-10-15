using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Security;
using Toki.Fiscal.Domain;
using Toki.SharedKernel;

namespace Toki.Fiscal.Features;

/// <summary>DGII taxpayer registry: the owner uploads DGII_RNC.TXT (pipe-separated, ANSI) and the register looks names up by RNC or cédula.</summary>
internal static class Padron
{
    private const int Lote = 5000;

    public sealed record Importado(int Contribuyentes);
    public sealed record Response(string Rnc, string Nombre, string? NombreComercial, bool Activo);

    /// <summary>"RNC|RAZÓN SOCIAL|NOMBRE COMERCIAL|…|ESTADO|…". Null for the header, blank or malformed lines.</summary>
    /// <remarks>shortcut: the state is read from column 10 as DGII publishes it today; check the layout when DGII changes the file.</remarks>
    public static Contribuyente? Leer(string linea)
    {
        var c = linea.Split('|');
        var rnc = Rnc.Normalizar(c[0]);
        if (c.Length < 2 || !Rnc.EsValido(rnc) || string.IsNullOrWhiteSpace(c[1])) return null;
        string? Campo(int i) => i < c.Length && c[i].Trim() is { Length: > 0 } v ? (v.Length > 250 ? v[..250] : v) : null;
        return new Contribuyente
        {
            Rnc = rnc!, Nombre = Campo(1)!, NombreComercial = Campo(2),
            Activo = Campo(9) is not { } estado || estado.Equals("ACTIVO", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>DGII has published the file as ANSI (Windows-1252); if a future one is UTF-8, its first 64 KB decode strictly and it is read as such.</summary>
    internal static async Task<Encoding> CodificacionAsync(Stream archivo, CancellationToken ct)
    {
        var inicio = new byte[64 * 1024];
        var leidos = 0;
        while (leidos < inicio.Length && await archivo.ReadAsync(inicio.AsMemory(leidos), ct) is var n and > 0) leidos += n;
        // A multi-byte character may be cut at the end of the sample: leave its last 3 bytes out.
        var muestra = inicio.AsSpan(0, leidos == inicio.Length ? leidos - 3 : leidos);
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(muestra);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            // Replaces the whole registry in one transaction: a failed upload leaves the previous one.
            app.MapPost("/api/fiscal/padron", async (HttpContext http, FiscalDbContext db, ILogger<Importado> log, CancellationToken ct) =>
                {
                    if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limite) limite.MaxRequestBodySize = 300_000_000;
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    await db.Contribuyentes.ExecuteDeleteAsync(ct);
                    db.ChangeTracker.AutoDetectChangesEnabled = false;

                    var vistos = new HashSet<string>(StringComparer.Ordinal);
                    http.Request.EnableBuffering(1024 * 1024, 300_000_000);
                    var codificacion = await CodificacionAsync(http.Request.Body, ct);
                    http.Request.Body.Position = 0;
                    using var lector = new StreamReader(http.Request.Body, codificacion);
                    while (await lector.ReadLineAsync(ct) is { } linea)
                    {
                        if (Leer(linea) is not { } c || !vistos.Add(c.Rnc)) continue;
                        db.Contribuyentes.Add(c);
                        if (vistos.Count % Lote == 0) await GuardarAsync();
                    }
                    await GuardarAsync();
                    await tx.CommitAsync(ct);
                    log.LogInformation("Padrón RNC importado: {Cantidad} contribuyentes", vistos.Count);
                    return Results.Ok(new Importado(vistos.Count));

                    async Task GuardarAsync()
                    {
                        await db.SaveChangesAsync(ct);
                        db.ChangeTracker.Clear();
                    }
                })
                .RequirePermiso(Permisos.FiscalGestionar).WithTags("Fiscal");

            app.MapGet("/api/fiscal/contribuyentes/{rnc}", async (string rnc, FiscalDbContext db, CancellationToken ct) =>
                    await db.Contribuyentes.AsNoTracking().Where(c => c.Rnc == (Rnc.Normalizar(rnc) ?? "")).Select(c => new Response(c.Rnc, c.Nombre, c.NombreComercial, c.Activo))
                        .FirstOrDefaultAsync(ct) is { } r
                        ? Results.Ok(r)
                        : EndpointExtensions.Problema(Error.NotFound("Fiscal.RncNoEncontrado", "Ese RNC o cédula no está en el padrón de la DGII.")))
                .RequireAuthorization().WithTags("Fiscal");
        }
    }
}
