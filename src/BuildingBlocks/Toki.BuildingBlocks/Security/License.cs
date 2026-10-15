namespace Toki.BuildingBlocks.Security;

/// <summary>Sellable functions switched on by the license (docs/funcionalidades-v1.md §5). None of them is built in V1 yet.</summary>
public enum Feature { ButcherModule, WholesaleModule, CloudBackup, AiAssistant }

public enum Limit { Registers }

/// <summary>Trial: no license installed. Grace: expired less than 15 days ago (offline stores keep everything).
/// Restricted: expired longer: paid functions off and trial limits, but selling, e-CF, exports and backups never stop.</summary>
public enum LicenseStatus { Trial, Active, Grace, Restricted }

/// <summary>What the store's license allows (arquitectura-v1-vsa §9). Selling, fiscal and backup code must never ask it (an architecture test checks).</summary>
public interface IFeatureService
{
    LicenseStatus Status { get; }
    bool IsEnabled(Feature feature);
    int LimitOf(Limit limit);
}
