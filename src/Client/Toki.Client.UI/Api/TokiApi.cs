using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Toki.Client.UI.State;
using Toki.Escuadra.Components;

namespace Toki.Client.UI.Api;

/// <summary>What the API said when it refused: its ProblemDetails <c>code</c>, the text for the cashier and the field errors.</summary>
public sealed record Problem(int Status, string Code, string Message, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public const string NoServer = "NoServer";

    /// <summary>The permission a supervisor must grant (<c>errors.permission</c> of <c>Approval.Required</c>).</summary>
    public string? PermissionRequired => Code == "Approval.Required" ? Error("permission") : null;

    /// <summary>No answer or a server failure: the command may have been saved anyway (timeout after the commit).</summary>
    public bool IsUncertain => Code == NoServer || Status >= 500;

    /// <summary>No connection is amber, never red (principle 3); everything else the API refused is an error.</summary>
    public Notice.Kinds Kind => Code == NoServer ? Notice.Kinds.Warning : Notice.Kinds.Error;

    public string? Error(string field) => Errors?.GetValueOrDefault(field)?.FirstOrDefault();
}

public readonly record struct ApiResult<T>(T? Value, Problem? Problem)
{
    public bool Ok => Problem is null;
}

/// <summary>Typed calls to the store's local API. Every request carries this PC's device token and, once someone logged in, their session.</summary>
public sealed class TokiApi(HttpClient http, Session session)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public Task<ApiResult<T>> GetAsync<T>(string url) => SendAsync<T>(HttpMethod.Get, url, null, DefaultTimeout);
    /// <param name="timeout">Longer than the 30 s default only for slow commands (a backup).</param>
    public Task<ApiResult<T>> PostAsync<T>(string url, object? body = null, TimeSpan? timeout = null) => SendAsync<T>(HttpMethod.Post, url, body, timeout ?? DefaultTimeout);
    public Task<ApiResult<T>> PutAsync<T>(string url, object? body) => SendAsync<T>(HttpMethod.Put, url, body, DefaultTimeout);
    public Task<ApiResult<T>> DeleteAsync<T>(string url) => SendAsync<T>(HttpMethod.Delete, url, null, DefaultTimeout);

    /// <summary>After an uncertain checkout: the sale when the server saved it, no value and no problem when it did not,
    /// or a problem when it still cannot tell.</summary>
    public async Task<ApiResult<SaleRegistered>> SavedSaleAsync(Guid saleId)
    {
        var detail = await GetAsync<SaleDetail>($"/api/sales/{saleId}");
        if (detail.Problem is { Status: 404 }) return new(default, null);
        if (detail.Value is not { } d) return new(default, detail.Problem);
        var receipt = await GetAsync<Receipt>($"/api/sales/{saleId}/receipt");
        return receipt.Value is { } r ? new(new SaleRegistered(d.Id, d.Number, d.ENcf, d.Total, d.Change, r), null) : new(default, receipt.Problem);
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, TimeSpan timeout)
    {
        using var request = new HttpRequestMessage(method, url);
        if (session.DeviceToken is { } device) request.Headers.Add("X-Device", device);
        if (session.Token is { } token) request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        HttpResponseMessage r;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            r = await http.SendAsync(request, cts.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            session.SetOnline(false);
            return new(default, new Problem(0, Problem.NoServer, "No hay conexión con el servidor de la tienda. Revise que la PC principal esté encendida."));
        }
        session.SetOnline(true);

        using (r)
        {
            if (r.IsSuccessStatusCode)
            {
                if (r.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(Empty) || r.Content.Headers.ContentLength == 0) return new(default, null);
                return new(await r.Content.ReadFromJsonAsync<T>(Json), null);
            }

            var problem = await ReadProblemAsync(r);
            // A session that expired (12 h) or was closed elsewhere: back to the PIN screen. A wrong PIN has its own code.
            if (r.StatusCode == HttpStatusCode.Unauthorized && problem.Code == "" && session.Token is not null) session.Expire();
            return new(default, problem);
        }
    }

    private static async Task<Problem> ReadProblemAsync(HttpResponseMessage r)
    {
        var status = (int)r.StatusCode;
        try
        {
            var p = await r.Content.ReadFromJsonAsync<JsonElement>(Json);
            var code = p.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var detail = p.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var errors = p.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object ? e.Deserialize<Dictionary<string, string[]>>(Json) : null;
            return new(status, code, detail ?? errors?.Values.FirstOrDefault()?.FirstOrDefault() ?? Generic(status), errors);
        }
        catch (JsonException)
        {
            return new(status, "", Generic(status));
        }
    }

    private static string Generic(int status) => status switch
    {
        401 => "Su sesión terminó. Entre de nuevo con su PIN.",
        403 => "Su usuario no tiene permiso para esto.",
        404 => "No se encontró.",
        _ => "El servidor no pudo completar la operación. Intente de nuevo.",
    };
}

/// <summary>For calls that answer 204.</summary>
public sealed record Empty;
