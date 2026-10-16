using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Settings.Contracts;
using Toki.SharedKernel;
using Toki.Sync.Contracts;

namespace Toki.Fiscal.Features;

/// <summary>TK-233: "Solicitar acompañamiento" (docs/servicio-certificacion.md). The request goes straight to the cloud through the node's channel, so
/// the owner knows at once that it arrived; the cloud e-mails Toki staff. Without the cloud there is nothing to send: the owner is told to connect.</summary>
internal static class Accompaniment
{
    public static readonly Error NotConnected = Error.Conflict("Fiscal.CloudNotConnected",
        "Conecte la tienda a la nube de Toki (Configuración → Nube) para pedir el acompañamiento. También puede escribirnos por WhatsApp.");

    /// <param name="Status">New, Scheduled, Session1Done, Session2Done, Certified or Cancelled.</param>
    public sealed record Request(Guid Id, string Status, string ContactName, string Phone, string PreferredTimes, DateTime? Session1Utc, DateTime? Session2Utc,
        DateTime CreatedUtc);

    public sealed record CloudState(Request? Request);

    /// <param name="SuggestedName">To prefill the form: who is signed in.</param>
    /// <param name="SuggestedPhone">To prefill the form: the business phone.</param>
    public sealed record Response(bool Connected, Request? Request, string? SuggestedName, string? SuggestedPhone);

    public sealed record Ask(string? ContactName, string? Phone, string? PreferredTimes);

    internal sealed record Recorded(Guid Id, string ContactName, string Phone) : ICommand<Ok>;

    internal sealed class Handler(IAuditLog audit) : ICommandHandler<Recorded, Ok>
    {
        public async Task<Result<Ok>> Handle(Recorded c, CancellationToken ct)
        {
            await audit.RecordAsync("fiscal.accompaniment-request", "AccompanimentRequest", c.Id, new { c.ContactName, c.Phone }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapGet("/api/fiscal/accompaniment", async (ClaimsPrincipal user, ICloudClient cloud, ISettingsQueries settings, CancellationToken ct) =>
            {
                var phone = (await settings.GetBusinessProfileAsync(ct)).Phone;
                var name = user.FindFirstValue(ClaimTypes.Name);
                var state = await ReceiverCloud.PostAsync<CloudState>(cloud, "/fiscal/v1/accompaniment/status", new { }, ct, NotConnected);
                if (state.IsSuccess) return Results.Ok(new Response(true, state.Value.Request, name, phone));
                return state.Error == NotConnected ? Results.Ok(new Response(false, null, name, phone)) : EndpointExtensions.Problem(state.Error!);
            }).RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");

            app.MapPost("/api/fiscal/accompaniment", async (Ask r, ICloudClient cloud, ICommandHandler<Recorded, Ok> record, CancellationToken ct) =>
            {
                var state = await ReceiverCloud.PostAsync<CloudState>(cloud, "/fiscal/v1/accompaniment",
                    new { ContactName = r.ContactName?.Trim(), Phone = r.Phone?.Trim(), PreferredTimes = r.PreferredTimes?.Trim() }, ct, NotConnected);
                if (state.IsFailure) return EndpointExtensions.Problem(state.Error!);
                if (state.Value.Request is { } sent) await record.Handle(new Recorded(sent.Id, sent.ContactName, sent.Phone), ct);
                return Results.Ok(state.Value.Request);
            }).RequirePermission(Permissions.FiscalManage).WithTags("Fiscal");
        }
    }
}
