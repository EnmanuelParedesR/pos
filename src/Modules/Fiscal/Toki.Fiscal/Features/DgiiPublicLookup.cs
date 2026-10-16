using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Toki.BuildingBlocks;
using Toki.Fiscal.Contracts;
using Toki.SharedKernel;

namespace Toki.Fiscal.Features;

/// <summary>TK-252: reads the two DGII public consultation pages (ASP.NET WebForms; docs/consultas-dgii.md). No CAPTCHA on either; Toki identifies
/// itself in the User-Agent and never pretends to be a browser.</summary>
internal static partial class DgiiPages
{
    public const string Base = "https://dgii.gov.do/app/WebApps/ConsultasWeb2/ConsultasWeb/consultas/";

    /// <summary>The hidden inputs (__VIEWSTATE, __EVENTVALIDATION…) the form posts back.</summary>
    public static Dictionary<string, string> HiddenFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in HiddenInput().Matches(html))
            if (Attribute(input.Value, "name") is { } name) fields[name] = WebUtility.HtmlDecode(Attribute(input.Value, "value") ?? "");
        return fields;
    }

    /// <summary>The result of rnc.aspx; null when the page is not one Toki knows how to read.</summary>
    public static RncLookup? ReadRnc(string html)
    {
        if (DetailsTable().Match(html) is { Success: true } table)
        {
            var f = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match row in Row().Matches(table.Groups[1].Value)) f.TryAdd(Key(Text(row.Groups["label"].Value)), Text(row.Groups["value"].Value));
            if (Rnc.Normalize(f.GetValueOrDefault("cedularnc")) is { } rnc && Rnc.IsValid(rnc) && Blank(f.GetValueOrDefault("nombrerazonsocial")) is { } name
                && Blank(f.GetValueOrDefault("estado")) is { } status)
                return new(DgiiAnswerKind.Found, new DgiiTaxpayer(rnc, name, Blank(f.GetValueOrDefault("nombrecomercial")), status,
                    Blank(f.GetValueOrDefault("categoria")), Blank(f.GetValueOrDefault("regimendepagos")),
                    string.Equals(f.GetValueOrDefault("facturadorelectronico"), "SI", StringComparison.OrdinalIgnoreCase)));
        }
        return Key(Span(html, "cphMain_lblInformacion") ?? "").Contains("noseencuentrainscrito", StringComparison.Ordinal)
            ? new RncLookup(DgiiAnswerKind.NotRegistered)
            : null;
    }

    /// <summary>The result of ncf.aspx; null when the page is not one Toki knows how to read.</summary>
    public static NcfCheck? ReadNcf(string html)
    {
        var info = Key(Span(html, "cphMain_lblInformacion") ?? "");
        if (info.Contains("noescorrecto", StringComparison.Ordinal) || info.Contains("nocorresponde", StringComparison.Ordinal))
            return new NcfCheck(NcfValidity.NotMatching);
        if (Span(html, "cphMain_lblEstado") is not { } status) return null;
        var until = DateOnly.TryParseExact(Span(html, "cphMain_lblVigencia"), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
        var validity = Key(status) switch
        {
            var s when s.StartsWith("vencid", StringComparison.Ordinal) => NcfValidity.Expired,
            var s when s.StartsWith("anulad", StringComparison.Ordinal) => NcfValidity.NotMatching,
            _ when info.Contains("esvalido", StringComparison.Ordinal) => NcfValidity.Valid,
            _ => NcfValidity.Unknown,
        };
        return new NcfCheck(validity, until, Span(html, "cphMain_lblRazonSocial"), Span(html, "cphMain_lblTipoComprobante"));
    }

    private static string? Span(string html, string id) =>
        Regex.Match(html, $"<span[^>]*\\bid=\"{Regex.Escape(id)}\"[^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            is { Success: true } m ? Blank(Text(m.Groups[1].Value)) : null;

    private static string? Attribute(string tag, string name) =>
        Regex.Match(tag, $"\\b{name}=\"([^\"]*)\"", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Visible text: tags out, entities decoded, every run of whitespace one space.</summary>
    private static string Text(string html) => Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(html, " ")), " ").Trim();

    /// <summary>"Régimen de pagos" → "regimendepagos": labels compared without accents, case or punctuation.</summary>
    private static string Key(string label) =>
        new(label.Normalize(NormalizationForm.FormD).Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*>", RegexOptions.IgnoreCase, 1000)] private static partial Regex HiddenInput();
    [GeneratedRegex("id=\"cphMain_dvDatosContribuyentes\"[^>]*>(.*?)</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex DetailsTable();
    [GeneratedRegex("<tr[^>]*>\\s*<td[^>]*>(?<label>.*?)</td>\\s*<td[^>]*>(?<value>.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase, 1000)] private static partial Regex Row();
    [GeneratedRegex("<[^>]+>", RegexOptions.None, 1000)] private static partial Regex Tags();
    [GeneratedRegex("\\s+", RegexOptions.None, 1000)] private static partial Regex Spaces();
}

/// <summary>TK-252: <see cref="IDgiiPublicLookup"/> over the DGII public pages, from the store PC. A good citizen: answers are cached (an RNC 7 days, a
/// valid or expired NCF for good), the whole store asks at most <c>Fiscal:DgiiPublic:PerMinute</c> (30) and <c>PerDay</c> (500) lookups, and after a
/// failure it waits 2 minutes before asking again. Anything short of a clear answer is <see cref="DgiiAnswerKind.Unavailable"/>, never an error.
/// <c>Fiscal:DgiiPublic:Enabled</c> = false turns it off (tests).</summary>
/// <remarks>shortcut: the cache lives in memory, so a restart of the store server asks again; a table would keep it.</remarks>
internal sealed class DgiiPublicSite(IHttpClientFactory http, IConfiguration config, TimeProvider clock, ILogger<DgiiPublicSite> log) : IDgiiPublicLookup
{
    public const string HttpClientName = "dgii-public";
    private static readonly TimeSpan PauseAfterFailure = TimeSpan.FromMinutes(2);
    private static readonly RncLookup Unavailable = new(DgiiAnswerKind.Unavailable);

    private readonly bool _enabled = config.GetValue("Fiscal:DgiiPublic:Enabled", true);
    private readonly int _perMinute = config.GetValue("Fiscal:DgiiPublic:PerMinute", 30);
    private readonly int _perDay = config.GetValue("Fiscal:DgiiPublic:PerDay", 500);
    private readonly ConcurrentDictionary<string, (object Answer, DateTime ExpiresUtc)> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Queue<DateTime> _lastMinute = new();
    private DateOnly _day;
    private int _today;
    private DateTime _pausedUntilUtc;

    public async Task<RncLookup> RncAsync(string rnc, CancellationToken ct)
    {
        var digits = Rnc.Normalize(rnc);
        if (!Rnc.IsValid(digits)) return Unavailable;
        var key = "rnc:" + digits;
        if (Cached<RncLookup>(key) is { } hit) return hit;
        var html = await AskAsync("rnc.aspx", new()
        {
            ["__EVENTTARGET"] = "", ["__EVENTARGUMENT"] = "", ["ctl00$cphMain$txtRNCCedula"] = digits!, ["ctl00$cphMain$btnBuscarPorRNC"] = "BUSCAR",
            ["ctl00$cphMain$txtRazonSocial"] = "", ["ctl00$cphMain$hidActiveTab"] = "",
        }, ct);
        if (html is null) return Unavailable;
        if (DgiiPages.ReadRnc(html) is not { } answer) return Failed("rnc.aspx", "a page it cannot read", Unavailable);
        Remember(key, answer, answer.Kind == DgiiAnswerKind.Found ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1));
        return answer;
    }

    public async Task<NcfCheck> NcfAsync(string issuerRnc, string ncf, CancellationToken ct)
    {
        var rnc = Rnc.Normalize(issuerRnc);
        ncf = ncf.Trim().ToUpperInvariant();
        // An e-NCF is checked on that page only with the security code printed under its QR: Toki does not have it here.
        if (!Rnc.IsValid(rnc) || ncf is not { Length: 11 } || ncf[0] != 'B') return NcfCheck.Unknown;
        var key = $"ncf:{rnc}:{ncf}";
        if (Cached<NcfCheck>(key) is { } hit) return hit;
        var html = await AskAsync("ncf.aspx", new()
        {
            ["ctl00$cphMain$txtRNC"] = rnc!, ["ctl00$cphMain$txtNCF"] = ncf, ["ctl00$cphMain$txtRncComprador"] = "", ["ctl00$cphMain$txtCodigoSeg"] = "",
            ["ctl00$cphMain$btnConsultar"] = "Buscar",
        }, ct);
        if (html is null) return NcfCheck.Unknown;
        if (DgiiPages.ReadNcf(html) is not { } answer) return Failed("ncf.aspx", "a page it cannot read", NcfCheck.Unknown);
        // A valid or expired receipt stays so; "does not match" may be a sequence the DGII is still loading.
        if (answer.Validity != NcfValidity.Unknown)
            Remember(key, answer, answer.Validity == NcfValidity.NotMatching ? TimeSpan.FromHours(1) : TimeSpan.MaxValue);
        return answer;
    }

    /// <summary>GET the form (its ViewState and cookies), POST it back with <paramref name="fields"/>. Null when the store may not ask now or the DGII failed.</summary>
    private async Task<string?> AskAsync(string page, Dictionary<string, string> fields, CancellationToken ct)
    {
        if (!_enabled || !TryTake()) return null;
        try
        {
            var client = http.CreateClient(HttpClientName);
            using var form = await client.GetAsync(DgiiPages.Base + page, ct);
            if (form.StatusCode != HttpStatusCode.OK) return Failed<string?>(page, $"HTTP {(int)form.StatusCode}", null);
            var hidden = DgiiPages.HiddenFields(await form.Content.ReadAsStringAsync(ct));
            if (!hidden.ContainsKey("__VIEWSTATE")) return Failed<string?>(page, "a form without ViewState", null);
            foreach (var (k, v) in fields) hidden[k] = v;
            using var post = new HttpRequestMessage(HttpMethod.Post, DgiiPages.Base + page) { Content = new FormUrlEncodedContent(hidden) };
            // The session and load-balancer cookies of the GET go back with the POST.
            if (form.Headers.TryGetValues("Set-Cookie", out var cookies))
                post.Headers.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
            using var result = await client.SendAsync(post, ct);
            return result.StatusCode == HttpStatusCode.OK ? await result.Content.ReadAsStringAsync(ct) : Failed<string?>(page, $"HTTP {(int)result.StatusCode}", null);
        }
        catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return Failed<string?>(page, e.Message, null);
        }
    }

    /// <summary>Takes one lookup from the store's minute and day allowance; false while paused after a failure or over the limit.</summary>
    private bool TryTake()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            if (now < _pausedUntilUtc) return false;
            var day = BusinessDay.Of(now);
            if (day != _day) (_day, _today) = (day, 0);
            while (_lastMinute.TryPeek(out var t) && t <= now.AddMinutes(-1)) _lastMinute.Dequeue();
            if (_lastMinute.Count >= _perMinute || _today >= _perDay) return false;
            _lastMinute.Enqueue(now);
            _today++;
            return true;
        }
    }

    private T Failed<T>(string page, string why, T result)
    {
        log.LogWarning("DGII public consultation {Page} failed ({Why}); asking again in {Minutes} minutes", page, why, PauseAfterFailure.TotalMinutes);
        lock (_gate) _pausedUntilUtc = clock.GetUtcNow().UtcDateTime + PauseAfterFailure;
        return result;
    }

    private T? Cached<T>(string key) where T : class =>
        _cache.TryGetValue(key, out var e) && e.ExpiresUtc > clock.GetUtcNow().UtcDateTime ? (T)e.Answer : null;

    private void Remember(string key, object answer, TimeSpan lifetime) =>
        _cache[key] = (answer, lifetime == TimeSpan.MaxValue ? DateTime.MaxValue : clock.GetUtcNow().UtcDateTime + lifetime);
}
