namespace Abastalia.Settings.Contracts;

/// <summary>Shop data printed on receipts and limits that apply to every register.</summary>
/// <param name="DiscountCapPercent">Largest discount (percent of each line, after the global discount) a cashier may give without a supervisor.</param>
/// <param name="RequirePaymentReference">R11 A16: card and transfer payments at the register need their authorization or transfer number.</param>
/// <param name="LogoVersion">TK-280: changes with each logo upload (a cache key); null when the business has no logo.</param>
public sealed record BusinessProfile(string Name, string? Rnc, string? Address, string? Phone, string? ReceiptFooter, decimal DiscountCapPercent,
    bool RequirePaymentReference = true, string? LogoVersion = null);

public interface ISettingsQueries
{
    Task<BusinessProfile> GetBusinessProfileAsync(CancellationToken ct);

    /// <summary>TK-280: the business logo as the PNG Settings wrote (at most 600 px a side), or null.</summary>
    Task<byte[]?> GetLogoAsync(CancellationToken ct);
}

/// <summary>TK-249: the store's license, for the cloud channel (Sync): a store with a paid subscription gets its signed license from the cloud.</summary>
public interface IStoreLicense
{
    /// <summary>Installs <paramref name="jws"/> when it is valid (signed by Abastalia, offline or cloud key) and issued after the installed one.
    /// False when it was refused or brings nothing new.</summary>
    Task<bool> InstallFromCloudAsync(string jws, CancellationToken ct);

    /// <summary>TK-276: the cloud says the trial lasts <paramref name="days"/> (the business came recommended). False when it changes nothing.</summary>
    Task<bool> ExtendTrialAsync(int days, CancellationToken ct);
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

/// <summary>TK-238: an encrypted backup (owner's passphrase) waiting to be copied to the cloud.</summary>
public sealed record CloudBackupFile(string FileName, string Path);

/// <summary>TK-238: the copy of the backups in the cloud, which the Sync module uploads. Settings says what to upload and shows how it went.</summary>
public interface ICloudBackups
{
    /// <summary>The newest scheduled backup when the owner turned the cloud copy on and it is not in the cloud yet; null otherwise.</summary>
    Task<CloudBackupFile?> PendingAsync(CancellationToken ct);

    /// <summary>The cloud keeps <paramref name="fileName"/>.</summary>
    Task UploadedAsync(string fileName, CancellationToken ct);

    /// <summary>Why the last upload did not happen (Spanish, for Configuración → Respaldos and the health check); null clears it.</summary>
    void Failed(string? problem);
}

/// <summary>TK-280: the business logo for the cloud (the public catalog shows it): the PNG Settings wrote, under 48 KB, or null when it was removed.</summary>
public sealed record BusinessLogoChangedV1(byte[]? Png) : Abastalia.SharedKernel.IntegrationEvent(Guid.Empty);
