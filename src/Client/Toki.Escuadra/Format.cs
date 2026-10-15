using System.Globalization;

namespace Toki.Escuadra;

/// <summary>How amounts look everywhere: 1,234.50 (the Dominican convention), weights to the thousandth of a pound.</summary>
public static class Format
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Local = CultureInfo.GetCultureInfo("es-DO");

    public static string Amount(decimal value) => value.ToString("#,##0.00", Invariant);

    public static string WithCurrency(decimal value) => "RD$ " + Amount(value);

    public static string Quantity(decimal value, string unit) => unit == "Pound"
        ? value.ToString("0.000", Invariant) + " lb"
        : value.ToString("0.###", Invariant);

    public static string Time(DateTimeOffset moment) => moment.ToLocalTime().ToString("h:mm tt", Local);

    public static string Date(DateTimeOffset moment) => moment.ToLocalTime().ToString("d MMM yyyy, h:mm tt", Local);

    /// <summary>Reads what a cashier types ("1,250.5" or "1250.50"); null when it is not a number.</summary>
    public static decimal? ParseNumber(string? text) =>
        decimal.TryParse(text?.Replace(",", "", StringComparison.Ordinal).Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, Invariant, out var v) ? v : null;
}
