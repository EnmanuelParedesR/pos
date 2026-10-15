using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Toki.Fiscal.Domain;

namespace Toki.Fiscal;

/// <summary>DGII result code (Descripción Técnica Servicios DGII). <c>Simulated</c> is Toki's: nothing was sent.</summary>
internal enum DgiiState { NotFound = 0, Accepted = 1, Rejected = 2, InProcess = 3, AcceptedConditional = 4, Simulated = 99 }

/// <param name="Messages">The DGII messages joined in one line ("código: valor"), as shown to the owner.</param>
/// <param name="SequenceUsed">With a rejection: false means the e-NCF was not consumed.</param>
internal sealed record DgiiAnswer(DgiiState State, string? TrackId = null, bool? SequenceUsed = null, string? Messages = null);

/// <summary>Who receives the signed documents. Throws on a transient failure (network, timeout, 5xx, 429): the queue retries.
/// A DGII refusal comes back as an answer with <see cref="DgiiState.Rejected"/>.</summary>
internal interface IEcfProvider
{
    /// <summary>True for the stand-in: documents are marked Simulated and never leave the store.</summary>
    bool IsSimulated { get; }
    FiscalEnvironment Environment { get; }
    /// <summary>The whole e-CF to recepción: answers <see cref="DgiiState.InProcess"/> with a TrackId.</summary>
    Task<DgiiAnswer> SendEcfAsync(string issuerRnc, string encf, string signedXml, CancellationToken ct);
    Task<DgiiAnswer> GetResultAsync(string trackId, CancellationToken ct);
    /// <summary>The RFCE of a 32 under RD$250,000: the final answer comes right away.</summary>
    Task<DgiiAnswer> SendRfceAsync(string issuerRnc, string encf, string signedRfce, CancellationToken ct);
    /// <summary>After a send that may or may not have arrived (timeout): what the DGII already has for this e-NCF, or null if nothing.</summary>
    Task<DgiiAnswer?> FindAsync(string issuerRnc, string encf, string securityCode, EcfChannel channel, CancellationToken ct);
    /// <summary>An ANECF (e-NCF void).</summary>
    Task<DgiiAnswer> VoidAsync(string issuerRnc, string signedAnecf, CancellationToken ct);
}

/// <summary>Stand-in while the store is not certified (<c>Fiscal:Provider</c> = <c>Simulated</c>). Nothing reaches the DGII, so nothing is accepted.</summary>
internal sealed class SimulatedEcfProvider : IEcfProvider
{
    private static readonly Task<DgiiAnswer> Simulated = Task.FromResult(new DgiiAnswer(DgiiState.Simulated));

    public bool IsSimulated => true;
    public FiscalEnvironment Environment => FiscalEnvironment.Simulated;
    public Task<DgiiAnswer> SendEcfAsync(string issuerRnc, string encf, string signedXml, CancellationToken ct) => Simulated;
    public Task<DgiiAnswer> GetResultAsync(string trackId, CancellationToken ct) => Simulated;
    public Task<DgiiAnswer> SendRfceAsync(string issuerRnc, string encf, string signedRfce, CancellationToken ct) => Simulated;
    public Task<DgiiAnswer?> FindAsync(string issuerRnc, string encf, string securityCode, EcfChannel channel, CancellationToken ct) => Task.FromResult<DgiiAnswer?>(null);
    public Task<DgiiAnswer> VoidAsync(string issuerRnc, string signedAnecf, CancellationToken ct) => Simulated;
}

/// <summary>The DGII web services URLs of one environment (docs/ecf-propio.md §2.2).</summary>
internal sealed record DgiiUrls(FiscalEnvironment Environment)
{
    public string Path => Environment switch
    {
        FiscalEnvironment.TesteCF => "testecf",
        FiscalEnvironment.CerteCF => "certecf",
        FiscalEnvironment.eCF => "ecf",
        _ => throw new InvalidOperationException("The simulated environment has no DGII URLs."),
    };

    public string Ecf => $"https://ecf.dgii.gov.do/{Path}";
    public string Fc => $"https://fc.dgii.gov.do/{Path}";

    /// <summary>The DGII does not publish these in CerteCF (TrackIds by e-NCF, voids, directory).</summary>
    public bool HasQueries => Environment != FiscalEnvironment.CerteCF;
}

/// <summary>TK-144: Toki's own client of the DGII web services. Authenticates with the seed signed by the store's certificate and keeps the
/// token until 5 minutes before its <c>expira</c> (or a 401). Uploads are <c>multipart/form-data</c> with the field <c>xml</c> named
/// <c>RNC + e-NCF + .xml</c>. No retries of its own: the queue retries. Never logs the token or the XML.</summary>
internal sealed class DgiiClient(IHttpClientFactory http, IEcfSigner signer, DgiiUrls urls, TimeProvider clock) : IEcfProvider, IDisposable
{
    public const string HttpClientName = "Toki.Dgii";
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private (string Token, DateTimeOffset Expires)? _token;

    public bool IsSimulated => false;

    public void Dispose() => _tokenLock.Dispose();
    public FiscalEnvironment Environment => urls.Environment;

    /// <summary><c>Fiscal:Dgii:Environment</c> = <c>TesteCF</c> (default) | <c>CerteCF</c> | <c>eCF</c>.</summary>
    public static DgiiUrls UrlsFrom(IConfiguration config) =>
        Enum.TryParse<FiscalEnvironment>(config["Fiscal:Dgii:Environment"] ?? "TesteCF", out var env) && env != FiscalEnvironment.Simulated
            ? new DgiiUrls(env)
            : throw new InvalidOperationException($"Unknown Fiscal:Dgii:Environment '{config["Fiscal:Dgii:Environment"]}': use TesteCF, CerteCF or eCF.");

    public async Task<DgiiAnswer> SendEcfAsync(string issuerRnc, string encf, string signedXml, CancellationToken ct)
    {
        using var response = await SendAsync(() => Upload($"{urls.Ecf}/recepcion/api/facturaselectronicas", $"{issuerRnc}{encf}.xml", signedXml), ct);
        var json = await JsonAsync(response, ct);
        var trackId = Str(json, "trackId");
        return string.IsNullOrEmpty(trackId)
            ? new DgiiAnswer(DgiiState.Rejected, Messages: Join(Str(json, "error"), Str(json, "mensaje")) ?? $"La DGII respondió {(int)response.StatusCode} sin TrackId.")
            : new DgiiAnswer(DgiiState.InProcess, trackId);
    }

    public async Task<DgiiAnswer> GetResultAsync(string trackId, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{urls.Ecf}/consultaresultado/api/consultas/estado?trackid={Uri.EscapeDataString(trackId)}"), ct);
        return Answer(await JsonAsync(response, ct), trackId);
    }

    public async Task<DgiiAnswer> SendRfceAsync(string issuerRnc, string encf, string signedRfce, CancellationToken ct)
    {
        using var response = await SendAsync(() => Upload($"{urls.Fc}/recepcionfc/api/recepcion/ecf", $"{issuerRnc}{encf}.xml", signedRfce), ct);
        return Answer(await JsonAsync(response, ct), null);
    }

    public async Task<DgiiAnswer?> FindAsync(string issuerRnc, string encf, string securityCode, EcfChannel channel, CancellationToken ct)
    {
        if (channel == EcfChannel.Rfce)
        {
            using var rfce = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{urls.Fc}/consultarfce/api/Consultas/Consulta" +
                $"?RNC_Emisor={issuerRnc}&ENCF={encf}&Cod_Seguridad_eCF={Uri.EscapeDataString(securityCode)}"), ct);
            var answer = Answer(await JsonAsync(rfce, ct), null);
            return answer.State == DgiiState.NotFound ? null : answer;
        }
        // shortcut: CerteCF does not publish consultatrackids; there a timeout is resent (the DGII gives a second TrackId).
        if (!urls.HasQueries) return null;
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{urls.Ecf}/consultatrackids/api/trackids/consulta?rncemisor={issuerRnc}&encf={encf}"), ct);
        var json = await JsonAsync(response, ct);
        var last = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().Select(e => Str(e, "trackId")).LastOrDefault(t => !string.IsNullOrEmpty(t)) : null;
        return last is null ? null : new DgiiAnswer(DgiiState.InProcess, last);
    }

    public async Task<DgiiAnswer> VoidAsync(string issuerRnc, string signedAnecf, CancellationToken ct)
    {
        if (!urls.HasQueries) return new DgiiAnswer(DgiiState.Rejected, Messages: "La DGII no publica la anulación de e-NCF en CerteCF.");
        var name = $"{issuerRnc}ANECF{clock.GetUtcNow():yyyyMMddHHmmss}.xml"; // (verificar) the DGII names no file for the ANECF
        using var response = await SendAsync(() => Upload($"{urls.Ecf}/anulacionrangos/api/operaciones/anularrango", name, signedAnecf), ct);
        var json = await JsonAsync(response, ct);
        // (verificar) Response shape: like the others, a code and messages; an HTTP 200 without a code counts as accepted.
        return response.IsSuccessStatusCode && (Int(json, "codigo") is null or 1)
            ? new DgiiAnswer(DgiiState.Accepted, Messages: Messages(json))
            : new DgiiAnswer(DgiiState.Rejected, Messages: Messages(json) ?? $"La DGII respondió {(int)response.StatusCode}.");
    }

    /// <summary>The electronic issuers directory entry of an RNC (for TK-153): its reception, approval and authentication URLs.</summary>
    public async Task<(string? Reception, string? Approval, string? Authentication)?> DirectoryAsync(string rnc, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{urls.Ecf}/consultadirectorio/api/consultas/obtenerdirectorioporrnc?RNC={rnc}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var json = await JsonAsync(response, ct);
        var entry = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().FirstOrDefault() : json;
        return entry.ValueKind == JsonValueKind.Object ? (Str(entry, "urlRecepcion"), Str(entry, "urlAceptacion"), Str(entry, "urlOpcional")) : null;
    }

    /// <summary>The DGII's copy of a signed commercial approval (ACECF) the store sent to an issuer (for TK-154).</summary>
    public async Task<DgiiAnswer> SendCommercialApprovalAsync(string buyerRnc, string encf, string signedAcecf, CancellationToken ct)
    {
        using var response = await SendAsync(() => Upload($"{urls.Ecf}/aprobacioncomercial/api/aprobacioncomercial", $"{buyerRnc}{encf}.xml", signedAcecf), ct);
        var json = await JsonAsync(response, ct);
        return new DgiiAnswer(response.IsSuccessStatusCode ? DgiiState.Accepted : DgiiState.Rejected, Messages: Messages(json));
    }

    /// <summary>Sends with the cached token; a 401 drops the token and tries once more with a new one.
    /// 5xx and 429 throw (the queue retries); other answers are returned for the caller to read.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        for (var attempt = 1; ; attempt++)
        {
            var token = await TokenAsync(ct);
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
            {
                response.Dispose();
                _token = null;
                continue;
            }
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"La DGII respondió {status}.", null, (HttpStatusCode)status);
            }
            return response;
        }
    }

    /// <summary>Seed → signed seed → token (Bearer). Renewed <see cref="RenewBefore"/> before it expires.</summary>
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is { } cached && clock.GetUtcNow() < cached.Expires - RenewBefore) return cached.Token;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is { } again && clock.GetUtcNow() < again.Expires - RenewBefore) return again.Token;
            var client = http.CreateClient(HttpClientName);
            var auth = $"{urls.Ecf}/autenticacion/api/autenticacion";
            using var seedResponse = await client.GetAsync($"{auth}/semilla", ct);
            EnsureAuthentication(seedResponse);
            var seed = XDocument.Parse(await seedResponse.Content.ReadAsStringAsync(ct));
            seed.Declaration = null;
            var (signedSeed, _) = signer.Sign(seed);

            using var request = Upload($"{auth}/validarsemilla", "semilla.xml", signedSeed);
            using var tokenResponse = await client.SendAsync(request, ct);
            EnsureAuthentication(tokenResponse);
            var json = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
            var token = Str(json, "token") ?? throw new HttpRequestException("La DGII no devolvió el token.");
            var expires = DateTimeOffset.TryParse(Str(json, "expira"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e)
                ? e : clock.GetUtcNow().AddHours(1);
            _token = (token, expires);
            return token;
        }
        finally { _tokenLock.Release(); }
    }

    private static void EnsureAuthentication(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"La autenticación con la DGII falló ({(int)response.StatusCode}): revise el certificado y la delegación del firmante.",
                null, response.StatusCode);
    }

    private static HttpRequestMessage Upload(string url, string fileName, string xml)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(xml));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = new MultipartFormDataContent { { file, "xml", fileName } } };
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try { return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    /// <summary>Result of consultaresultado, recepcionfc and consultarfce: <c>codigo</c> (or the <c>estado</c> text), messages and <c>secuenciaUtilizada</c>.</summary>
    private static DgiiAnswer Answer(JsonElement json, string? trackId)
    {
        var state = Int(json, "codigo") is { } code && Enum.IsDefined((DgiiState)code)
            ? (DgiiState)code
            : Str(json, "estado")?.ToLowerInvariant() switch
            {
                "aceptado" => DgiiState.Accepted,
                "rechazado" => DgiiState.Rejected,
                "aceptado condicional" => DgiiState.AcceptedConditional,
                "en proceso" => DgiiState.InProcess,
                _ => DgiiState.NotFound,
            };
        bool? used = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("secuenciaUtilizada", out var u) && u.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? u.GetBoolean() : null;
        return new DgiiAnswer(state, Str(json, "trackId") ?? trackId, used, Messages(json));
    }

    /// <summary><c>mensajes</c> / <c>mensaje</c>: strings or <c>{ valor, codigo }</c>, joined in one line.</summary>
    private static string? Messages(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) return null;
        var parts = new List<string>();
        foreach (var name in new[] { "mensajes", "mensaje" })
        {
            if (!json.TryGetProperty(name, out var m)) continue;
            var items = m.ValueKind == JsonValueKind.Array ? m.EnumerateArray().ToList() : [m];
            parts.AddRange(items.Select(i => i.ValueKind switch
            {
                JsonValueKind.String => i.GetString(),
                JsonValueKind.Object => Join(Str(i, "codigo"), Str(i, "valor")),
                _ => null,
            }).Where(s => !string.IsNullOrWhiteSpace(s))!);
        }
        var text = string.Join(" | ", parts);
        return text.Length == 0 ? null : text.Length > 2000 ? text[..2000] : text;
    }

    private static string? Join(string? a, string? b) => (a, b) switch
    {
        ({ Length: > 0 }, { Length: > 0 }) => $"{a}: {b}",
        ({ Length: > 0 }, _) => a,
        (_, { Length: > 0 }) => b,
        _ => null,
    };

    private static string? Str(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static int? Int(JsonElement json, string name) =>
        int.TryParse(Str(json, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}
