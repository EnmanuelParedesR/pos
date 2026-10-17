using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Abastalia.Client.UI.State;
using Abastalia.Escuadra.Components;
using Abastalia.Hardware;

namespace Abastalia.Client.UI.Api;

/// <summary>What the API said when it refused: its ProblemDetails <c>code</c>, the text for the cashier and the field errors.</summary>
public sealed record Problem(int Status, string Code, string Message, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public const string NoServer = "NoServer";
    /// <summary>The server's certificate is not the one pinned when this PC paired (TK-122).</summary>
    public const string UnknownServer = "UnknownServer";

    /// <summary>The permission a supervisor must grant (<c>errors.permission</c> of <c>Approval.Required</c>).</summary>
    public string? PermissionRequired => Code == "Approval.Required" ? Error("permission") : null;

    /// <summary>R11 A1: what the supervisor approves, written by the server (<c>errors.summary</c>); the approval is bound to it.</summary>
    public string ApprovalSummary => Error("summary") ?? "";

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
public sealed class AbastaliaApi(HttpClient http, Session session, ServerAddress server)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The store server runs on this same PC (the main PC): nobody else on the LAN can answer in its place.</summary>
    public bool ServerIsThisPc => server.Current.IsLoopback;

    public Task<ApiResult<T>> GetAsync<T>(string url) => SendAsync<T>(HttpMethod.Get, url, null, DefaultTimeout);
    /// <summary>A periodic check of the status bar: R11 A4, it does not keep an idle session of someone who can approve open.</summary>
    public Task<ApiResult<T>> PollAsync<T>(string url) => SendAsync<T>(HttpMethod.Get, url, null, DefaultTimeout, background: true);
    /// <summary>TK-254: the register's periodic version report; like <see cref="PollAsync{T}"/>, it does not keep an idle session open.</summary>
    public Task<ApiResult<T>> PollPutAsync<T>(string url, object? body) => SendAsync<T>(HttpMethod.Put, url, body, DefaultTimeout, background: true);
    /// <param name="timeout">Longer than the 30 s default only for slow commands (a backup).</param>
    public Task<ApiResult<T>> PostAsync<T>(string url, object? body = null, TimeSpan? timeout = null) => SendAsync<T>(HttpMethod.Post, url, body, timeout ?? DefaultTimeout);
    public Task<ApiResult<T>> PutAsync<T>(string url, object? body) => SendAsync<T>(HttpMethod.Put, url, body, DefaultTimeout);
    public Task<ApiResult<T>> DeleteAsync<T>(string url) => SendAsync<T>(HttpMethod.Delete, url, null, DefaultTimeout);
    /// <summary>TK-343: Confirmar on Talia's card: the proposal's own request with this session, marked with its id (the server runs it once).</summary>
    public Task<ApiResult<Empty>> ProposalAsync(TaliaProposal p, object body) => SendAsync<Empty>(new HttpMethod(p.Method), p.Path, body, DefaultTimeout, proposal: p.Id);

    /// <summary>After an uncertain checkout: the sale when the server saved it, no value and no problem when it did not,
    /// or a problem when it still cannot tell.</summary>
    public async Task<ApiResult<SaleRegistered>> SavedSaleAsync(Guid saleId)
    {
        var detail = await GetAsync<SaleDetail>($"/api/sales/{saleId}");
        if (detail.Problem is { Status: 404 }) return new(default, null);
        if (detail.Value is not { } d) return new(default, detail.Problem);
        var receipt = await GetAsync<Receipt>($"/api/sales/{saleId}/receipt");
        // The change from the receipt: the sale's detail hides it from the cashier while the shift is open (R11 A2).
        return receipt.Value is { } r ? new(new SaleRegistered(d.Id, d.Number, d.ENcf, d.Total, r.Change, r), null) : new(default, receipt.Problem);
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body, TimeSpan timeout, bool background = false, Guid? proposal = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(server.Current, url));
        if (proposal is { } id) request.Headers.Add("X-Talia-Proposal", id.ToString());
        if (session.DeviceToken is { } device) request.Headers.Add("X-Device", device);
        if (session.Token is { } token) request.Headers.Authorization = new("Bearer", token);
        if (background) request.Headers.Add("X-Abastalia-Background", "1");
        // A file upload (the DGII registry) goes as is; everything else as JSON.
        if (body is not null) request.Content = body as HttpContent ?? JsonContent.Create(body, body.GetType(), options: Json);

        HttpResponseMessage r;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            r = await http.SendAsync(request, cts.Token);
        }
        catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
        {
            // TK-122, R8 #9: the TLS handshake failed before anything was sent: not the server this PC paired with (or its certificate changed).
            session.SetOnline(false, unknownServer: true);
            return new(default, new Problem(0, Problem.UnknownServer,
                "El servidor cambió de certificado o no es el de la tienda, así que no se envió nada. Vuelva a unir este equipo o llame al técnico."));
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
                if (typeof(T) == typeof(FileDownload))
                {
                    var name = r.Content.Headers.ContentDisposition?.FileNameStar ?? r.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "reporte";
                    object file = new FileDownload(await r.Content.ReadAsByteArrayAsync(), name, r.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
                    return new((T)file, null);
                }
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
