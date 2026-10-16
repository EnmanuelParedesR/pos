using Microsoft.EntityFrameworkCore;
using Toki.Host.Cloud.Persistence;

namespace Toki.Host.Cloud.Fe;

/// <summary>TK-151: the owner's consent and the copy of the store's e-CF certificate in Key Vault (docs/ecf-propio.md §10.3, option C).
/// One active row per tenant (a new consent revokes the previous one) and per RNC across tenants. Neither the .p12 nor its password is kept:
/// only where the non-exportable key lives (<see cref="KeyName"/>) and what the owner agreed to.</summary>
public sealed class FeCertificate
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>The business RNC the receiver URLs are published for (<c>/{amb}/{rnc}/fe/…</c>).</summary>
    public string Rnc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Thumbprint { get; set; } = "";
    public DateTime NotAfterUtc { get; set; }
    /// <summary>The certificate's name in the vault (one per consent: a soft-deleted name cannot be reused).</summary>
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

/// <summary>Every signature made with a tenant's certificate in the cloud (TK-151): when, what kind of document and its SHA-256. Shown in the panel.
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
    public byte[] XmlGzip { get; set; } = [];
    public DateTime ReceivedUtc { get; set; }
    /// <summary>Commercial approval the owner gave (TK-154): Approved or Rejected; null while none.</summary>
    public string? Approval { get; set; }
    public string? ApprovalReason { get; set; }
    public byte[]? ApprovalXmlGzip { get; set; }
    public DateTime? ApprovalUtc { get; set; }
    /// <summary>When the issuer's approval URL took it, and when the DGII took its copy.</summary>
    public DateTime? ApprovalDeliveredUtc { get; set; }
    public DateTime? ApprovalDgiiUtc { get; set; }
    public string? ApprovalMessage { get; set; }
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
            e.Property(c => c.OwnerName).HasMaxLength(150);
            e.Property(c => c.HolderName).HasMaxLength(150);
            e.Property(c => c.ConsentVersion).HasMaxLength(20);
            e.Property(c => c.RevokedBy).HasMaxLength(256);
            e.HasIndex(c => c.TenantId);
            e.HasIndex(c => c.Rnc);
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
            e.HasIndex(d => new { d.TenantId, d.IssuerRnc, d.ENcf }).IsUnique();
            e.HasIndex(d => new { d.TenantId, d.Id });
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
