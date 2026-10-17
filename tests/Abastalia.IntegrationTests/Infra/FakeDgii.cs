using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;

namespace Abastalia.IntegrationTests.Infra;

/// <summary>The DGII web services as docs/ecf-propio.md §2 describes them (no TesteCF access yet, TK-141), in memory: the primary handler of the
/// DGII HttpClient. Seed → signed seed → token with <c>expira</c>; recepción answers a TrackId and consultaresultado the result; recepcionfc
/// answers right away; consultatrackids and consultarfce find what arrived; anularrango takes an ANECF. Every request is recorded.</summary>
public sealed class FakeDgii : HttpMessageHandler
{
    public List<Received> Requests { get; } = [];

    /// <summary>The clock tokens expire by (the app's).</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Tokens issued so far; a request with a token not in here gets 401.</summary>
    public List<string> Tokens { get; } = [];

    /// <summary>The DGII is unreachable: every request throws, as a network failure.</summary>
    public bool Down { get; set; }

    /// <summary>The next reception arrives but the answer is lost (the client times out).</summary>
    public bool LoseNextAnswer { get; set; }

    /// <summary>"En proceso" answers before the final one, per TrackId.</summary>
    public int PollsInProcess { get; set; } = 1;

    /// <summary>The final answer of a full e-CF and of an RFCE (code 1 accepted, 2 rejected, 4 accepted with conditions), by e-NCF.</summary>
    public Func<string, (int Code, bool SequenceUsed, string? Message)> Result { get; set; } = _ => (1, true, null);

    /// <summary>TK-153: the electronic issuers directory, RNC → base URL of its receiver services.</summary>
    public Dictionary<string, string> Directory { get; } = [];

    private readonly Dictionary<string, (string Encf, int Polls)> _tracks = [];
    private int _lastTrack;

    public IEnumerable<Received> To(string pathEnd)
    {
        lock (Requests) return Requests.Where(r => r.Path.EndsWith(pathEnd, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>TK-156: only the e-CF services fail (as a network failure); the DGII's status host still answers (its 401 without an API key).</summary>
    public bool EcfDown { get; set; }

    /// <summary>TK-156: how far the app's clock is ahead of the DGII's (negative: behind). Null: answers carry no <c>Date</c> header.</summary>
    public TimeSpan? AppAheadBy { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down) throw new HttpRequestException("No such host is known. (ecf.dgii.gov.do:443)");
        HttpResponseMessage response;
        if (request.RequestUri!.Host == "statusecf.dgii.gov.do")
        {
            lock (Requests) Requests.Add(new Received(request.RequestUri.Host, request.RequestUri.AbsolutePath, request.RequestUri.Query, null, "", null, null));
            response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        else if (EcfDown) throw new HttpRequestException("Connection timed out. (ecf.dgii.gov.do:443)");
        else response = await RespondAsync(request, cancellationToken);
        if (AppAheadBy is { } ahead) response.Headers.Date = Now() - ahead;
        return response;
    }

    private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down) throw new HttpRequestException("No such host is known. (ecf.dgii.gov.do:443)");
        var uri = request.RequestUri!;
        var (field, file, xml) = (string.Empty, (string?)null, (string?)null);
        if (request.Content is MultipartFormDataContent multipart)
            foreach (var part in multipart)
                (field, file, xml) = (part.Headers.ContentDisposition!.Name!.Trim('"'), part.Headers.ContentDisposition.FileName?.Trim('"'),
                    await part.ReadAsStringAsync(cancellationToken));
        var received = new Received(uri.Host, uri.AbsolutePath, uri.Query, request.Headers.Authorization?.ToString(), field, file, xml);
        lock (Requests) Requests.Add(received);
        var path = uri.AbsolutePath.ToLowerInvariant();

        if (path.EndsWith("/autenticacion/semilla", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"<SemillaModel><valor>{Guid.NewGuid():N}</valor><fecha>{Now():yyyy-MM-ddTHH:mm:ss}</fecha></SemillaModel>"),
            };
        if (path.EndsWith("/autenticacion/validarsemilla", StringComparison.Ordinal))
        {
            var seed = XDocument.Parse(xml!);
            if (seed.Root!.Name != "SemillaModel" || !seed.Root.Elements().Any(e => e.Name.LocalName == "Signature"))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            var token = $"token-{Tokens.Count + 1}";
            lock (Tokens) Tokens.Add(token);
            var now = Now();
            return Json(new { token, expira = (now + TokenLifetime).ToString("yyyy-MM-ddTHH:mm:ssZ"), expedido = now.ToString("yyyy-MM-ddTHH:mm:ssZ") });
        }
        if (received.Authorization is not { } auth || !Tokens.Contains(auth["Bearer ".Length..])) return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        string Query(string name) => System.Web.HttpUtility.ParseQueryString(uri.Query)[name] ?? "";
        string EncfOf(string x) => XDocument.Parse(x).Descendants("eNCF").Single().Value;

        if (path.EndsWith("/recepcion/api/facturaselectronicas", StringComparison.Ordinal))
        {
            var track = $"track-{++_lastTrack}";
            _tracks[track] = (EncfOf(xml!), 0);
            if (LoseNextAnswer)
            {
                LoseNextAnswer = false;
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.");
            }
            return Json(new { trackId = track, error = (string?)null, mensaje = (string?)null });
        }
        if (path.EndsWith("/consultaresultado/api/consultas/estado", StringComparison.Ordinal))
        {
            var id = Query("trackid");
            if (!_tracks.TryGetValue(id, out var t)) return Json(new { trackId = id, codigo = 0, estado = "No encontrado", mensajes = Array.Empty<object>() });
            _tracks[id] = (t.Encf, t.Polls + 1);
            if (t.Polls < PollsInProcess) return Json(new { trackId = id, codigo = 3, estado = "En Proceso", mensajes = Array.Empty<object>() });
            return Final(t.Encf, id);
        }
        if (path.EndsWith("/consultatrackids/api/trackids/consulta", StringComparison.Ordinal))
            return Json(_tracks.Where(t => t.Value.Encf == Query("encf")).Select(t => new { trackId = t.Key, estado = "Aceptado", fechaRecepcion = Now() }).ToList());
        if (path.EndsWith("/recepcionfc/api/recepcion/ecf", StringComparison.Ordinal)) return Final(EncfOf(xml!), null);
        if (path.EndsWith("/consultarfce/api/consultas/consulta", StringComparison.Ordinal))
            return To("/recepcionfc/api/recepcion/ecf").Any(r => EncfOf(r.Xml!) == Query("ENCF")) ? Final(Query("ENCF"), null) : Json(new { codigo = 0, estado = "No encontrado" });
        if (path.EndsWith("/anulacionrangos/api/operaciones/anularrango", StringComparison.Ordinal))
            return Json(new { rnc = XDocument.Parse(xml!).Descendants("RncEmisor").Single().Value, codigo = 1, nombre = "Aceptado", mensajes = Array.Empty<string>() });
        if (path.EndsWith("/aprobacioncomercial/api/aprobacioncomercial", StringComparison.Ordinal))
            return Json(new { mensaje = Array.Empty<string>(), estado = "Recibido", codigo = 1 }); // TK-246: the store's copy of an ACECF
        if (path.EndsWith("/consultadirectorio/api/consultas/obtenerdirectorioporrnc", StringComparison.Ordinal))
            return Directory.TryGetValue(Query("RNC"), out var receiver)
                ? Json(new[] { new { nombre = "Comprador", rnc = Query("RNC"), urlRecepcion = receiver + "/fe/recepcion/api/ecf", urlAceptacion = receiver + "/fe/aprobacioncomercial/api/ecf", urlOpcional = receiver + "/fe/autenticacion" } })
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Final(string encf, string? trackId)
    {
        var (code, used, message) = Result(encf);
        var state = code switch { 1 => "Aceptado", 2 => "Rechazado", 4 => "Aceptado Condicional", _ => "No encontrado" };
        return Json(new
        {
            trackId, codigo = code, estado = state, encf, secuenciaUtilizada = used,
            mensajes = message is null ? Array.Empty<object>() : [new { valor = message, codigo = 145 }],
        });
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    public sealed record Received(string Host, string Path, string Query, string? Authorization, string Field, string? FileName, string? Xml);
}
