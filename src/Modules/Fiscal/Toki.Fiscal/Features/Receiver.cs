using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Fiscal.Contracts;
using Toki.Fiscal.Domain;
using Toki.Settings.Contracts;
using Toki.SharedKernel;
using Toki.Sync.Contracts;

namespace Toki.Fiscal.Features;

/// <summary>Calls to the cloud's e-CF receiver (TK-151, TK-154, TK-246) as this node. A refusal of the cloud comes back with its own code and text.</summary>
internal static class ReceiverCloud
{
    /// <summary>The consent text versions the store shows (the cloud refuses another one). Text: docs/ecf-propio.md §10.3.</summary>
    public const string ConsentVersion = "2026-10";
    public const string StoreSignedConsentVersion = "2026-10-tienda";

    /// <summary>TK-246: the store signs (default) or a copy of the certificate in Key Vault (only while the cloud allows it).</summary>
    public const string StoreSigned = "StoreSigned";
    public const string CloudCustody = "CloudCustody";

    public static readonly Error NotConnected = Error.Conflict("Fiscal.CloudNotConnected",
        "Conecte la tienda a la nube de Toki: los servicios de receptor de comprobantes viven allí.");
    public static readonly Error Unreachable = Error.Conflict("Fiscal.CloudUnreachable", "No se pudo hablar con la nube de Toki. Revise el internet e intente de nuevo.");

    /// <param name="notConnected">What to tell the owner when the store is not connected (default: the receiver's text).</param>
    public static async Task<Result<T>> PostAsync<T>(ICloudClient cloud, string path, object body, CancellationToken ct, Error? notConnected = null)
    {
        try
        {
            using var response = await cloud.PostAsJsonAsync(path, body, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(ct))!;
            return await RefusalAsync(response, ct);
        }
        catch (InvalidOperationException) { return notConnected ?? NotConnected; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) { return Unreachable; }
    }

    private static async Task<Error> RefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null, detail = null;
        try
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            (code, detail) = (json.TryGetProperty("code", out var c) ? c.GetString() : null, json.TryGetProperty("detail", out var d) ? d.GetString() : null);
        }
        catch (JsonException) { }
        code ??= "Fiscal.CloudRefused";
        detail ??= $"La nube respondió {(int)response.StatusCode}.";
        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => Error.Validation(code, detail),
            HttpStatusCode.NotFound => Error.NotFound(code, detail),
            _ => Error.Conflict(code, detail),
        };
    }
}

/// <summary>TK-151: the receiver as the owner sees it in Compras → "Recibidos (e-CF)": active or not, how it signs (TK-246) and the URLs to register in
/// the DGII (certification steps 1, 7 and 12).</summary>
internal static class ReceiverStatus
{
    public sealed record Urls(string Environment, string Reception, string Approval, string Authentication);

    public sealed record Response(bool Active, string? Rnc = null, string? Subject = null, DateTime? NotAfterUtc = null, DateTime? ConsentedUtc = null,
        string? OwnerName = null, string? HolderName = null, IReadOnlyList<Urls>? Urls = null, string? SigningMode = null, string? DefaultSigningMode = null,
        bool CloudCustodyAvailable = false);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/receiver", async (ICloudClient cloud, CancellationToken ct) =>
                    (await ReceiverCloud.PostAsync<Response>(cloud, "/fiscal/v1/receiver/status", new { }, ct)).ToHttp())
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>Activating the receiver in the cloud (docs/ecf-propio.md §10.3, ADR-0011), with the owner's and the certificate holder's consent:
/// <list type="bullet">
/// <item>StoreSigned (TK-246, default): no certificate leaves this PC. The store signs the cloud's challenge with its certificate (the proof that the
/// RNC is its) and from then on signs each acknowledgement on request (<see cref="RemoteSigning"/>).</item>
/// <item>CloudCustody (TK-151, only while the cloud allows it): the .p12 goes, once, through the node's channel to the cloud, which imports it
/// non-exportable in Key Vault. The password is typed again here and never stored, here or there.</item>
/// </list>
/// The audit trail keeps who consented and the mode, never the password.</summary>
internal static class GiveReceiverConsent
{
    /// <param name="Mode">StoreSigned (default) or CloudCustody.</param>
    /// <param name="File">CloudCustody: the .p12/.pfx in Base64.</param>
    public sealed record Request(string? Mode, string? File, string? Password, string? OwnerName, string? HolderName, bool Accepted);

    internal sealed record Recorded(string? OwnerName, string? HolderName, string? Rnc, string? Subject, string? Mode, bool Revoked) : ICommand<Ok>;

    internal sealed class Handler(IAuditLog audit) : ICommandHandler<Recorded, Ok>
    {
        public async Task<Result<Ok>> Handle(Recorded c, CancellationToken ct)
        {
            await audit.RecordAsync(c.Revoked ? "fiscal.receiver-revoke" : "fiscal.receiver-consent", "FeReceiver", Guid.Empty,
                new { c.OwnerName, c.HolderName, c.Rnc, c.Subject, c.Mode, ConsentVersion = c.Revoked ? null : Version(c.Mode) }, ct);
            return Ok.Value;
        }
    }

    private static string Version(string? mode) => mode == ReceiverCloud.CloudCustody ? ReceiverCloud.ConsentVersion : ReceiverCloud.StoreSignedConsentVersion;

    private sealed record ChallengeAnswer(string Challenge);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/fiscal/receiver/consent", async (Request r, ICloudClient cloud, ISettingsQueries settings, CertificateEcfSigner signer,
                ICommandHandler<Recorded, Ok> record, RemoteSigningWake wake, TimeProvider clock, CancellationToken ct) =>
            {
                var rnc = (await settings.GetBusinessProfileAsync(ct)).Rnc;
                var mode = r.Mode == ReceiverCloud.CloudCustody ? ReceiverCloud.CloudCustody : ReceiverCloud.StoreSigned;
                var (owner, holder) = (r.OwnerName?.Trim(), r.HolderName?.Trim());
                Result<ReceiverStatus.Response> status;
                if (mode == ReceiverCloud.CloudCustody)
                    status = await ReceiverCloud.PostAsync<ReceiverStatus.Response>(cloud, "/fiscal/v1/receiver/certificate", new
                    {
                        r.File, r.Password, Rnc = rnc, OwnerName = owner, HolderName = holder, r.Accepted, ConsentVersion = ReceiverCloud.ConsentVersion,
                    }, ct);
                else
                {
                    if (signer.CannotSign(clock.GetUtcNow().UtcDateTime) is { } why)
                        return EndpointExtensions.Problem(Error.Conflict("Fiscal.ReceiverNeedsCertificate",
                            $"Para que la tienda firme por el receptor hace falta el certificado digital: {why}. Impórtelo en Configuración → Comprobantes."));
                    var challenge = await ReceiverCloud.PostAsync<ChallengeAnswer>(cloud, "/fiscal/v1/receiver/challenge", new { }, ct);
                    if (challenge.IsFailure) return EndpointExtensions.Problem(challenge.Error!);
                    // The proof that the RNC is this business's: the cloud's challenge, signed here with the certificate (the cloud checks chain and RNC).
                    var proof = signer.Sign(new XDocument(new XElement("ActivacionReceptor", new XElement("RNC", rnc),
                        new XElement("Reto", challenge.Value.Challenge)))).SignedXml;
                    status = await ReceiverCloud.PostAsync<ReceiverStatus.Response>(cloud, "/fiscal/v1/receiver/activate", new
                    {
                        Proof = proof, Rnc = rnc, OwnerName = owner, HolderName = holder, r.Accepted, ConsentVersion = ReceiverCloud.StoreSignedConsentVersion,
                    }, ct);
                }
                if (status.IsFailure) return EndpointExtensions.Problem(status.Error!);
                await record.Handle(new Recorded(owner, holder, rnc, status.Value.Subject, mode, false), ct);
                if (mode == ReceiverCloud.StoreSigned) wake.Now(); // the signing poll was backing off (not the signing PC until now)
                return Results.Ok(status.Value);
            }).RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");

            app.MapPost("/api/fiscal/receiver/revoke", async (ICloudClient cloud, ICommandHandler<Recorded, Ok> record, CancellationToken ct) =>
            {
                var status = await ReceiverCloud.PostAsync<ReceiverStatus.Response>(cloud, "/fiscal/v1/receiver/revoke", new { }, ct);
                if (status.IsFailure) return EndpointExtensions.Problem(status.Error!);
                await record.Handle(new Recorded(null, null, null, null, null, true), ct);
                return Results.Ok(status.Value);
            }).RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
        }
    }
}

/// <summary>TK-154: "Comprobantes recibidos". What suppliers sent to this business (newest first), to record each one as a purchase (supplier, NCF and
/// amounts for the 606; the purchase screen already refuses a repeated NCF), and the optional commercial approval or rejection.</summary>
internal static class ReceivedDocuments
{
    public sealed record Item(Guid Id, string Environment, string IssuerRnc, string IssuerName, string ENcf, int Type, DateOnly IssuedOn, decimal Total,
        decimal Itbis, DateTime ReceivedUtc, string? Approval, string? ApprovalReason, DateTime? ApprovalUtc, DateTime? ApprovalDeliveredUtc,
        DateTime? ApprovalDgiiUtc, string? ApprovalMessage);

    public sealed record ApprovalRequest(bool? Approve, string? Reason);

    public static Item Of(ReceivedDocument d) => new(d.Id, d.Environment, d.IssuerRnc, d.IssuerName, d.ENcf, d.Type, d.IssuedOn, d.Total, d.Itbis, d.ReceivedUtc,
        d.Approval, d.ApprovalReason, d.ApprovalUtc, d.ApprovalDeliveredUtc, d.ApprovalDgiiUtc, d.ApprovalMessage);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/fiscal/received", async (FiscalDbContext db, CancellationToken ct) =>
                    Results.Ok((await db.Set<ReceivedDocument>().AsNoTracking().OrderByDescending(d => d.CloudId).Take(200).ToListAsync(ct)).Select(Of)))
                .RequirePermission(Permissions.PurchasingManage).WithTags("Fiscal");

            // TK-246: signed here and sent from here to the supplier and the DGII; pressing again retries only what did not go.
            app.MapPost("/api/fiscal/received/{id:guid}/approval", async (Guid id, ApprovalRequest r, CommercialApprovals approvals, CancellationToken ct) =>
                    (await approvals.DecideAsync(id, r, ct)).ToHttp())
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
        }
    }
}

/// <summary>TK-154/TK-246: the owner's commercial approval (or rejection) of a received e-CF. Signed once here (ACECF, XSD-checked) with the store's
/// certificate, sent to the supplier's approval URL (DGII directory; its seed authentication signed here) and to the DGII, whatever mode the receiver
/// is in: no seed is ever signed in the cloud. The cloud learns of it through the outbox (<see cref="CommercialApprovalRecordedV1"/>). The decision
/// cannot change once signed.</summary>
/// <remarks>shortcut: no automatic retry; the admin presses again and only the missing leg goes.</remarks>
internal sealed class CommercialApprovals(FiscalDbContext db, CertificateEcfSigner signer, IEcfProvider provider, ISettingsQueries settings, IHttpClientFactory http,
    ICommandHandler<CommercialApprovals.Decided, ReceivedDocuments.Item> record, TimeProvider clock, ILogger<CommercialApprovals> log)
{
    /// <param name="DecidedBy">TK-250: "panel: {e-mail}" when an owner decided it in the panel; null when decided at this PC (the audit has the user).</param>
    internal sealed record Decided(Guid Id, string Approval, string? Reason, DateTime ApprovalUtc, byte[] XmlGzip, DateTime? DeliveredUtc, DateTime? DgiiUtc,
        string? Message, string? DecidedBy = null) : ICommand<ReceivedDocuments.Item>;

    internal sealed class Handler(FiscalDbContext db, IAuditLog audit, IOutbox outbox) : ICommandHandler<Decided, ReceivedDocuments.Item>
    {
        public async Task<Result<ReceivedDocuments.Item>> Handle(Decided c, CancellationToken ct)
        {
            var d = await db.Set<ReceivedDocument>().SingleAsync(x => x.Id == c.Id, ct);
            var first = d.ApprovalXmlGzip is null;
            if (first) (d.Approval, d.ApprovalReason, d.ApprovalUtc, d.ApprovalXmlGzip) = (c.Approval, c.Reason, c.ApprovalUtc, c.XmlGzip);
            (d.ApprovalDeliveredUtc, d.ApprovalDgiiUtc, d.ApprovalMessage) = (d.ApprovalDeliveredUtc ?? c.DeliveredUtc, d.ApprovalDgiiUtc ?? c.DgiiUtc, c.Message);
            if (first) await audit.RecordAsync("fiscal.commercial-approval", "ReceivedDocument", d.Id, new { d.IssuerRnc, d.ENcf, d.Approval, d.ApprovalReason, c.DecidedBy }, ct);
            outbox.Add(new CommercialApprovalRecordedV1(d.Id, d.CloudId, d.Approval!, d.ApprovalReason, d.ApprovalUtc!.Value, d.ApprovalDeliveredUtc,
                d.ApprovalDgiiUtc, d.ApprovalMessage, d.ApprovalXmlGzip!));
            return ReceivedDocuments.Of(d);
        }
    }

    public async Task<Result<ReceivedDocuments.Item>> DecideAsync(Guid id, ReceivedDocuments.ApprovalRequest r, CancellationToken ct, string? decidedBy = null)
    {
        var d = await db.Set<ReceivedDocument>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return Error.NotFound("Fiscal.ReceivedNotFound", "Ese comprobante recibido no existe.");
        if (r.Approve is not { } approve) return Error.Validation("Fiscal.ApprovalRequired", "Elija aprobar o rechazar.");
        var reason = r.Reason?.Trim();
        if (!approve && (string.IsNullOrEmpty(reason) || reason.Length > 250))
            return Error.Validation("Fiscal.ApprovalReasonRequired", "Escriba el motivo del rechazo (hasta 250 caracteres).");
        var decision = approve ? "Approved" : "Rejected";
        if (d.Approval is not null && d.Approval != decision)
            return Error.Conflict("Fiscal.ApprovalAlreadySigned", "Ese comprobante ya tiene una aprobación comercial firmada con otra decisión: no se puede cambiar.");
        var environment = d.Environment switch
        {
            "testecf" => FiscalEnvironment.TesteCF,
            "certecf" => FiscalEnvironment.CerteCF,
            _ => FiscalEnvironment.eCF,
        };
        if (provider.For(environment) is not DgiiClient dgii)
            return Error.Conflict("Fiscal.ApprovalNoDgii", "La tienda no puede hablar con la DGII en ese ambiente.");
        var now = clock.GetUtcNow().UtcDateTime;
        var buyer = (await settings.GetBusinessProfileAsync(ct)).Rnc ?? "";

        var (approvalUtc, storedReason) = (d.ApprovalUtc ?? now, d.Approval is null ? approve ? null : reason : d.ApprovalReason);
        string xml;
        if (d.ApprovalXmlGzip is { } kept) xml = EcfSubmissionQueue.Unzip(kept);
        else
        {
            if (signer.CannotSign(now) is { } why)
                return Error.Conflict("Fiscal.ApprovalCannotSign", $"No se puede firmar la aprobación: {why}. Importe el certificado en Configuración → Comprobantes.");
            var acecf = new XDocument(new XElement("ACECF", new XElement("DetalleAprobacionComercial",
                new XElement("Version", "1.0"), new XElement("RNCEmisor", d.IssuerRnc), new XElement("eNCF", d.ENcf),
                new XElement("FechaEmision", d.IssuedOn.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)),
                new XElement("MontoTotal", d.Total.ToString("0.00", CultureInfo.InvariantCulture)), new XElement("RNCComprador", buyer),
                new XElement("Estado", approve ? 1 : 2), approve ? null : new XElement("DetalleMotivoRechazo", reason),
                new XElement("FechaHoraAprobacionComercial", (now + BusinessDay.Offset).ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture)))));
            if (EcfXml.Validate(acecf, EcfXml.AcecfSchema) is [var error, ..])
                return Error.Validation("Fiscal.InvalidApproval", $"La aprobación no cumple el formato de la DGII: {error}");
            xml = signer.Sign(acecf).SignedXml;
        }

        var file = $"{buyer}{d.ENcf}.xml";
        var messages = new List<string>();
        DateTime? delivered = null, taken = null;
        if (d.ApprovalDeliveredUtc is null)
        {
            try
            {
                if (environment == FiscalEnvironment.CerteCF) messages.Add("En CerteCF no hay directorio: la aprobación va solo a la DGII.");
                else if (await dgii.DirectoryAsync(d.IssuerRnc, ct) is { Approval: { } url, Authentication: var auth })
                {
                    await TaxpayerReceiver.PostAsync(http.CreateClient(EcfDeliveryQueue.HttpClientName), signer, url, auth, "aprobacioncomercial/api/ecf", file, xml, ct);
                    delivered = clock.GetUtcNow().UtcDateTime;
                }
                else messages.Add("El suplidor no aparece en el directorio de la DGII: la aprobación va solo a la DGII.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Could not deliver the commercial approval of {ENcf} to its issuer", d.ENcf);
                messages.Add($"No se pudo entregar al suplidor: {e.Message}");
            }
        }
        if (d.ApprovalDgiiUtc is null)
        {
            try
            {
                var answer = await dgii.SendCommercialApprovalAsync(buyer, d.ENcf, xml, ct);
                if (answer.State == DgiiState.Accepted) taken = clock.GetUtcNow().UtcDateTime;
                else messages.Add($"La DGII no la tomó: {answer.Messages ?? "sin mensaje"}");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Could not send the commercial approval of {ENcf} to the DGII", d.ENcf);
                messages.Add($"No se pudo enviar a la DGII: {e.Message}");
            }
        }
        var message = messages.Count == 0 ? null : string.Join(" ", messages);
        if (message?.Length > 1000) message = message[..1000];
        return await record.Handle(new Decided(d.Id, d.Approval ?? decision, storedReason, approvalUtc, EcfSubmissionQueue.Zip(xml), delivered, taken, message, decidedBy), ct);
    }
}
