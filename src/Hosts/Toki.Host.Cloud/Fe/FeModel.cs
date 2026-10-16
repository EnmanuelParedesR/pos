using Microsoft.EntityFrameworkCore;
using Toki.Host.Cloud.Persistence;

namespace Toki.Host.Cloud.Fe;

/// <summary>TK-246: who signs what the receiver answers, chosen per tenant when the receiver is activated.</summary>
public static class FeSigningModes
{
    /// <summary>No certificate in the cloud: the store's main PC signs each acknowledgement over the node channel, and sends its own approvals.</summary>
    public const string StoreSigned = "StoreSigned";
    /// <summary>TK-151: a non-exportable copy of the certificate in Key Vault, with the owner's consent; the cloud signs and sends.</summary>
    public const string CloudCustody = "CloudCustody";

    public static bool IsValid(string? mode) => mode is StoreSigned or CloudCustody;

    /// <summary><c>Fe:DefaultSigningMode</c> (StoreSigned): what the activation screen offers first.</summary>
    public static string Default(IConfiguration config) => config["Fe:DefaultSigningMode"] is { } mode
        ? IsValid(mode) ? mode : throw new InvalidOperationException($"Fe:DefaultSigningMode '{mode}': use StoreSigned or CloudCustody.")
        : StoreSigned;
}

/// <summary>The tenant's active receiver: its RNC, the owner's consent and how it signs (<see cref="SigningMode"/>, TK-246). In
/// <see cref="FeSigningModes.CloudCustody"/> (TK-151, docs/ecf-propio.md §10.3, option C) a copy of the store's certificate lives in Key Vault
/// (<see cref="KeyName"/>); in <see cref="FeSigningModes.StoreSigned"/> nothing of the certificate but its public part (subject, thumbprint, expiry,
/// from the signed proof of the RNC) is here. One active row per tenant (a new activation revokes the previous one) and per RNC across tenants.
/// Neither a .p12 nor its password is ever kept.</summary>
public sealed class FeCertificate
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>The business RNC the receiver URLs are published for (<c>/{amb}/{rnc}/fe/…</c>).</summary>
    public string Rnc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Thumbprint { get; set; } = "";
    public DateTime NotAfterUtc { get; set; }
    /// <summary>StoreSigned or CloudCustody (<see cref="FeSigningModes"/>).</summary>
    public string SigningMode { get; set; } = FeSigningModes.CloudCustody;
    /// <summary>The certificate's name in the vault (one per consent: a soft-deleted name cannot be reused). Empty when the store signs.</summary>
    public string KeyName { get; set; } = "";
    /// <summary>Who consented, as typed on the consent screen: the owner and the certificate holder.</summary>
    public string OwnerName { get; set; } = "";
    public string HolderName { get; set; } = "";
    /// <summary>Which consent text they accepted (<see cref="FeConsent.Version"/>).</summary>
    public string ConsentVersion { get; set; } = "";
    public Guid ConsentedByNodeId { get; set; }
    public DateTime ConsentedUtc { get; set; }
    /// <summary>Consent withdrawn: the key is deleted from the vault and nothing is signed from then on.</summary>
    public DateTime? RevokedUtc { get; set; }
    public string? RevokedBy { get; set; }
}

/// <summary>Every signature made for the tenant's receiver, in the vault (TK-151) or by the store on request (TK-246): when, what kind of document
/// and its SHA-256. Shown in the panel.
/// The signed ARECF itself is kept here too (10 years, docs/ecf-propio.md §9).</summary>
public sealed class FeSignature
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CertificateId { get; set; }
    public DateTime AtUtc { get; set; }
    /// <summary>ARECF, ACECF or SemillaModel: the only roots the cloud signer accepts.</summary>
    public string Root { get; set; } = "";
    public string? ENcf { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>The signed acknowledgement (gzip); null for seeds and approvals (those are kept with their document).</summary>
    public byte[]? XmlGzip { get; set; }
}

/// <summary>TK-152/154: an e-CF a supplier delivered to the tenant's reception URL and acknowledged as received. The store pulls it (cursor <see cref="Id"/>).</summary>
public sealed class FeReceivedDocument
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>testecf, certecf or ecf: from the URL the supplier used.</summary>
    public string Environment { get; set; } = "";
    public string IssuerRnc { get; set; } = "";
    public string IssuerName { get; set; } = "";
    public string BuyerRnc { get; set; } = "";
    public string ENcf { get; set; } = "";
    public int Type { get; set; }
    public DateOnly IssuedOn { get; set; }
    public decimal Total { get; set; }
    public decimal Itbis { get; set; }
    /// <summary>The document exactly as the supplier sent it, in its own encoding (R9 #17), gzip.</summary>
    public byte[] XmlGzip { get; set; } = [];
    /// <summary>SHA-256 of those bytes: the same e-NCF sent again with the same bytes is a duplicate; with others, a <see cref="Conflict"/>.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>R9 #1: the issuer's e-NCF arrived more than once with different content. Every version is kept and flagged: which one is real is
    /// for the owner to check with the supplier (or the DGII), not for the first one to arrive.</summary>
    public bool Conflict { get; set; }
    public DateTime ReceivedUtc { get; set; }
    /// <summary>TK-246: when its signed ARECF was made. Null while pending: it arrived with the store off (the supplier got a 503) and is
    /// acknowledged when the store is back (<see cref="FeLateAcknowledger"/>); the late ARECF is kept, not sent (§11, question 44).</summary>
    public DateTime? AcknowledgedUtc { get; set; }
    /// <summary>Commercial approval the owner gave at the store (TK-154, TK-246): Approved or Rejected; null while none.</summary>
    public string? Approval { get; set; }
    public string? ApprovalReason { get; set; }
    public byte[]? ApprovalXmlGzip { get; set; }
    public DateTime? ApprovalUtc { get; set; }
    /// <summary>When the issuer's approval URL took it, and when the DGII took its copy.</summary>
    public DateTime? ApprovalDeliveredUtc { get; set; }
    public DateTime? ApprovalDgiiUtc { get; set; }
    public string? ApprovalMessage { get; set; }
    /// <summary>TK-250: Approved or Rejected, decided by an owner in the panel and waiting for the PC that activated the receiver (it holds the
    /// certificate) to sign and send it. Kept after the store records it: <see cref="Approval"/> is the result (a decision made at the store first wins).</summary>
    public string? PanelDecision { get; set; }
    public string? PanelReason { get; set; }
    /// <summary>Who decided (e-mail) and when: the audit trail of the panel's decision.</summary>
    public string? PanelDecidedBy { get; set; }
    public DateTime? PanelDecidedUtc { get; set; }
}

/// <summary>TK-152: a commercial approval (ACECF) a buyer sent about an e-CF the tenant issued. Linked to the archive by e-NCF.</summary>
public sealed class FeApprovalReceived
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public string Environment { get; set; } = "";
    public string ENcf { get; set; } = "";
    public string BuyerRnc { get; set; } = "";
    public bool Approved { get; set; }
    public string? Reason { get; set; }
    public byte[] XmlGzip { get; set; } = [];
    public DateTime ReceivedUtc { get; set; }
}

/// <summary>TK-246: a stretch the receiver could not sign because the store's main PC (StoreSigned) was off or did not answer. Opened at the first
/// request it missed, closed at its next poll. The panel shows the hours per PC.</summary>
public sealed class FeReceiverOutage
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid NodeId { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
}

internal static class FeModel
{
    /// <remarks>Documents (gzip) have no max length: varbinary(max) on SQL Server, a supplier's e-CF can pass 8 KB compressed.</remarks>
    public static void Configure(ModelBuilder builder)
    {
        builder.Entity<FeCertificate>(e =>
        {
            e.ToTable("FeCertificates");
            e.Property(c => c.Rnc).HasMaxLength(11);
            e.Property(c => c.Subject).HasMaxLength(500);
            e.Property(c => c.Thumbprint).HasMaxLength(64);
            e.Property(c => c.KeyName).HasMaxLength(127);
            e.Property(c => c.SigningMode).HasMaxLength(20); // the migration marks the rows from before TK-246 CloudCustody
            e.Property(c => c.OwnerName).HasMaxLength(150);
            e.Property(c => c.HolderName).HasMaxLength(150);
            e.Property(c => c.ConsentVersion).HasMaxLength(20);
            e.Property(c => c.RevokedBy).HasMaxLength(256);
            e.HasIndex(c => c.TenantId);
            e.HasIndex(c => c.Rnc).IsUnique().HasFilter("[RevokedUtc] IS NULL"); // R9 #6: one active receiver per RNC, even when two consents race
        });
        builder.Entity<FeSignature>(e =>
        {
            e.ToTable("FeSignatures");
            e.Property(s => s.Root).HasMaxLength(20);
            e.Property(s => s.ENcf).HasMaxLength(13);
            e.Property(s => s.Sha256).HasMaxLength(64);
            e.HasIndex(s => new { s.TenantId, s.AtUtc });
        });
        builder.Entity<FeReceivedDocument>(e =>
        {
            e.ToTable("FeReceived");
            e.Property(d => d.Environment).HasMaxLength(10);
            e.Property(d => d.IssuerRnc).HasMaxLength(11);
            e.Property(d => d.IssuerName).HasMaxLength(250);
            e.Property(d => d.BuyerRnc).HasMaxLength(11);
            e.Property(d => d.ENcf).HasMaxLength(13);
            e.Property(d => d.Approval).HasMaxLength(10);
            e.Property(d => d.ApprovalReason).HasMaxLength(250);
            e.Property(d => d.ApprovalMessage).HasMaxLength(1000);
            e.Property(d => d.PanelDecision).HasMaxLength(10);
            e.Property(d => d.PanelReason).HasMaxLength(250);
            e.Property(d => d.PanelDecidedBy).HasMaxLength(256);
            e.Property(d => d.Sha256).HasMaxLength(64);
            // R9 #5: per environment, a TesteCF document never makes the eCF one a duplicate.
            e.HasIndex(d => new { d.TenantId, d.Environment, d.IssuerRnc, d.ENcf, d.Sha256 }).IsUnique();
            e.HasIndex(d => new { d.TenantId, d.Id });
            e.HasIndex(d => d.AcknowledgedUtc);
        });
        builder.Entity<FeReceiverOutage>(e =>
        {
            e.ToTable("FeReceiverOutages");
            e.HasIndex(o => new { o.TenantId, o.NodeId, o.ToUtc });
        });
        builder.Entity<FeApprovalReceived>(e =>
        {
            e.ToTable("FeApprovalsReceived");
            e.Property(a => a.Environment).HasMaxLength(10);
            e.Property(a => a.ENcf).HasMaxLength(13);
            e.Property(a => a.BuyerRnc).HasMaxLength(11);
            e.Property(a => a.Reason).HasMaxLength(250);
            e.HasIndex(a => new { a.TenantId, a.ENcf });
        });
    }
}
