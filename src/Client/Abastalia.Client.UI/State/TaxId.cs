namespace Abastalia.Client.UI.State;

/// <summary>TK-304: an RNC (9 digits) or cédula (11) as people type it ("101-01234-5", "001-1234567-8"). One rule for every screen that takes one.</summary>
public static class TaxId
{
    /// <summary>Without dashes or spaces; null when blank.</summary>
    public static string? Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : string.Concat(text.Where(c => c != '-' && !char.IsWhiteSpace(c)));

    /// <summary>9 or 11 digits, once normalized.</summary>
    public static bool IsValid(string? taxId) => taxId is { Length: 9 or 11 } && taxId.All(char.IsAsciiDigit);
}
