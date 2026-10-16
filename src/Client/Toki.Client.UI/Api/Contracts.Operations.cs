namespace Toki.Client.UI.Api;

// TK-238: DGII environment on screen, backup schedule, store health.

public sealed record FiscalModeTarget(string Environment, List<string> Blockers);

/// <param name="Saved">True when the environment was chosen on screen; false while the store follows appsettings.</param>
public sealed record FiscalModeState(string Environment, bool Saved, bool AutoSubmit, DateTime? ChangedUtc, List<FiscalModeTarget> Targets);

public sealed record BackupSettingsInfo(string? CopyTo, string Time, int Keep, bool Automatic, bool Saved, string Folder, string? PassphraseProblem,
    BackupFile? Last, string? CopyProblem);

public sealed record BackupWarning(string? PassphraseProblem);

/// <param name="Status">Healthy, Degraded or Unhealthy.</param>
public sealed record StoreHealthCheck(string Name, string Status, string Title, string? Detail, string? Action);
