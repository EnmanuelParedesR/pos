namespace Toki.SharedKernel;

/// <summary>The single rounding policy for money in the whole product.</summary>
/// <remarks>shortcut: money is a plain decimal in DOP; add a currency type when USD payments ship.</remarks>
public static class Money
{
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

/// <summary>How a payment is spread over open invoices: in the given order (oldest first), each up to its balance.</summary>
public static class Allocation
{
    /// <returns>The invoices that receive something, with the amount; the rest of <paramref name="amount"/> is <c>amount − sum</c>.</returns>
    public static List<(T Item, decimal Amount)> InOrder<T>(decimal amount, IEnumerable<(T Item, decimal Balance)> open)
    {
        var result = new List<(T, decimal)>();
        foreach (var (item, balance) in open)
        {
            if (amount <= 0) break;
            var part = Math.Min(amount, balance);
            if (part <= 0) continue;
            result.Add((item, part));
            amount -= part;
        }
        return result;
    }
}

/// <summary>Dominican business day. The Dominican Republic is UTC-4 all year (no daylight saving).</summary>
public static class BusinessDay
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-4);

    public static DateOnly Of(DateTime utc) => DateOnly.FromDateTime(utc + Offset);

    /// <summary>UTC range [from, to) that covers the given local business days.</summary>
    public static (DateTime From, DateTime To) UtcRange(DateOnly from, DateOnly to) =>
        (Utc(from.ToDateTime(TimeOnly.MinValue) - Offset), Utc(to.AddDays(1).ToDateTime(TimeOnly.MinValue) - Offset));

    private static DateTime Utc(DateTime v) => DateTime.SpecifyKind(v, DateTimeKind.Utc);
}
