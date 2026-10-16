using System.Text.RegularExpressions;
using Serilog.Events;
using Serilog.AspNetCore;

namespace Toki.Host.Local;

/// <summary>R11 C10: the request log (kept 60 days) never carries an RNC or a cédula. A path segment of 9 or 11 digits (dashes allowed),
/// such as /api/fiscal/taxpayers/{rnc}, is written as ***. The query string is never logged.</summary>
internal static partial class RequestLogs
{
    public static string Mask(string path) => TaxId().Replace(path, m => m.Value.Count(char.IsAsciiDigit) is 9 or 11 ? "***" : m.Value);

    [GeneratedRegex(@"(?<=/)[0-9-]{9,13}(?=/|$)")]
    private static partial Regex TaxId();

    public static void Configure(RequestLoggingOptions o) => o.GetMessageTemplateProperties = (http, path, elapsed, status) =>
    [
        new LogEventProperty("RequestMethod", new ScalarValue(http.Request.Method)),
        new LogEventProperty("RequestPath", new ScalarValue(Mask(path))),
        new LogEventProperty("StatusCode", new ScalarValue(status)),
        new LogEventProperty("Elapsed", new ScalarValue(elapsed)),
    ];
}
