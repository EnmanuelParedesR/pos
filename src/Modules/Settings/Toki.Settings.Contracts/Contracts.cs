namespace Toki.Settings.Contracts;

/// <summary>Shop data printed on receipts and limits that apply to every register.</summary>
/// <param name="DiscountCapPercent">Largest discount (percent of each line, after the global discount) a cashier may give without a supervisor.</param>
public sealed record BusinessProfile(string Name, string? Rnc, string? Address, string? Phone, string? ReceiptFooter, decimal DiscountCapPercent);

public interface ISettingsQueries
{
    Task<BusinessProfile> GetBusinessProfileAsync(CancellationToken ct);
}

/// <summary>TK-249: the store's license, for the cloud channel (Sync): a store with a paid subscription gets its signed license from the cloud.</summary>
public interface IStoreLicense
{
    /// <summary>Installs <paramref name="jws"/> when it is valid (signed by Toki, offline or cloud key) and issued after the installed one.
    /// False when it was refused or brings nothing new.</summary>
    Task<bool> InstallFromCloudAsync(string jws, CancellationToken ct);
}
