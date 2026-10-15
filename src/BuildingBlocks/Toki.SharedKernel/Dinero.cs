namespace Toki.SharedKernel;

/// <summary>The single rounding policy for money in the whole product.</summary>
/// <remarks>shortcut: money is a plain decimal in DOP; add a currency type when USD payments ship.</remarks>
public static class Dinero
{
    public static decimal Redondear(decimal monto) => Math.Round(monto, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Dominican business day. The Dominican Republic is UTC-4 all year (no daylight saving).</summary>
public static class DiaNegocio
{
    public static readonly TimeSpan Desfase = TimeSpan.FromHours(-4);

    public static DateOnly De(DateTime utc) => DateOnly.FromDateTime(utc + Desfase);

    /// <summary>UTC range [desde, hasta) that covers the given local business days.</summary>
    public static (DateTime Desde, DateTime Hasta) RangoUtc(DateOnly desde, DateOnly hasta) =>
        (Utc(desde.ToDateTime(TimeOnly.MinValue) - Desfase), Utc(hasta.AddDays(1).ToDateTime(TimeOnly.MinValue) - Desfase));

    private static DateTime Utc(DateTime v) => DateTime.SpecifyKind(v, DateTimeKind.Utc);
}
