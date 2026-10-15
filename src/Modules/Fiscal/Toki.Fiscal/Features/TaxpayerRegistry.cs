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
internal static class TaxpayerRegistry
{
    private const int BatchSize = 5000;

    public sealed record Imported(int Taxpayers);
    public sealed record Response(string Rnc, string Name, string? TradeName, bool IsActive);

    /// <summary>"RNC|RAZÓN SOCIAL|NOMBRE COMERCIAL|…|ESTADO|…". Null for the header, blank or malformed lines.</summary>
    /// <remarks>shortcut: the state is read from column 10 as DGII publishes it today; check the layout when DGII changes the file.</remarks>
    public static Taxpayer? Read(string line)
    {
        var c = line.Split('|');
        var rnc = Rnc.Normalize(c[0]);
        if (c.Length < 2 || !Rnc.IsValid(rnc) || string.IsNullOrWhiteSpace(c[1])) return null;
        string? Field(int i) => i < c.Length && c[i].Trim() is { Length: > 0 } v ? (v.Length > 250 ? v[..250] : v) : null;
        return new Taxpayer
        {
            Rnc = rnc!, Name = Field(1)!, TradeName = Field(2),
            IsActive = Field(9) is not { } status || status.Equals("ACTIVO", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>DGII has published the file as ANSI (Windows-1252); if a future one is UTF-8, its first 64 KB decode strictly and it is read as such.</summary>
    internal static async Task<Encoding> DetectEncodingAsync(Stream file, CancellationToken ct)
    {
        var head = new byte[64 * 1024];
        var read = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        // A multi-byte character may be cut at the end of the sample: leave its last 3 bytes out.
        var sample = head.AsSpan(0, read == head.Length ? read - 3 : read);
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(sample);
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
            app.MapPost("/api/fiscal/taxpayer-registry", async (HttpContext http, FiscalDbContext db, ILogger<Imported> log, CancellationToken ct) =>
                {
                    if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 300_000_000;
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    await db.Taxpayers.ExecuteDeleteAsync(ct);
                    db.ChangeTracker.AutoDetectChangesEnabled = false;

                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    http.Request.EnableBuffering(1024 * 1024, 300_000_000);
                    var encoding = await DetectEncodingAsync(http.Request.Body, ct);
                    http.Request.Body.Position = 0;
                    using var reader = new StreamReader(http.Request.Body, encoding);
                    while (await reader.ReadLineAsync(ct) is { } line)
                    {
                        if (Read(line) is not { } c || !seen.Add(c.Rnc)) continue;
                        db.Taxpayers.Add(c);
                        if (seen.Count % BatchSize == 0) await SaveAsync();
                    }
                    await SaveAsync();
                    await tx.CommitAsync(ct);
                    log.LogInformation("RNC taxpayer registry imported: {Count} taxpayers", seen.Count);
                    return Results.Ok(new Imported(seen.Count));

                    async Task SaveAsync()
                    {
                        await db.SaveChangesAsync(ct);
                        db.ChangeTracker.Clear();
                    }
                })
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");

            app.MapGet("/api/fiscal/taxpayers/{rnc}", async (string rnc, FiscalDbContext db, CancellationToken ct) =>
                    await db.Taxpayers.AsNoTracking().Where(c => c.Rnc == (Rnc.Normalize(rnc) ?? "")).Select(c => new Response(c.Rnc, c.Name, c.TradeName, c.IsActive))
                        .FirstOrDefaultAsync(ct) is { } r
                        ? Results.Ok(r)
                        : EndpointExtensions.Problem(Error.NotFound("Fiscal.RncNotFound", "Ese RNC o cédula no está en el padrón de la DGII.")))
                .RequireAuthorization().WithTags("Fiscal");
        }
    }
}
