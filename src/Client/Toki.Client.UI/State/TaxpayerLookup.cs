using Toki.Client.UI.Api;

namespace Toki.Client.UI.State;

/// <summary>TK-239: a typed RNC (9 digits) or cédula (11) brings the name from the DGII registry (Comprobantes → Padrón).
/// The name stays editable; the hint is quiet: an unloaded registry or an unknown number never blocks saving.</summary>
public static class TaxpayerLookup
{
    private sealed record Taxpayer(string Rnc, string Name, string? TradeName, bool IsActive);

    public static async Task<(string? Name, string? Hint)> FindAsync(TokiApi api, string? taxId)
    {
        var digits = new string((taxId ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length is not (9 or 11)) return (null, null);
        var r = await api.GetAsync<Taxpayer>($"/api/fiscal/taxpayers/{digits}");
        if (r.Value is { } t) return (t.Name, t.IsActive ? null : "Aparece como inactivo en el padrón de la DGII.");
        return (null, r.Problem?.Code switch
        {
            "Fiscal.RegistryNotLoaded" => "El padrón de la DGII no está cargado; escriba el nombre.",
            "Fiscal.RncNotFound" => "No aparece en el padrón de la DGII.",
            _ => null,
        });
    }

    /// <summary>Fills the name only when it is empty or still the one filled before (never over what the user typed).</summary>
    public static string? Fill(string? current, string? previousFill, string? found) =>
        found is not null && (string.IsNullOrWhiteSpace(current) || current == previousFill) ? found : current;
}
