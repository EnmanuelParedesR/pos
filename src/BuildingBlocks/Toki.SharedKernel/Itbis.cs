namespace Toki.SharedKernel;

/// <summary>ITBIS rate of a product (Dominican VAT).</summary>
public enum TasaItbis { General18, Reducida16, Cero, Exento }

public static class Itbis
{
    public static decimal Porcentaje(TasaItbis tasa) => tasa switch
    {
        TasaItbis.General18 => 0.18m,
        TasaItbis.Reducida16 => 0.16m,
        _ => 0m,
    };

    /// <summary>ITBIS contained in an amount whose price already includes it (shelf prices in the DR include ITBIS).</summary>
    public static decimal Incluido(decimal montoConItbis, TasaItbis tasa)
    {
        var p = Porcentaje(tasa);
        return Dinero.Redondear(montoConItbis * p / (1 + p));
    }
}

/// <summary>Dominican taxpayer id: RNC (9 digits) or cédula (11 digits).</summary>
public static class Rnc
{
    /// <remarks>shortcut: format only, no check digit; some valid old cédulas fail the official check, so it would block real customers.</remarks>
    public static bool EsValido(string? valor) => valor is { Length: 9 or 11 } && valor.All(char.IsAsciiDigit);

    public static string? Normalizar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : new string(valor.Where(char.IsAsciiDigit).ToArray());
}
