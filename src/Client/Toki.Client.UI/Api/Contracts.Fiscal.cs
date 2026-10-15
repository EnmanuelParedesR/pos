namespace Toki.Client.UI.Api;

// Electronic receipts (TK-204, TK-205, TK-176). Same rules as Contracts.cs: camelCase JSON, enums by name (as strings here: Names reads them).

/// <summary><c>GET /api/fiscal/queue</c> whole, for the "Comprobantes electrónicos" screen.</summary>
public sealed record EcfQueue(string Environment, int Pending, int Sent, int Overdue, DateTime? NextDeadlineUtc, int Rejected, int Simulated,
    DateTime? CertificateExpiresUtc);

/// <summary>A row of <c>GET /api/fiscal/documents</c>.</summary>
public sealed record EcfDocument(Guid Id, string ENcf, string Type, Guid ReferenceId, decimal Total, decimal Itbis, DateTime SignedUtc, string Status,
    DateTime SubmitDeadlineUtc, string? LastError, string Environment, string Channel, string? TrackId, int? DgiiCode, string? DgiiMessages,
    bool? SequenceUsed, bool Contingency, string? ReplacesENcf, string? ReplacedByENcf);

/// <summary>A row of <c>GET /api/fiscal/sequences</c>. <c>Current</c>: of the environment the store issues in now.</summary>
public sealed record EcfSequence(Guid Id, string Type, string Environment, bool Current, string From, string To, string Next, long Available,
    decimal PercentUsed, DateOnly? ExpiresOn);

/// <summary>A row of <c>GET /api/fiscal/voids</c> (ANECF).</summary>
public sealed record EcfVoidRow(Guid Id, string Type, string Environment, string From, string To, long Count, DateTime CreatedUtc, string Status, string? Message);

/// <summary>An ANECF signed and queued: the numbers it voids.</summary>
public sealed record EcfVoided(Guid Id, string From, string To, long Count);

/// <summary>The new e-CF that replaces a rejected (or voided) one.</summary>
public sealed record EcfIssued(Guid DocumentId, string ENcf);

/// <summary><c>GET /api/fiscal/certificate</c>. <c>InUse</c>: the store signs with it; <c>ExpiresSoon</c>: 30 days or less left.</summary>
public sealed record EcfCertificateStatus(bool Installed, bool InUse, string? Subject, string? TaxId, string? Issuer, DateTime? NotAfterUtc, string? Thumbprint,
    string? Store, int? DaysLeft, bool ExpiresSoon);
