using System.IO.Compression;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Fiscal.Contracts;
using Toki.Host.Cloud.Fe;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Relay;

/// <param name="SignedXmlGzip">The signed e-CF XML, gzip-compressed, base64.</param>
/// <param name="RfceGzip">The signed RFCE of a 32 under RD$250,000 (gzip, base64), when there is one.</param>
public sealed record ArchiveEcfRequest(string? Encf, string? Environment, EcfRelayStatus? Status, string? TrackId, int? DgiiCode, string? Messages,
    DateTime? SignedUtc, string? SignedXmlGzip, string? RfceGzip, string? ReplacesENcf, string? ReplacedByENcf);

/// <summary>The e-CF archive (docs/ecf-propio.md §10.4; replaces the ADR-0008 relay). The store talks to the DGII itself and, once a document has
/// its final result, uploads the signed XML, the RFCE and the DGII answer here: the 10-year copy outside the store and the panel's fiscal status.
/// Nothing is sent to the DGII from here. The environment and the e-NCF are the natural key: a later upload (a rejection that got its replacement)
/// updates the row.</summary>
/// <remarks>The table keeps the relay's name (<c>EcfRelays</c>). After <c>Archive:HotDays</c> the XML moves to cold storage (TK-207, <see cref="ColdArchiver"/>)
/// and the row stays as the index. shortcut: a later upload of a document already in cold storage updates the status only; its XML is immutable
/// there (the RFCE is signed with the e-CF, so it always comes in the first upload).</remarks>
internal static class EcfArchiveEndpoint
{
    public const int MaxXmlBytes = 1_000_000;

    /// <summary>R11 B-Med3: uploads per store PC (<c>Archive:UploadsPerMinutePerNode</c>, 120).</summary>
    public const string NodeRateLimit = "archive-node";

    public static void MapEcfArchive(this IEndpointRouteBuilder app) =>
        app.MapPost("/fiscal/v1/archive", async (ArchiveEcfRequest request, ClaimsPrincipal user, CloudDbContext db, ColdArchiveSignal cold, TimeProvider clock, CancellationToken ct) =>
        {
            var node = NodeIdentity.From(user);
            var encf = request.Encf?.Trim() ?? "";
            var gzip = Unpacked(request.SignedXmlGzip);
            var rfce = request.RfceGzip is null ? null : Unpacked(request.RfceGzip);
            var environment = EnvironmentOf(request.Environment);
            if (TypeOf(encf) is not { } type || gzip is null || (request.RfceGzip is not null && rfce is null) || request.Status is null || environment is null)
                return EndpointExtensions.Problem(Error.Validation("Archive.InvalidDocument",
                    "El documento no trae un e-NCF válido, el ambiente de la DGII, el estado o el XML firmado (gzip, base64)."));

            var now = clock.GetUtcNow().UtcDateTime;
            for (var attempt = 1; ; attempt++)
            {
                // R9 #4: the same e-NCF in TesteCF, CerteCF and eCF are three documents; a test one never overwrites production's.
                var row = await db.EcfRelays.SingleOrDefaultAsync(r => r.TenantId == node.TenantId && r.Environment == environment && r.ENcf == encf, ct);
                if (row is null)
                    db.EcfRelays.Add(row = new EcfRelayRecord
                    {
                        Id = Guid.CreateVersion7(), TenantId = node.TenantId, Environment = environment, ENcf = encf, EcfType = type, ReceivedUtc = now,
                    });
                if (row.ColdUtc is null) (row.SignedXmlGzip, row.RfceGzip) = (gzip, rfce);
                (row.StoreId, row.NodeId, row.Status, row.TrackId, row.DgiiCode, row.Message) =
                    (node.StoreId, node.NodeId, request.Status.Value, FeXml.Cut(request.TrackId, 100), request.DgiiCode, FeXml.Cut(request.Messages, 1000));
                (row.SignedUtc, row.ReplacesENcf, row.ReplacedByENcf, row.UpdatedUtc) =
                    (request.SignedUtc, FeXml.Cut(request.ReplacesENcf, 13), FeXml.Cut(request.ReplacedByENcf, 13), now);
                try
                {
                    await db.SaveChangesAsync(ct);
                    cold.Notify();
                    return Results.Ok();
                }
                catch (DbUpdateException) when (attempt == 1)
                {
                    db.ChangeTracker.Clear(); // the same e-NCF arrived twice at once: update the row the other request saved
                }
            }
        }).RequireAuthorization(CloudAuth.NodePolicy).RequireRateLimiting(NodeRateLimit);


    /// <summary>The DGII environments a document is archived for, as the store names them.</summary>
    public static readonly string[] Environments = ["TesteCF", "CerteCF", "eCF"];

    /// <summary>The canonical name of a DGII environment in any case, or null (Simulated documents are never archived).</summary>
    public static string? EnvironmentOf(string? value) => Environments.FirstOrDefault(e => string.Equals(e, value?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>An e-NCF is "E" + the 2-digit e-CF type + 10 digits (E320000000001).</summary>
    private static EcfType? TypeOf(string encf) =>
        encf.Length == 13 && encf[0] == 'E' && encf[1..].All(char.IsAsciiDigit) && Enum.IsDefined((EcfType)int.Parse(encf[1..3], System.Globalization.CultureInfo.InvariantCulture))
            ? (EcfType)int.Parse(encf[1..3], System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>The gzip bytes, or null when it is not base64 gzip or unpacks to more than <see cref="MaxXmlBytes"/>.</summary>
    private static byte[]? Unpacked(string? base64)
    {
        if (base64 is null) return null;
        var buffer = new byte[base64.Length * 3 / 4 + 3];
        if (!Convert.TryFromBase64String(base64, buffer, out var written)) return null;
        var gzip = buffer[..written];
        try
        {
            using var input = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(chunk)) > 0)
                if ((total += read) > MaxXmlBytes) return null;
            return total > 0 ? gzip : null;
        }
        catch (InvalidDataException) { return null; }
    }
}
