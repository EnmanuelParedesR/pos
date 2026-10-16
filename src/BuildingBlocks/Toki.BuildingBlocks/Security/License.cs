namespace Toki.BuildingBlocks.Security;

/// <summary>Sellable functions switched on by the license (docs/funcionalidades-v1.md §5). None of them is built in V1 yet.</summary>
public enum Feature { ButcherModule, WholesaleModule, CloudBackup, AiAssistant }

public enum Limit { Registers }

/// <summary>Trial: no license installed, 14 days from the first sale with everything on (TK-196). Grace: expired less than 15 days ago
/// (offline stores keep everything). Restricted: the trial ended or the license expired longer ago: paid functions off and one register
/// besides the main PC, but selling, e-CF, exports and backups never stop and no data is lost (TK-220).</summary>
public enum LicenseStatus { Trial, Active, Grace, Restricted }

/// <summary>What the store's license allows (arquitectura-v1-vsa §9). Selling, fiscal and backup code must never ask it (an architecture test checks).</summary>
public interface IFeatureService
{
    LicenseStatus Status { get; }
    /// <summary>When the trial ends, or when the installed license expires; null while the trial has not started (no sale yet).</summary>
    DateTime? EndsUtc { get; }
    bool IsEnabled(Feature feature);
    int LimitOf(Limit limit);
}
