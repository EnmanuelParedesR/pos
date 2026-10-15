namespace Toki.Settings.Contracts;

/// <summary>Shop data printed on receipts and limits that apply to every register.</summary>
/// <param name="DiscountCapPercent">Largest discount (percent of each line, after the global discount) a cashier may give without a supervisor.</param>
public sealed record BusinessProfile(string Name, string? Rnc, string? Address, string? Phone, string? ReceiptFooter, decimal DiscountCapPercent);

public interface ISettingsQueries
{
    Task<BusinessProfile> GetBusinessProfileAsync(CancellationToken ct);
}
