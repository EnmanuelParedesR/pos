using Toki.Client.UI.Api;

namespace Toki.Client.UI.State;

/// <summary>TK-239: a typed RNC (9 digits) or cédula (11) brings the name from the DGII registry (Comprobantes → Padrón).
/// TK-252: the store asks the DGII consultation first, so the status (Activo, Suspendido…) is today's; the loaded registry answers when it cannot.
/// The name stays editable; the hint is quiet: an unloaded registry or an unknown number never blocks saving.</summary>
public static class TaxpayerLookup
{
    private sealed record Taxpayer(string Rnc, string Name, string? TradeName, bool IsActive, string? Status, string? Category, bool FromDgii);

    /// <returns><c>Status</c>: the DGII status as the badge shows it ("Activo", "Suspendido"); null when the DGII did not answer.</returns>
    public static async Task<(string? Name, string? Hint, string? Status)> FindAsync(TokiApi api, string? taxId)
    {
        var digits = new string((taxId ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is not (9 or 11)) return (null, null, null);
        var r = await api.GetAsync<Taxpayer>($"/api/fiscal/taxpayers/{digits}");
        if (r.Value is { } t)
            return (t.Name, t.IsActive ? null : t.FromDgii ? $"La DGII lo muestra como {StatusText(t.Status)?.ToLowerInvariant()}." : "Aparece como inactivo en el padrón de la DGII.",
                t.FromDgii ? StatusText(t.Status) : null);
        return (null, r.Problem?.Code switch
        {
            "Fiscal.RegistryNotLoaded" => "El padrón de la DGII no está cargado; escriba el nombre.",
            "Fiscal.RncNotFound" => "No aparece en la DGII.",
            _ => null,
        }, null);
    }

    /// <summary>"DADO DE BAJA" → "Dado de baja".</summary>
    public static string? StatusText(string? status) =>
        string.IsNullOrWhiteSpace(status) ? null : char.ToUpperInvariant(status.Trim()[0]) + status.Trim()[1..].ToLowerInvariant();

    /// <summary>Fills the name only when it is empty or still the one filled before (never over what the user typed).</summary>
    public static string? Fill(string? current, string? previousFill, string? found) =>
        found is not null && (string.IsNullOrWhiteSpace(current) || current == previousFill) ? found : current;
}
