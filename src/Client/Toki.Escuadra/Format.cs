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

    /// <summary>The store's clock: Dominican Republic time, UTC−4 all year. A copy of SharedKernel's <c>BusinessDay.Offset</c>
    /// (Escuadra does not reference it), so a PC with the wrong time zone still shows the server's hours and days.</summary>
    public static readonly TimeSpan StoreOffset = TimeSpan.FromHours(-4);

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow + StoreOffset);

    public static DateTime StoreTime(DateTimeOffset moment) => moment.UtcDateTime + StoreOffset;

    public static string Time(DateTimeOffset moment) => StoreTime(moment).ToString("h:mm tt", Local);

    public static string Date(DateTimeOffset moment) => StoreTime(moment).ToString("d MMM yyyy, h:mm tt", Local);

    public static string ShortDate(DateTimeOffset moment) => StoreTime(moment).ToString("dd/MM/yyyy", Invariant);

    /// <summary>Reads what a cashier types ("1,250.5" or "1250.50"); null when it is not a number.</summary>
    public static decimal? ParseNumber(string? text) =>
        decimal.TryParse(text?.Replace(",", "", StringComparison.Ordinal).Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, Invariant, out var v) ? v : null;
}
