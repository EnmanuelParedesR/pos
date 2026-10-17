using System.Text.Json;
using System.Text.RegularExpressions;

namespace Abastalia.Client.UI.Talia;

/// <param name="Permissions">Any one of them opens the screen; the cloud offers Talia only those (null = everyone in Administración).</param>
public sealed record StoreScreen(string Route, string Label, IReadOnlyList<string>? Permissions = null);

/// <summary>TK-342: the screens Talia may suggest (storeScreens.json, embedded here and in the cloud). Only these become links or an "Abrir …" button.</summary>
public static partial class StoreScreens
{
    public static readonly IReadOnlyList<StoreScreen> All = JsonSerializer.Deserialize<List<StoreScreen>>(
        typeof(StoreScreens).Assembly.GetManifestResourceStream("Abastalia.StoreScreens.json")!, JsonSerializerOptions.Web)!;

    /// <summary>The button's label for a screen Talia suggested; null for anything outside the list (no button).</summary>
    public static string? Label(string? route) => All.FirstOrDefault(s => s.Route == route)?.Label;

    /// <summary>The answer cut into text and known routes, in order: only a route of the list becomes a link; everything else stays text.</summary>
    public static IEnumerable<(string Text, string? Route)> Parts(string text)
    {
        var at = 0;
        foreach (Match m in Path().Matches(text))
        {
            var route = m.Groups[2];
            if (Label(route.Value) is null) continue;
            yield return (text[at..route.Index], null);
            yield return (route.Value, route.Value);
            at = route.Index + route.Length;
        }
        yield return (text[at..], null);
    }

    [GeneratedRegex(@"(^|[\s(«""'¿¡])(/[a-z-]+(?:/[a-z-]+)*)(?![A-Za-z0-9/#?-])")]
    private static partial Regex Path();
}
