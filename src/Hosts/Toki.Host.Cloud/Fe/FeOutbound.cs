using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Toki.Host.Cloud.Fe;

/// <summary>DGII tokens of each tenant and environment, obtained with the seed signed by the vault copy of its certificate.</summary>
internal sealed class FeDgiiTokenCache
{
    public ConcurrentDictionary<(Guid Tenant, string Env), (string Token, DateTimeOffset Expires)> Tokens { get; } = new();
}

/// <summary>TK-154: what the cloud sends out for a tenant: its commercial approval (ACECF) to the supplier's approval URL (found in the DGII directory,
/// with the supplier's optional seed authentication) and the copy to the DGII. Tokens are seeds signed in the cloud (SemillaModel is allowed).</summary>
/// <remarks>shortcut: no automatic retry; the store's admin presses "Enviar" again and only the missing leg is retried.
/// The same "post to a taxpayer's receiver" exists in the store for the 31 delivery (TK-153): the cloud references no module code.</remarks>
internal sealed class FeOutbound(IHttpClientFactory http, FeSigner signer, FeDgiiTokenCache cache, TimeProvider clock)
{
    public const string HttpClientName = "Toki.Fe.Outbound";
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(5);

    public static string DgiiBase(string env) => $"https://ecf.dgii.gov.do/{env}";

    /// <summary>The supplier's approval and authentication URLs from the DGII directory; null when it is not an electronic issuer.
    /// CerteCF has no directory: there the approval only goes to the DGII.</summary>
    public async Task<(string? Approval, string? Authentication)?> DirectoryAsync(FeCertificate certificate, string env, string rnc, CancellationToken ct)
    {
        if (env == "certecf") return null;
        using var response = await DgiiAsync(certificate, env, () => new HttpRequestMessage(HttpMethod.Get,
            $"{DgiiBase(env)}/consultadirectorio/api/consultas/obtenerdirectorioporrnc?RNC={rnc}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        var entry = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().FirstOrDefault() : json;
        return entry.ValueKind == JsonValueKind.Object && Str(entry, "urlAceptacion") is { } approval ? (approval, Str(entry, "urlOpcional")) : null;
    }

    /// <summary>The DGII's copy of the approval. Null when it was taken; otherwise the DGII's message.</summary>
    public async Task<string?> SendApprovalToDgiiAsync(FeCertificate certificate, string env, string fileName, string xml, CancellationToken ct)
    {
        using var response = await DgiiAsync(certificate, env, () => Upload($"{DgiiBase(env)}/aprobacioncomercial/api/aprobacioncomercial", fileName, xml), ct);
        return response.IsSuccessStatusCode ? null : $"La DGII respondió {(int)response.StatusCode}: {Cut(await response.Content.ReadAsStringAsync(ct))}";
    }

    /// <summary>Posts to another taxpayer's receiver service, authenticating first when it publishes the authentication URL. Throws when it refuses.</summary>
    public async Task PostToTaxpayerAsync(FeCertificate certificate, string url, string? authentication, string service, string fileName, string xml, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        using var request = Upload(ServiceUrl(url, service), fileName, xml);
        if (!string.IsNullOrWhiteSpace(authentication))
        {
            using var seed = await client.GetAsync(ServiceUrl(authentication, "autenticacion/api/semilla"), ct);
            seed.EnsureSuccessStatusCode();
            var signed = await SignSeedAsync(certificate, await seed.Content.ReadAsStringAsync(ct), ct);
            using var validate = await client.SendAsync(Upload(ServiceUrl(authentication, "autenticacion/api/validacioncertificado"), "semilla.xml", signed), ct);
            validate.EnsureSuccessStatusCode();
            var token = Str(JsonDocument.Parse(await validate.Content.ReadAsStringAsync(ct)).RootElement, "token")
                ?? throw new HttpRequestException("El receptor no devolvió el token.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"El receptor respondió {(int)response.StatusCode}: {Cut(await response.Content.ReadAsStringAsync(ct))}");
    }

    /// <summary>The directory may list a base URL or the whole service URL: either way the service goes after <c>/fe/</c>. (verificar) §11, question 31.</summary>
    public static string ServiceUrl(string url, string service)
    {
        url = url.Trim().TrimEnd('/');
        var fe = url.IndexOf("/fe/", StringComparison.OrdinalIgnoreCase);
        return (fe >= 0 ? url[..fe] : url.EndsWith("/fe", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url) + "/fe/" + service;
    }

    private async Task<HttpResponseMessage> DgiiAsync(FeCertificate certificate, string env, Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        for (var attempt = 1; ; attempt++)
        {
            using var request = build();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await DgiiTokenAsync(certificate, env, ct));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await client.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 1) return response;
            response.Dispose();
            cache.Tokens.TryRemove((certificate.TenantId, env), out _);
        }
    }

    private async Task<string> DgiiTokenAsync(FeCertificate certificate, string env, CancellationToken ct)
    {
        var key = (certificate.TenantId, env);
        if (cache.Tokens.TryGetValue(key, out var cached) && clock.GetUtcNow() < cached.Expires - RenewBefore) return cached.Token;
        var client = http.CreateClient(HttpClientName);
        var auth = $"{DgiiBase(env)}/autenticacion/api/autenticacion";
        using var seed = await client.GetAsync($"{auth}/semilla", ct);
        seed.EnsureSuccessStatusCode();
        var signed = await SignSeedAsync(certificate, await seed.Content.ReadAsStringAsync(ct), ct);
        using var response = await client.SendAsync(Upload($"{auth}/validarsemilla", "semilla.xml", signed), ct);
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        var token = Str(json, "token") ?? throw new HttpRequestException("La DGII no devolvió el token.");
        var expires = DateTimeOffset.TryParse(Str(json, "expira"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var e) ? e : clock.GetUtcNow().AddHours(1);
        cache.Tokens[key] = (token, expires);
        return token;
    }

    private async Task<string> SignSeedAsync(FeCertificate certificate, string seedXml, CancellationToken ct)
    {
        var seed = FeXml.Parse(seedXml) is { DocumentElement.Name: "SemillaModel" } parsed
            ? XDocument.Parse(parsed.OuterXml)
            : throw new HttpRequestException("La semilla no es un SemillaModel.");
        seed.Declaration = null;
        return await signer.SignAsync(certificate, seed, null, keepXml: false, ct) ?? throw new InvalidOperationException("Consent withdrawn.");
    }

    private static HttpRequestMessage Upload(string url, string fileName, string xml)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(xml));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = new MultipartFormDataContent { { file, "xml", fileName } } };
    }

    private static string? Str(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Cut(string s) => s.Length > 300 ? s[..300] : s;
}
