using System.Globalization;

namespace Toki.Escuadra;

/// <summary>How amounts look everywhere: 1,234.50 (the Dominican convention), weights to the thousandth of a pound.</summary>
public static class Formato
{
    private static readonly CultureInfo Cultura = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Local = CultureInfo.GetCultureInfo("es-DO");

    public static string Pesos(decimal valor) => valor.ToString("#,##0.00", Cultura);

    public static string ConMoneda(decimal valor) => "RD$ " + Pesos(valor);

    public static string Cantidad(decimal valor, string unidad) => unidad == "Libra"
        ? valor.ToString("0.000", Cultura) + " lb"
        : valor.ToString("0.###", Cultura);

    public static string Hora(DateTimeOffset momento) => momento.ToLocalTime().ToString("h:mm tt", Local);

    public static string Fecha(DateTimeOffset momento) => momento.ToLocalTime().ToString("d MMM yyyy, h:mm tt", Local);

    /// <summary>Reads what a cashier types ("1,250.5" or "1250.50"); null when it is not a number.</summary>
    public static decimal? LeerNumero(string? texto) =>
        decimal.TryParse(texto?.Replace(",", "", StringComparison.Ordinal).Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, Cultura, out var v) ? v : null;
}
