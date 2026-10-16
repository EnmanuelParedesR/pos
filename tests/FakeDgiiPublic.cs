using System.Net;
using System.Text;

namespace Abastalia.Tests;

/// <summary>TK-252: the DGII public consultation pages (rnc.aspx, ncf.aspx) from HTML recorded on 2026-10-16 (tests/DgiiPages). Linked into each test
/// project that needs it; no test ever reaches the real DGII. A lookup nobody programmed answers 503, as a DGII that is down.</summary>
public sealed class FakeDgiiPublic : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _answers = new(StringComparer.Ordinal);
    private readonly List<(HttpMethod Method, string Page, Dictionary<string, string> Form, string? Cookie, string? UserAgent)> _requests = [];

    public static string Page(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DgiiPages", name + ".html"));

    /// <summary>What reached the "DGII", in order.</summary>
    public IReadOnlyList<(HttpMethod Method, string Page, Dictionary<string, string> Form, string? Cookie, string? UserAgent)> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    /// <summary>rnc.aspx answers <paramref name="page"/> (a file of tests/DgiiPages) for that RNC.</summary>
    public FakeDgiiPublic Rnc(string rnc, string page) => Answer("rnc:" + rnc, () => Html(Page(page)));

    public FakeDgiiPublic Ncf(string rnc, string ncf, string page) => Answer($"ncf:{rnc}:{ncf}", () => Html(Page(page)));

    /// <summary>Any status or page for a lookup (a key "rnc:…" or "ncf:…:…"), e.g. the WAF's 403.</summary>
    public FakeDgiiPublic Answer(string key, Func<HttpResponseMessage> answer)
    {
        lock (_answers) _answers[key] = answer;
        return this;
    }

    public static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var page = request.RequestUri!.Segments[^1];
        var form = request.Content is null ? [] : (await request.Content.ReadAsStringAsync(cancellationToken)).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.ElementAtOrDefault(1) ?? ""));
        lock (_requests)
            _requests.Add((request.Method, page, form, request.Headers.TryGetValues("Cookie", out var c) ? string.Join("; ", c) : null,
                request.Headers.UserAgent.ToString()));
        if (request.Method == HttpMethod.Get)
        {
            var empty = Html(Page(page == "rnc.aspx" ? "rnc-form" : "ncf-form"));
            empty.Headers.Add("Set-Cookie", "NSC_TEST=abc123;expires=Fri, 16-Oct-2026 16:01:07 GMT;path=/;secure;httponly");
            return empty;
        }
        // The form posts back its ViewState: a POST without it is not what the page sent.
        if (!form.ContainsKey("__VIEWSTATE")) return Html("", HttpStatusCode.BadRequest);
        var key = page == "rnc.aspx" ? "rnc:" + form.GetValueOrDefault("ctl00$cphMain$txtRNCCedula")
            : $"ncf:{form.GetValueOrDefault("ctl00$cphMain$txtRNC")}:{form.GetValueOrDefault("ctl00$cphMain$txtNCF")}";
        Func<HttpResponseMessage>? answer;
        lock (_answers) _answers.TryGetValue(key, out answer);
        return answer?.Invoke() ?? Html("", HttpStatusCode.ServiceUnavailable);
    }
}
