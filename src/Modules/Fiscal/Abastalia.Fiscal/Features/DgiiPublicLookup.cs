using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Abastalia.BuildingBlocks;
using Abastalia.Fiscal.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Fiscal.Features;

/// <summary>TK-252: reads the two DGII public consultation pages (ASP.NET WebForms; docs/consultas-dgii.md). No CAPTCHA on either; Abastalia identifies
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

    /// <summary>The result of rnc.aspx; null when the page is not one Abastalia knows how to read.</summary>
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

    /// <summary>The result of ncf.aspx; null when the page is not one Abastalia knows how to read.</summary>
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

/// <summary>TK-252: a cached DGII answer (the JSON of an <see cref="RncLookup"/> or <see cref="NcfCheck"/>), or the store's lookup count of a
/// business day (key <c>day:yyyy-MM-dd</c>), so a restart neither asks again nor resets the day's allowance.</summary>
internal sealed class DgiiPublicAnswer
{
    public string Key { get; set; } = "";
    public string Answer { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
}

/// <summary>TK-252: <see cref="IDgiiPublicLookup"/> over the DGII public pages, from the store PC. A good citizen: answers are cached in
/// <c>fiscal.DgiiPublicAnswers</c> (an RNC 7 days, a valid or expired NCF for good), the whole store asks at most <c>Fiscal:DgiiPublic:PerMinute</c> (30)
/// and <c>PerDay</c> (500) lookups, and after a failure it waits 2 minutes before asking again. Anything short of a clear answer is
/// <see cref="DgiiAnswerKind.Unavailable"/>, never an error. <c>Fiscal:DgiiPublic:Enabled</c> = false turns it off (tests).</summary>
/// <remarks>The table is written behind, in order, on its own connection: a lookup runs inside a command's transaction (a purchase), which must
/// neither wait for nor roll back the cache. Memory answers first; the table answers after a restart. The minute window and the pause after a
/// failure stay in memory (a restart costs at most one more minute's worth).</remarks>
internal sealed class DgiiPublicSite(IHttpClientFactory http, IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, ILogger<DgiiPublicSite> log)
    : IDgiiPublicLookup, IDisposable
{
    public const string HttpClientName = "dgii-public";
    private static readonly TimeSpan PauseAfterFailure = TimeSpan.FromMinutes(2);
    private static readonly RncLookup Unavailable = new(DgiiAnswerKind.Unavailable);

    private readonly bool _enabled = config.GetValue("Fiscal:DgiiPublic:Enabled", true);
    private readonly int _perMinute = config.GetValue("Fiscal:DgiiPublic:PerMinute", 30);
    private readonly int _perDay = config.GetValue("Fiscal:DgiiPublic:PerDay", 500);
    private readonly ConcurrentDictionary<string, (object Answer, DateTime ExpiresUtc)> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTime> _lastMinute = new();
    private readonly Lock _writing = new();
    private DateOnly _day;
    private int _today;
    private long _pausedUntilTicks;

    /// <summary>The table writes queued so far (tests wait on it before starting a "new app").</summary>
    internal Task Written { get; private set; } = Task.CompletedTask;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public void Dispose() => _gate.Dispose();

    public async Task<RncLookup> RncAsync(string rnc, CancellationToken ct)
    {
        var digits = Rnc.Normalize(rnc);
        if (!Rnc.IsValid(digits)) return Unavailable;
        var key = "rnc:" + digits;
        if (await CachedAsync<RncLookup>(key, ct) is { } hit) return hit;
        var html = await AskAsync("rnc.aspx", purchase: false, new()
        {
            ["__EVENTTARGET"] = "", ["__EVENTARGUMENT"] = "", ["ctl00$cphMain$txtRNCCedula"] = digits!, ["ctl00$cphMain$btnBuscarPorRNC"] = "BUSCAR",
            ["ctl00$cphMain$txtRazonSocial"] = "", ["ctl00$cphMain$hidActiveTab"] = "",
        }, ct);
        if (html is null) return Unavailable;
        if (Read(html, DgiiPages.ReadRnc) is not { } answer) return Failed("rnc.aspx", "a page it cannot read", Unavailable);
        Remember(key, answer, answer.Kind == DgiiAnswerKind.Found ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1));
        return answer;
    }

    public async Task<NcfCheck> NcfAsync(string issuerRnc, string ncf, CancellationToken ct)
    {
        var rnc = Rnc.Normalize(issuerRnc);
        ncf = ncf.Trim().ToUpperInvariant();
        // An e-NCF is checked on that page only with the security code printed under its QR: Abastalia does not have it here.
        if (!Rnc.IsValid(rnc) || ncf is not { Length: 11 } || ncf[0] != 'B') return NcfCheck.Unknown;
        var key = $"ncf:{rnc}:{ncf}";
        if (await CachedAsync<NcfCheck>(key, ct) is { } hit) return hit;
        var html = await AskAsync("ncf.aspx", purchase: true, new()
        {
            ["ctl00$cphMain$txtRNC"] = rnc!, ["ctl00$cphMain$txtNCF"] = ncf, ["ctl00$cphMain$txtRncComprador"] = "", ["ctl00$cphMain$txtCodigoSeg"] = "",
            ["ctl00$cphMain$btnConsultar"] = "Buscar",
        }, ct);
        if (html is null) return NcfCheck.Unknown;
        if (Read(html, DgiiPages.ReadNcf) is not { } answer) return Failed("ncf.aspx", "a page it cannot read", NcfCheck.Unknown);
        // A valid or expired receipt stays so; "does not match" may be a sequence the DGII is still loading.
        if (answer.Validity != NcfValidity.Unknown)
            Remember(key, answer, answer.Validity == NcfValidity.NotMatching ? TimeSpan.FromHours(1) : TimeSpan.MaxValue);
        return answer;
    }

    /// <summary>A page so odd its regular expressions time out is one Abastalia cannot read: never an error.</summary>
    private static T? Read<T>(string html, Func<string, T?> read) where T : class
    {
        try { return read(html); }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>GET the form (its ViewState and cookies), POST it back with <paramref name="fields"/>. Null when the store may not ask now or the DGII failed.</summary>
    private async Task<string?> AskAsync(string page, bool purchase, Dictionary<string, string> fields, CancellationToken ct)
    {
        if (!_enabled || !await TryTakeAsync(purchase, ct)) return null;
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

    /// <summary>Takes one lookup from the store's minute and day allowance; false while paused after a failure or over the limit.
    /// The day's count is read from the table on the first lookup of a day (so after a restart too) and saved after each one.
    /// R12: RNC lookups (any register) stop at four fifths of the day; the last fifth is kept for the NCF checks of purchases.</summary>
    private async Task<bool> TryTakeAsync(bool purchase, CancellationToken ct)
    {
        var now = Now;
        if (now.Ticks < Interlocked.Read(ref _pausedUntilTicks)) return false;
        await _gate.WaitAsync(ct);
        try
        {
            var day = BusinessDay.Of(now);
            if (day != _day)
                (_day, _today) = (day, int.TryParse((await StoredAsync(DayKey(day), ct))?.Answer, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0);
            while (_lastMinute.TryPeek(out var t) && t <= now.AddMinutes(-1)) _lastMinute.Dequeue();
            if (_lastMinute.Count >= _perMinute || _today >= (purchase ? _perDay : _perDay - _perDay / 5)) return false;
            _lastMinute.Enqueue(now);
            _today++;
            Save(DayKey(day), _today.ToString(CultureInfo.InvariantCulture), now.AddDays(2));
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string DayKey(DateOnly day) => "day:" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private T Failed<T>(string page, string why, T result)
    {
        log.LogWarning("DGII public consultation {Page} failed ({Why}); asking again in {Minutes} minutes", page, why, PauseAfterFailure.TotalMinutes);
        Interlocked.Exchange(ref _pausedUntilTicks, (Now + PauseAfterFailure).Ticks);
        return result;
    }

    private async Task<T?> CachedAsync<T>(string key, CancellationToken ct) where T : class
    {
        var now = Now;
        if (_cache.TryGetValue(key, out var e)) return e.ExpiresUtc > now ? (T)e.Answer : null;
        if (await StoredAsync(key, ct) is not { } row || row.ExpiresUtc <= now) return null;
        T? answer;
        try { answer = JsonSerializer.Deserialize<T>(row.Answer); }
        catch (JsonException) { return null; } // a row from an older shape: as if not cached
        if (answer is null) return null;
        _cache[key] = (answer, row.ExpiresUtc);
        return answer;
    }

    private void Remember<T>(string key, T answer, TimeSpan lifetime) where T : class
    {
        var expires = lifetime == TimeSpan.MaxValue ? DateTime.MaxValue : Now + lifetime;
        _cache[key] = (answer, expires);
        Save(key, JsonSerializer.Serialize(answer), expires);
    }

    /// <summary>The table row, or null when it cannot be read (the database busy or down): a lookup never fails because of its cache.</summary>
    private async Task<DgiiPublicAnswer?> StoredAsync(string key, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<FiscalDbContext>().DgiiPublicAnswers.AsNoTracking().FirstOrDefaultAsync(a => a.Key == key, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Could not read the DGII public answer {Key}", key);
            return null;
        }
    }

    /// <summary>Queues an upsert after the previous ones; it also purges what expired. A failure only costs a lookup later.</summary>
    private void Save(string key, string answer, DateTime expiresUtc)
    {
        lock (_writing) Written = Written.ContinueWith(_ => WriteAsync(key, answer, expiresUtc), TaskScheduler.Default).Unwrap();
    }

    private async Task WriteAsync(string key, string answer, DateTime expiresUtc)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FiscalDbContext>();
            var now = Now;
            await db.DgiiPublicAnswers.Where(a => a.ExpiresUtc <= now).ExecuteDeleteAsync();
            var row = await db.DgiiPublicAnswers.FirstOrDefaultAsync(a => a.Key == key);
            if (row is null) db.DgiiPublicAnswers.Add(row = new DgiiPublicAnswer { Key = key });
            (row.Answer, row.ExpiresUtc) = (answer, expiresUtc);
            await db.SaveChangesAsync();
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not keep the DGII public answer {Key}", key);
        }
    }
}
