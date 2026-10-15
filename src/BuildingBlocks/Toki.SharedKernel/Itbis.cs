namespace Toki.SharedKernel;

/// <summary>ITBIS rate of a product (Dominican VAT).</summary>
public enum ItbisRate { General18, Reduced16, Zero, Exempt }

public static class Itbis
{
    public static decimal Percentage(ItbisRate rate) => rate switch
    {
        ItbisRate.General18 => 0.18m,
        ItbisRate.Reduced16 => 0.16m,
        _ => 0m,
    };

    /// <summary>ITBIS contained in an amount whose price already includes it (shelf prices in the DR include ITBIS).</summary>
    public static decimal Included(decimal amountWithItbis, ItbisRate rate)
    {
        var p = Percentage(rate);
        return Money.Round(amountWithItbis * p / (1 + p));
    }
}

/// <summary>Dominican taxpayer id: RNC (9 digits) or cédula (11 digits).</summary>
public static class Rnc
{
    /// <remarks>shortcut: format only, no check digit; some valid old cédulas fail the official check, so it would block real customers.</remarks>
    public static bool IsValid(string? value) => value is { Length: 9 or 11 } && value.All(char.IsAsciiDigit);

    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : new string(value.Where(char.IsAsciiDigit).ToArray());
}
