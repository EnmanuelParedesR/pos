namespace Toki.Settings.Contracts;

/// <summary>Shop data printed on receipts and limits that apply to every register.</summary>
/// <param name="DiscountCapPercent">Largest discount (percent of each line, after the global discount) a cashier may give without a supervisor.</param>
/// <param name="RequirePaymentReference">R11 A16: card and transfer payments at the register need their authorization or transfer number.</param>
public sealed record BusinessProfile(string Name, string? Rnc, string? Address, string? Phone, string? ReceiptFooter, decimal DiscountCapPercent,
    bool RequirePaymentReference = true);

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

/// <summary>TK-254: how this store takes updates, as the owner set it in Configuración → Actualizaciones and the cloud allows it.</summary>
/// <param name="Channel">"pilot" or "stable"; null follows appsettings.</param>
/// <param name="OutsideHours">Updates install on their own outside business hours.</param>
/// <param name="RequestedUtc">"Actualizar ahora": install as soon as no shift is open (or after closing time).</param>
/// <param name="AllowedVersion">The highest version the cloud allows (staged rollout): null = not managed by the cloud, "" = wait.</param>
public sealed record UpdatePolicy(string? Channel, bool OutsideHours, DateTime? RequestedUtc, string? AllowedVersion);

public interface IUpdatePolicy
{
    Task<UpdatePolicy> GetAsync(CancellationToken ct);
    /// <summary>The newest server version the feed offers (null: up to date).</summary>
    Task SetAvailableAsync(string? version, CancellationToken ct);
    /// <summary>What the cloud answered on the node channel.</summary>
    Task SetAllowedAsync(string? version, string? notesUrl, CancellationToken ct);
    /// <summary>The server runs the requested version: "Actualizar ahora" is done.</summary>
    Task AppliedAsync(CancellationToken ct);
}
