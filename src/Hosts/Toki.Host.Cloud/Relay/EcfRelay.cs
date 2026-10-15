using System.IO.Compression;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.Fiscal.Contracts;
using Toki.Host.Cloud.Nodes;
using Toki.Host.Cloud.Persistence;
using Toki.SharedKernel;

namespace Toki.Host.Cloud.Relay;

/// <param name="SignedXmlGzip">The signed e-CF XML, gzip-compressed, base64.</param>
public sealed record RelayEcfRequest(string? Encf, string? SignedXmlGzip);
public sealed record RelayEcfResponse(EcfRelayStatus Status, string TrackId, string Message);

public sealed record EcfSubmission(Guid TenantId, Guid StoreId, string ENcf, EcfType Type, byte[] SignedXml);
public sealed record EcfSubmissionResult(EcfRelayStatus Status, string TrackId, string Message);

/// <summary>Who actually talks to DGII from the cloud. A real provider's API key lives only here (Key Vault), never in the store.</summary>
public interface ICloudEcfProvider
{
    Task<EcfSubmissionResult> SubmitAsync(EcfSubmission submission, CancellationToken ct);
}

/// <summary>shortcut: DGII is on hold; every document is answered as Simulated. The real provider is TK-055.</summary>
internal sealed class SimulatedCloudEcfProvider : ICloudEcfProvider
{
    public Task<EcfSubmissionResult> SubmitAsync(EcfSubmission submission, CancellationToken ct) =>
        Task.FromResult(new EcfSubmissionResult(EcfRelayStatus.Simulated, $"SIM-{Guid.CreateVersion7():N}",
            "Simulado: el envío a la DGII está en espera; el documento quedó guardado en la nube."));
}

internal static class EcfRelayEndpoint
{
    public const int MaxXmlBytes = 1_000_000;

    public static void MapEcfRelay(this IEndpointRouteBuilder app) =>
        app.MapPost("/relay/v1/ecf", async (RelayEcfRequest request, ClaimsPrincipal user, CloudDbContext db, ICloudEcfProvider provider, TimeProvider clock, CancellationToken ct) =>
        {
            var node = NodeIdentity.From(user);
            var encf = request.Encf?.Trim() ?? "";
            var gzip = request.SignedXmlGzip is null ? null : TryBase64(request.SignedXmlGzip);
            var xml = gzip is null ? null : TryGunzip(gzip);
            if (TypeOf(encf) is not { } type || xml is not { Length: > 0 })
                return EndpointExtensions.Problem(Error.Validation("Relay.InvalidDocument", "El documento no trae un e-NCF válido o el XML firmado (gzip, base64)."));

            // The e-NCF is the natural key: a node that retries gets the first answer back, and DGII never sees it twice.
            var existing = await db.EcfRelays.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == node.TenantId && r.ENcf == encf, ct);
            if (existing is not null) return Results.Ok(new RelayEcfResponse(existing.Status, existing.TrackId, existing.Message));

            var result = await provider.SubmitAsync(new EcfSubmission(node.TenantId, node.StoreId, encf, type, xml), ct);
            db.EcfRelays.Add(new EcfRelayRecord
            {
                Id = Guid.CreateVersion7(), TenantId = node.TenantId, StoreId = node.StoreId, NodeId = node.NodeId, ENcf = encf, EcfType = type,
                SignedXmlGzip = gzip!, ReceivedUtc = clock.GetUtcNow().UtcDateTime, Status = result.Status, TrackId = result.TrackId, Message = result.Message,
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                // The same e-NCF arrived twice at once: answer with the one that was saved first.
                db.ChangeTracker.Clear();
                var first = await db.EcfRelays.AsNoTracking().SingleAsync(r => r.TenantId == node.TenantId && r.ENcf == encf, ct);
                return Results.Ok(new RelayEcfResponse(first.Status, first.TrackId, first.Message));
            }
            return Results.Ok(new RelayEcfResponse(result.Status, result.TrackId, result.Message));
        }).RequireAuthorization(CloudAuth.NodePolicy);

    /// <summary>An e-NCF is "E" + the 2-digit e-CF type + 10 digits (E320000000001).</summary>
    private static EcfType? TypeOf(string encf) =>
        encf.Length == 13 && encf[0] == 'E' && encf[1..].All(char.IsAsciiDigit) && Enum.IsDefined((EcfType)int.Parse(encf[1..3], System.Globalization.CultureInfo.InvariantCulture))
            ? (EcfType)int.Parse(encf[1..3], System.Globalization.CultureInfo.InvariantCulture) : null;

    private static byte[]? TryBase64(string value)
    {
        var buffer = new byte[value.Length * 3 / 4 + 3];
        return Convert.TryFromBase64String(value, buffer, out var written) ? buffer[..written] : null;
    }

    /// <summary>Null when it is not gzip or unpacks to more than <see cref="MaxXmlBytes"/>.</summary>
    private static byte[]? TryGunzip(byte[] gzip)
    {
        try
        {
            using var input = new GZipStream(new MemoryStream(gzip), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxXmlBytes) return null;
            }
            return output.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }
}
