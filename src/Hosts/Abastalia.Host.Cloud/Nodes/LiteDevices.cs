using System.Security.Claims;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.Host.Cloud.Accounts;
using Abastalia.Host.Cloud.Quotas;
using Abastalia.SharedKernel;

namespace Abastalia.Host.Cloud.Nodes;

/// <summary>TK-321 (ADR-0013): the selling device of a Lite business, in "Tiendas". The devices live in Abastalia.Host.Lite only; the panel asks it
/// server to server with <c>Lite:ProvisioningKey</c> at <c>Lite:Url</c>, always for the tenant of the panel's token (never one the browser names).
/// TK-347: the host serves every plan, so it decides: a business it does not host gets 404 (the panel then shows nothing).</summary>
internal static class LiteDeviceEndpoints
{
    public const string HttpClientName = "Abastalia.Lite";
    private static readonly Error NotHosted = Error.NotFound("Lite.NotHosted", "Este negocio no vende desde la nube.");

    public static void MapLiteDevices(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/panel/lite").RequireAuthorization(CloudAuth.PanelPolicy).RequireRateLimiting(QuotaRateLimits.PanelUser);
        api.MapGet("/devices", (ClaimsPrincipal user, IConfiguration config, IHttpClientFactory http, CancellationToken ct) =>
            ForwardAsync(HttpMethod.Get, "devices", user, config, http, ct));
        api.MapPost("/device-codes", (ClaimsPrincipal user, IConfiguration config, IHttpClientFactory http, CancellationToken ct) =>
            ForwardAsync(HttpMethod.Post, "device-codes", user, config, http, ct)).RequireAuthorization(CloudAuth.OwnerPolicy);
        api.MapPost("/devices/{deviceId:guid}/revoke", (Guid deviceId, ClaimsPrincipal user, IConfiguration config, IHttpClientFactory http, CancellationToken ct) =>
            ForwardAsync(HttpMethod.Post, $"devices/{deviceId}/revoke", user, config, http, ct)).RequireAuthorization(CloudAuth.OwnerPolicy);
    }

    private static async Task<IResult> ForwardAsync(HttpMethod method, string path, ClaimsPrincipal user, IConfiguration config,
        IHttpClientFactory http, CancellationToken ct)
    {
        var tenant = user.GetTenantId();
        if (string.IsNullOrEmpty(config["Lite:Url"])) return EndpointExtensions.Problem(NotHosted);
        using var request = new HttpRequestMessage(method, $"{config["Lite:Url"]!.TrimEnd('/')}/internal/tenants/{tenant}/{path}");
        request.Headers.Add("X-Provisioning-Key", config["Lite:ProvisioningKey"]);
        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            // The host's own answer (its Spanish message on a refusal), as it is: it is ours.
            return Results.Content(await response.Content.ReadAsStringAsync(ct), response.Content.Headers.ContentType?.ToString(), statusCode: (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Results.Problem(detail: "No pudimos hablar con el servicio del plan Ligero. Intente de nuevo en unos minutos.", statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Lite.Unavailable", extensions: new Dictionary<string, object?> { ["code"] = "Lite.Unavailable" });
        }
    }
}
