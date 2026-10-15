using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Toki.Client.UI.State;

namespace Toki.Client.UI.Api;

/// <summary>What the API said when it refused: its ProblemDetails <c>code</c>, the text for the cashier and the field errors.</summary>
public sealed record Problem(int Status, string Code, string Message, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public const string NoServer = "NoServer";

    /// <summary>The permission a supervisor must grant, from "...supervisor (sales.void)." (the API has no field for it).</summary>
    public string? PermissionRequired => Code == "Approval.Required" && Message.LastIndexOf('(') is var i and >= 0 && Message.IndexOf(')', i) is var j and > 0
        ? Message[(i + 1)..j]
        : null;

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

    public Task<ApiResult<T>> GetAsync<T>(string url) => SendAsync<T>(HttpMethod.Get, url, null);
    public Task<ApiResult<T>> PostAsync<T>(string url, object? body = null) => SendAsync<T>(HttpMethod.Post, url, body);
    public Task<ApiResult<T>> PutAsync<T>(string url, object? body) => SendAsync<T>(HttpMethod.Put, url, body);
    public Task<ApiResult<T>> DeleteAsync<T>(string url) => SendAsync<T>(HttpMethod.Delete, url, null);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body)
    {
        using var request = new HttpRequestMessage(method, url);
        if (session.DeviceToken is { } device) request.Headers.Add("X-Device", device);
        if (session.Token is { } token) request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        HttpResponseMessage r;
        try
        {
            r = await http.SendAsync(request);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new(default, new Problem(0, Problem.NoServer, "No hay conexión con el servidor de la tienda. Revise que la PC principal esté encendida."));
        }

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
            var errors = p.TryGetProperty("errors", out var e) ? e.Deserialize<Dictionary<string, string[]>>(Json) : null;
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
