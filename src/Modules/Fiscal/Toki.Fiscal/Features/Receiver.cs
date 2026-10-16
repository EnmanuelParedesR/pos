using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Settings.Contracts;
using Toki.SharedKernel;
using Toki.Sync.Contracts;

namespace Toki.Fiscal.Features;

/// <summary>Calls to the cloud's e-CF receiver (TK-151, TK-154) as this node. A refusal of the cloud comes back with its own code and text.</summary>
internal static class ReceiverCloud
{
    /// <summary>The consent text version the store shows (the cloud refuses another one). Text: docs/ecf-propio.md §10.3.</summary>
    public const string ConsentVersion = "2026-10";

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

/// <summary>TK-151: the receiver as the owner sees it in Configuración → Comprobantes: active or not, the certificate in the cloud and the URLs to register
/// in the DGII (certification steps 1, 7 and 12).</summary>
internal static class ReceiverStatus
{
    public sealed record Urls(string Environment, string Reception, string Approval, string Authentication);

    public sealed record Response(bool Active, string? Rnc = null, string? Subject = null, DateTime? NotAfterUtc = null, DateTime? ConsentedUtc = null,
        string? OwnerName = null, string? HolderName = null, IReadOnlyList<Urls>? Urls = null);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/fiscal/receiver", async (ICloudClient cloud, CancellationToken ct) =>
                    (await ReceiverCloud.PostAsync<Response>(cloud, "/fiscal/v1/receiver/status", new { }, ct)).ToHttp())
                .RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
    }
}

/// <summary>TK-151: the owner and the certificate holder accept the consent (docs/ecf-propio.md §10.3) and the .p12 goes, once, through the node's
/// authenticated channel to the cloud, which imports it non-exportable in Key Vault. The password is typed again here and never stored, here or there;
/// the audit trail keeps who consented, never the password.</summary>
internal static class GiveReceiverConsent
{
    /// <param name="File">The .p12/.pfx in Base64.</param>
    public sealed record Request(string? File, string? Password, string? OwnerName, string? HolderName, bool Accepted);

    internal sealed record Recorded(string? OwnerName, string? HolderName, string? Rnc, string? Subject, bool Revoked) : ICommand<Ok>;

    internal sealed class Handler(IAuditLog audit) : ICommandHandler<Recorded, Ok>
    {
        public async Task<Result<Ok>> Handle(Recorded c, CancellationToken ct)
        {
            await audit.RecordAsync(c.Revoked ? "fiscal.receiver-revoke" : "fiscal.receiver-consent", "FeReceiver", Guid.Empty,
                new { c.OwnerName, c.HolderName, c.Rnc, c.Subject, ConsentVersion = c.Revoked ? null : ReceiverCloud.ConsentVersion }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/fiscal/receiver/consent", async (Request r, ICloudClient cloud, ISettingsQueries settings, ICommandHandler<Recorded, Ok> record,
                CancellationToken ct) =>
            {
                var rnc = (await settings.GetBusinessProfileAsync(ct)).Rnc;
                var status = await ReceiverCloud.PostAsync<ReceiverStatus.Response>(cloud, "/fiscal/v1/receiver/certificate", new
                {
                    r.File, r.Password, Rnc = rnc, OwnerName = r.OwnerName?.Trim(), HolderName = r.HolderName?.Trim(), r.Accepted,
                    ConsentVersion = ReceiverCloud.ConsentVersion,
                }, ct);
                if (status.IsFailure) return EndpointExtensions.Problem(status.Error!);
                await record.Handle(new Recorded(r.OwnerName?.Trim(), r.HolderName?.Trim(), rnc, status.Value.Subject, false), ct);
                return Results.Ok(status.Value);
            }).RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");

            app.MapPost("/api/fiscal/receiver/revoke", async (ICloudClient cloud, ICommandHandler<Recorded, Ok> record, CancellationToken ct) =>
            {
                var status = await ReceiverCloud.PostAsync<ReceiverStatus.Response>(cloud, "/fiscal/v1/receiver/revoke", new { }, ct);
                if (status.IsFailure) return EndpointExtensions.Problem(status.Error!);
                await record.Handle(new Recorded(null, null, null, null, true), ct);
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

    internal sealed record Recorded(Guid Id, ReceivedDocumentsPuller.Item Answer) : ICommand<Item>;

    internal sealed class Handler(FiscalDbContext db, IAuditLog audit) : ICommandHandler<Recorded, Item>
    {
        public async Task<Result<Item>> Handle(Recorded c, CancellationToken ct)
        {
            var d = await db.Set<ReceivedDocument>().SingleAsync(x => x.Id == c.Id, ct);
            var a = c.Answer;
            (d.Approval, d.ApprovalReason, d.ApprovalUtc, d.ApprovalDeliveredUtc, d.ApprovalDgiiUtc, d.ApprovalMessage) =
                (a.Approval, a.ApprovalReason, a.ApprovalUtc, a.ApprovalDeliveredUtc, a.ApprovalDgiiUtc, a.ApprovalMessage);
            await audit.RecordAsync("fiscal.commercial-approval", "ReceivedDocument", d.Id, new { d.IssuerRnc, d.ENcf, d.Approval, d.ApprovalReason }, ct);
            return Of(d);
        }
    }

    public static Item Of(ReceivedDocument d) => new(d.Id, d.Environment, d.IssuerRnc, d.IssuerName, d.ENcf, d.Type, d.IssuedOn, d.Total, d.Itbis, d.ReceivedUtc,
        d.Approval, d.ApprovalReason, d.ApprovalUtc, d.ApprovalDeliveredUtc, d.ApprovalDgiiUtc, d.ApprovalMessage);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/fiscal/received", async (FiscalDbContext db, CancellationToken ct) =>
                    Results.Ok((await db.Set<ReceivedDocument>().AsNoTracking().OrderByDescending(d => d.CloudId).Take(200).ToListAsync(ct)).Select(Of)))
                .RequirePermission(Permissions.PurchasingManage).WithTags("Fiscal");

            // The cloud signs (ACECF) and sends it to the supplier and the DGII; pressing again retries only what did not go.
            app.MapPost("/api/fiscal/received/{id:guid}/approval", async (Guid id, ApprovalRequest r, FiscalDbContext db, ICloudClient cloud,
                ICommandHandler<Recorded, Item> record, CancellationToken ct) =>
            {
                var cloudId = await db.Set<ReceivedDocument>().Where(d => d.Id == id).Select(d => (long?)d.CloudId).SingleOrDefaultAsync(ct);
                if (cloudId is null) return EndpointExtensions.Problem(Error.NotFound("Fiscal.ReceivedNotFound", "Ese comprobante recibido no existe."));
                var answer = await ReceiverCloud.PostAsync<ReceivedDocumentsPuller.Item>(cloud, $"/fiscal/v1/received/{cloudId}/approval", r, ct);
                if (answer.IsFailure) return EndpointExtensions.Problem(answer.Error!);
                return (await record.Handle(new Recorded(id, answer.Value), ct)).ToHttp();
            }).RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
        }
    }
}
