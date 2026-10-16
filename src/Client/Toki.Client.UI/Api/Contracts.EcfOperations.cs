namespace Toki.Client.UI.Api;

// e-CF operations (TK-155 certification, TK-156 health, TK-157 paper contingency). Same rules as Contracts.cs.

/// <summary>A row of <c>GET /api/fiscal/health</c>. <c>Level</c>: error (red) or warning (amber).</summary>
public sealed record EcfHealthItem(string Level, string Code, string Title, string Detail);

public sealed record EcfHealth(string Environment, DateTime? OldestPendingUtc, DateTime? LastDgiiContactUtc, DateTime? CertificateExpiresUtc, bool CanSign,
    bool ContingencyActive, List<EcfHealthItem> Items);

/// <summary><c>POST /api/fiscal/dgii-status</c>. <c>State</c>: Simulated, Up or Down.</summary>
public sealed record DgiiStatus(string State, string Message, DateTime CheckedUtc);

public sealed record PaperRange(Guid Id, string Type, string From, string To, string Next, long Available, DateOnly? ExpiresOn);
public sealed record PaperPending(Guid Id, string Ncf, string Type, DateTime IssuedUtc, decimal Total);

/// <summary><c>GET /api/fiscal/contingency</c>: series B in use (any signed-in user reads it: the register shows it).</summary>
public sealed record EcfContingencyStatus(bool Active, DateTime? StartedUtc, string? Reason, int? Day, int MaxDays, DateTime? LastEndedUtc, DateOnly? RegularizeBy,
    bool CanSign, string? CannotSignReason, List<PaperRange> Ranges, List<PaperPending> ToRegularize, int Regularized);

/// <param name="Manual">R9 #11: B03 and B04 that are not regularized automatically.</param>
public sealed record PaperRegularized(int Regularized, int Manual = 0);

public sealed record CertificationStepState(int Number, bool Done, DateTime? DoneUtc);
public sealed record CertificationCaseRow(Guid Id, int Row, string ENcf, string Type, string Channel, string Status, string? TrackId, string? Messages);
public sealed record CertificationSimulation(int Accepted, int Rejected, int Waiting);

/// <summary><c>GET /api/fiscal/certification</c>.</summary>
public sealed record CertificationState(string Environment, List<CertificationStepState> Steps, List<CertificationCaseRow> Cases, CertificationSimulation Simulation);

public sealed record TestSetImported(int Imported, List<string> Errors);
public sealed record DgiiFileSigned(string Root, string SignedXml);

/// <summary>TK-233: the accompaniment request as the cloud keeps it. <c>Status</c>: New, Scheduled, Session1Done, Session2Done, Certified or Cancelled.</summary>
public sealed record AccompanimentRequest(Guid Id, string Status, string ContactName, string Phone, string PreferredTimes, DateTime? Session1Utc,
    DateTime? Session2Utc, DateTime CreatedUtc);

/// <summary><c>GET /api/fiscal/accompaniment</c>. <c>Connected</c> false: the store is not connected to the cloud, so nothing can be sent.</summary>
public sealed record AccompanimentState(bool Connected, AccompanimentRequest? Request, string? SuggestedName, string? SuggestedPhone);
