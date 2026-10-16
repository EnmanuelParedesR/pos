using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Toki.Host.Cloud.Persistence;

/// <summary>The whole cloud database: accounts and stores (<c>cloud</c>), received events (<c>ingest</c>) and the panel's projections (<c>replica</c>).
/// Sqlite (tests, development) ignores schemas, so table names are unique across them.</summary>
public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options) : IdentityDbContext<CloudUser>(options)
{
    /// <summary>Text columns that hold JSON or documents (nvarchar(max) / varbinary(max) on SQL Server).</summary>
    public const int LongText = 8000;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantMember> TenantMembers => Set<TenantMember>();
    public DbSet<TenantInvite> TenantInvites => Set<TenantInvite>();
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<ConnectionCode> ConnectionCodes => Set<ConnectionCode>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<EcfRelayRecord> EcfRelays => Set<EcfRelayRecord>();
    public DbSet<PlatformAuditRecord> PlatformAudit => Set<PlatformAuditRecord>();

    public DbSet<InboxEvent> Inbox => Set<InboxEvent>();

    public DbSet<ReplicaSale> Sales => Set<ReplicaSale>();
    public DbSet<ReplicaSaleLine> SaleLines => Set<ReplicaSaleLine>();
    public DbSet<ReplicaSalePayment> SalePayments => Set<ReplicaSalePayment>();
    public DbSet<ReplicaSaleVoid> SaleVoids => Set<ReplicaSaleVoid>();
    public DbSet<ReplicaReturn> Returns => Set<ReplicaReturn>();
    public DbSet<ReplicaClosedShift> ClosedShifts => Set<ReplicaClosedShift>();
    public DbSet<ReplicaStockMovement> StockMovements => Set<ReplicaStockMovement>();
    public DbSet<ReplicaPriceChange> PriceChanges => Set<ReplicaPriceChange>();
    public DbSet<ReplicaFiscalDocument> FiscalDocuments => Set<ReplicaFiscalDocument>();
    public DbSet<ReplicaStoreUser> StoreUsers => Set<ReplicaStoreUser>();
    public DbSet<ReplicaRegister> Registers => Set<ReplicaRegister>();
    public DbSet<ReplicaReturnLine> ReturnLines => Set<ReplicaReturnLine>();
    public DbSet<ReplicaPurchase> Purchases => Set<ReplicaPurchase>();
    public DbSet<ReplicaPurchaseVoid> PurchaseVoids => Set<ReplicaPurchaseVoid>();
    public DbSet<ReplicaCustomerCharge> CustomerCharges => Set<ReplicaCustomerCharge>();
    public DbSet<ReplicaCustomerPayment> CustomerPayments => Set<ReplicaCustomerPayment>();
    public DbSet<ReplicaCustomer> Customers => Set<ReplicaCustomer>();
    public DbSet<ReplicaOpenedShift> OpenedShifts => Set<ReplicaOpenedShift>();
    public DbSet<ReplicaCashMovement> CashMovements => Set<ReplicaCashMovement>();
    public DbSet<ReplicaNodeHeartbeat> NodeHeartbeats => Set<ReplicaNodeHeartbeat>();
    public DbSet<ReplicaDailyUsage> DailyUsage => Set<ReplicaDailyUsage>();
    public DbSet<Catalog.ReplicaCatalogItem> CatalogItems => Set<Catalog.ReplicaCatalogItem>();
    public DbSet<Catalog.ReplicaCatalogSettings> CatalogSettings => Set<Catalog.ReplicaCatalogSettings>();

    public DbSet<Fe.FeCertificate> FeCertificates => Set<Fe.FeCertificate>();
    public DbSet<Fe.FeSignature> FeSignatures => Set<Fe.FeSignature>();
    public DbSet<Fe.FeReceivedDocument> FeReceived => Set<Fe.FeReceivedDocument>();
    public DbSet<Fe.FeApprovalReceived> FeApprovalsReceived => Set<Fe.FeApprovalReceived>();
    public DbSet<Fe.FeReceiverOutage> FeReceiverOutages => Set<Fe.FeReceiverOutage>();
    public DbSet<ReplicaCertification> Certifications => Set<ReplicaCertification>();
    public DbSet<Alerts.AlertPreference> AlertPreferences => Set<Alerts.AlertPreference>();
    public DbSet<Alerts.SentAlert> SentAlerts => Set<Alerts.SentAlert>();
    public DbSet<Assistant.AiUsage> AiUsage => Set<Assistant.AiUsage>();
    public DbSet<Quotas.TenantUsage> TenantUsage => Set<Quotas.TenantUsage>();
    public DbSet<Billing.TenantSubscription> TenantSubscriptions => Set<Billing.TenantSubscription>();
    public DbSet<Billing.BillingEvent> BillingEvents => Set<Billing.BillingEvent>();
    public DbSet<Billing.PaddleProduct> PaddleProducts => Set<Billing.PaddleProduct>();
    public DbSet<Billing.Referral> Referrals => Set<Billing.Referral>();
    public DbSet<Certification.AccompanimentRequest> AccompanimentRequests => Set<Certification.AccompanimentRequest>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("cloud");

        builder.Entity<CloudUser>().HasIndex(u => u.TenantId);
        builder.Entity<Tenant>(e =>
        {
            e.ToTable("Tenants");
            e.Property(t => t.Name).HasMaxLength(256);
            e.Property(t => t.Plan).HasMaxLength(50);
            e.Property(t => t.AddOns).HasMaxLength(200);
        });
        builder.Entity<TenantMember>(e =>
        {
            e.ToTable("TenantMembers");
            e.HasKey(m => new { m.TenantId, m.UserId });
            e.Property(m => m.UserId).HasMaxLength(450);
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(m => m.UserId);
        });
        builder.Entity<TenantInvite>(e =>
        {
            e.ToTable("TenantInvites");
            e.Property(i => i.Email).HasMaxLength(256);
            e.Property(i => i.InvitedBy).HasMaxLength(256);
            e.Property(i => i.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.TokenHash).HasMaxLength(32);
            e.HasIndex(i => i.TokenHash).IsUnique();
            e.HasIndex(i => i.TenantId);
            // Accepting saves with WHERE AcceptedUtc IS NULL AND RevokedUtc IS NULL: two people with the same link, or a revoke in between, cannot both win.
            e.Property(i => i.AcceptedUtc).IsConcurrencyToken();
            e.Property(i => i.RevokedUtc).IsConcurrencyToken();
        });
        builder.Entity<Store>(e =>
        {
            e.ToTable("Stores");
            e.Property(s => s.Name).HasMaxLength(100);
            e.HasIndex(s => new { s.TenantId, s.Name });
        });
        builder.Entity<ConnectionCode>(e =>
        {
            e.ToTable("ConnectionCodes");
            e.Property(c => c.Code).HasMaxLength(8);
            e.HasIndex(c => c.Code);
        });
        builder.Entity<Node>(e =>
        {
            e.ToTable("Nodes");
            e.Property(n => n.Name).HasMaxLength(100);
            e.Property(n => n.PublicKey).HasMaxLength(200);
            e.HasIndex(n => new { n.TenantId, n.StoreId });
        });
        builder.Entity<EcfRelayRecord>(e =>
        {
            e.ToTable("EcfRelays");
            e.Property(r => r.ENcf).HasMaxLength(20);
            e.Property(r => r.EcfType).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(r => r.TrackId).HasMaxLength(100);
            e.Property(r => r.Message).HasMaxLength(1000);
            e.Property(r => r.SignedXmlGzip).HasMaxLength(LongText);
            e.Property(r => r.RfceGzip).HasMaxLength(LongText);
            e.Property(r => r.Environment).HasMaxLength(10);
            e.Property(r => r.ReplacesENcf).HasMaxLength(13);
            e.Property(r => r.ReplacedByENcf).HasMaxLength(13);
            e.HasIndex(r => new { r.TenantId, r.Environment, r.ENcf }).IsUnique(); // R9 #4
            e.Property(r => r.ColdKey).HasMaxLength(100);
            e.HasIndex(r => new { r.ColdUtc, r.ReceivedUtc });
        });
        builder.Entity<Assistant.AiUsage>(e =>
        {
            e.ToTable("AiUsage");
            e.HasKey(u => new { u.TenantId, u.Day });
        });
        Billing.BillingModel.Configure(builder); // TK-249
        builder.Entity<Quotas.TenantUsage>(e =>
        {
            e.ToTable("TenantUsage");
            e.HasKey(u => new { u.TenantId, u.Month, u.Meter });
            e.Property(u => u.Meter).HasConversion<string>().HasMaxLength(40);
        });
        builder.Entity<PlatformAuditRecord>(e =>
        {
            e.ToTable("PlatformAudit");
            e.Property(a => a.UserId).HasMaxLength(450);
            e.Property(a => a.Email).HasMaxLength(256);
            e.Property(a => a.Method).HasMaxLength(10);
            e.Property(a => a.Path).HasMaxLength(1000);
            e.HasIndex(a => a.AtUtc);
        });

        builder.Entity<InboxEvent>(e =>
        {
            e.ToTable("Inbox", "ingest");
            e.HasKey(i => i.Sequence);
            e.Property(i => i.Sequence).ValueGeneratedOnAdd();
            e.Property(i => i.Type).HasMaxLength(200);
            e.Property(i => i.Payload).HasMaxLength(LongText);
            e.Property(i => i.Error).HasMaxLength(2000);
            e.HasIndex(i => new { i.TenantId, i.EventId }).IsUnique();
            e.HasIndex(i => i.AppliedUtc);
        });

        builder.Entity<ReplicaSale>(e =>
        {
            e.ToTable("Sales", "replica");
            e.HasKey(s => new { s.TenantId, s.Id });
            e.Property(s => s.ENcf).HasMaxLength(20);
            e.HasIndex(s => new { s.TenantId, s.SoldUtc });
        });
        builder.Entity<ReplicaSaleLine>(e =>
        {
            e.ToTable("SaleLines", "replica");
            e.HasKey(l => new { l.TenantId, l.SaleId, l.LineNumber });
            e.Property(l => l.Description).HasMaxLength(200);
            e.Property(l => l.Unit).HasMaxLength(10);
        });
        builder.Entity<ReplicaSalePayment>(e =>
        {
            e.ToTable("SalePayments", "replica");
            e.HasKey(p => new { p.TenantId, p.SaleId, p.LineNumber });
            e.Property(p => p.Method).HasMaxLength(30);
        });
        builder.Entity<ReplicaSaleVoid>(e =>
        {
            e.ToTable("SaleVoids", "replica");
            e.HasKey(v => new { v.TenantId, v.SaleId });
            e.Property(v => v.CreditNoteENcf).HasMaxLength(20);
            e.Property(v => v.Reason).HasMaxLength(500);
        });
        builder.Entity<ReplicaReturn>(e =>
        {
            e.ToTable("Returns", "replica");
            e.HasKey(r => new { r.TenantId, r.Id });
            e.Property(r => r.CreditNoteENcf).HasMaxLength(20);
            e.Property(r => r.Method).HasMaxLength(30);
            e.HasIndex(r => new { r.TenantId, r.ReturnedUtc });
        });
        builder.Entity<ReplicaClosedShift>(e =>
        {
            e.ToTable("ClosedShifts", "replica");
            e.HasKey(s => new { s.TenantId, s.ShiftId });
            e.HasIndex(s => new { s.TenantId, s.ClosedUtc });
        });
        builder.Entity<ReplicaStockMovement>(e =>
        {
            e.ToTable("StockMovements", "replica");
            e.HasKey(m => new { m.TenantId, m.Id });
            e.Property(m => m.Type).HasMaxLength(30);
            e.HasIndex(m => new { m.TenantId, m.ProductId, m.OccurredUtc });
        });
        builder.Entity<ReplicaPriceChange>(e =>
        {
            e.ToTable("PriceChanges", "replica");
            e.HasKey(p => new { p.TenantId, p.EventId });
            e.HasIndex(p => new { p.TenantId, p.ProductId, p.ChangedUtc });
        });
        builder.Entity<ReplicaFiscalDocument>(e =>
        {
            e.ToTable("FiscalDocuments", "replica");
            e.HasKey(d => new { d.TenantId, d.Id });
            e.Property(d => d.ENcf).HasMaxLength(20);
            e.Property(d => d.Type).HasMaxLength(30);
            e.HasIndex(d => new { d.TenantId, d.IssuedUtc });
        });
        builder.Entity<ReplicaStoreUser>(e =>
        {
            e.ToTable("StoreUsers", "replica");
            e.HasKey(u => new { u.TenantId, u.Id });
            e.Property(u => u.Name).HasMaxLength(100);
        });
        builder.Entity<ReplicaRegister>(e =>
        {
            e.ToTable("Registers", "replica");
            e.HasKey(r => new { r.TenantId, r.Id });
            e.Property(r => r.Name).HasMaxLength(60);
        });
        builder.Entity<ReplicaReturnLine>(e =>
        {
            e.ToTable("ReturnLines", "replica");
            e.HasKey(l => new { l.TenantId, l.ReturnId, l.LineNumber });
            e.Property(l => l.Description).HasMaxLength(200);
        });
        builder.Entity<ReplicaPurchase>(e =>
        {
            e.ToTable("Purchases", "replica");
            e.HasKey(p => new { p.TenantId, p.Id });
            e.Property(p => p.Ncf).HasMaxLength(20);
            e.HasIndex(p => new { p.TenantId, p.PostedUtc });
        });
        builder.Entity<ReplicaPurchaseVoid>(e =>
        {
            e.ToTable("PurchaseVoids", "replica");
            e.HasKey(v => new { v.TenantId, v.PurchaseId });
            e.Property(v => v.Reason).HasMaxLength(500);
        });
        builder.Entity<ReplicaCustomerCharge>(e =>
        {
            e.ToTable("CustomerCharges", "replica");
            e.HasKey(c => new { c.TenantId, c.SaleId });
            e.HasIndex(c => new { c.TenantId, c.CustomerId });
        });
        builder.Entity<ReplicaCustomerPayment>(e =>
        {
            e.ToTable("CustomerPayments", "replica");
            e.HasKey(p => new { p.TenantId, p.Id });
            e.HasIndex(p => new { p.TenantId, p.CustomerId });
        });
        builder.Entity<ReplicaOpenedShift>(e =>
        {
            e.ToTable("OpenedShifts", "replica");
            e.HasKey(s => new { s.TenantId, s.ShiftId });
            e.HasIndex(s => new { s.TenantId, s.OpenedUtc });
        });
        builder.Entity<ReplicaCashMovement>(e =>
        {
            e.ToTable("CashMovements", "replica");
            e.HasKey(m => new { m.TenantId, m.EventId });
            e.Property(m => m.Type).HasMaxLength(30);
            e.Property(m => m.Method).HasMaxLength(30);
            e.HasIndex(m => new { m.TenantId, m.ShiftId });
        });
        builder.Entity<ReplicaNodeHeartbeat>(e =>
        {
            e.ToTable("NodeHeartbeats", "replica");
            e.HasKey(h => new { h.TenantId, h.NodeId });
            e.Property(h => h.AppVersion).HasMaxLength(50);
            e.Property(h => h.Os).HasMaxLength(200);
            e.Property(h => h.LastErrorCode).HasMaxLength(30);
            e.Property(h => h.FiscalProvider).HasMaxLength(30);
            e.Property(h => h.LicenseStatus).HasMaxLength(20);
        });
        builder.Entity<ReplicaCustomer>(e =>
        {
            e.ToTable("Customers", "replica");
            e.HasKey(c => new { c.TenantId, c.Id });
            e.Property(c => c.Name).HasMaxLength(150);
        });
        builder.Entity<ReplicaDailyUsage>(e =>
        {
            e.ToTable("DailyUsage", "replica");
            e.HasKey(u => new { u.TenantId, u.StoreId, u.Day });
        });
        Catalog.PublicCatalogEndpoints.Configure(builder);
        Fe.FeModel.Configure(builder);
        builder.Entity<ReplicaCertification>(e =>
        {
            e.ToTable("Certifications", "replica");
            e.HasKey(c => new { c.TenantId, c.StoreId });
        });
        Alerts.AlertModel.Configure(builder);
        Certification.AccompanimentModel.Configure(builder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcConverter>();
    }

    // Same rule as the store: Unspecified means UTC; only Local is converted.
    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}

/// <summary>Lets dotnet-ef build the context for SQL Server migrations; nothing connects at design time.</summary>
internal sealed class CloudDesignTimeFactory : IDesignTimeDbContextFactory<CloudDbContext>
{
    public CloudDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CloudDbContext>().UseSqlServer("Server=.;Database=toki_cloud_design;Integrated Security=True;TrustServerCertificate=True").Options);
}

/// <summary>A panel user. Each one belongs to exactly one tenant (the owner's business), created on registration.</summary>
public sealed class CloudUser : IdentityUser
{
    /// <summary>The tenant the user works in now (the one put in the token). Access comes from <see cref="TenantMember"/>, never from this alone (TK-217).</summary>
    public Guid TenantId { get; set; }
}

/// <summary>TK-217: what a panel member may do. Owner: everything. ReadOnly: summary, sales, shifts, stores and the assistant; writes nothing.</summary>
public enum MemberRole { Owner, ReadOnly }

/// <summary>A panel account's access to a tenant (TK-217). A tenant always keeps at least one Owner.</summary>
public sealed class TenantMember
{
    public Guid TenantId { get; set; }
    public string UserId { get; set; } = "";
    public MemberRole Role { get; set; }
    public DateTime JoinedUtc { get; set; }
    /// <summary>TK-201: a ReadOnly member sees cost and profit only when an Owner allows it (an accountant). Owners always see them.</summary>
    public bool CanViewCosts { get; set; }
}

/// <summary>A single-use link to join a tenant (TK-217). Only the SHA-256 of the token is kept.</summary>
public sealed class TenantInvite
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Email { get; set; } = "";
    public MemberRole Role { get; set; }
    public byte[] TokenHash { get; set; } = [];
    /// <summary>E-mail of the owner who invited.</summary>
    public string InvitedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? AcceptedUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    /// <summary>R8 #1: the inviter was shown the link (no e-mail provider). An account made from it is then not e-mail-confirmed.</summary>
    public bool LinkShown { get; set; }
}

public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    /// <summary>Set by Toki staff (TK-189): its nodes cannot upload and its users cannot use the panel.</summary>
    public DateTime? DisabledUtc { get; set; }
    /// <summary>TK-248: the plan code whose monthly quotas apply (<c>Quotas:&lt;Plan&gt;</c>); null = Default. Set by Toki staff in the console.</summary>
    public string? Plan { get; set; }
    /// <summary>TK-248: paid add-ons, comma-separated (<c>SupplierInvoiceReading</c>). Set by Toki staff in the console.</summary>
    public string AddOns { get; set; } = "";
}

public sealed class Store
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

/// <summary>8 digits, 30 minutes, single use: typed at the store's main PC to register it as a node.</summary>
public sealed class ConnectionCode
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public string Code { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}

/// <summary>A store's main PC. It signs every call with its own key (TK-241).</summary>
public sealed class Node
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>TK-241: the node's ECDSA P-256 public key (SubjectPublicKeyInfo). The private key never leaves the store's PC.</summary>
    public byte[]? PublicKey { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>When the last batch arrived (the node is alive).</summary>
    public DateTime? LastSyncUtc { get; set; }
    /// <summary>Newest event time received (how fresh the store's data is).</summary>
    public DateTime? LastEventUtc { get; set; }
    /// <summary>Set by Toki staff (TK-189), an Owner in the panel or the store when it disconnects (TK-241): the node's credentials stop working;
    /// the store connects again with a new code.</summary>
    public DateTime? RevokedUtc { get; set; }
}

/// <summary>Every call a Toki staff member makes to /api/platform (TK-189): who, what and when.</summary>
public sealed class PlatformAuditRecord
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; }
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Method { get; set; } = "";
    /// <summary>Path and query string.</summary>
    public string Path { get; set; } = "";
    public Guid? TenantId { get; set; }
}

public enum EcfRelayStatus { Accepted, AcceptedConditional, Rejected, Pending, Simulated }

/// <summary>The cloud archive of a signed e-CF and its DGII result (<c>/fiscal/v1/archive</c>; the type and table keep the relay's name).
/// The environment and the e-NCF are the natural key (R9 #4): a later upload updates the row.</summary>
public sealed class EcfRelayRecord
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid NodeId { get; set; }
    public string ENcf { get; set; } = "";
    public Toki.Fiscal.Contracts.EcfType EcfType { get; set; }
    /// <summary>The signed XML exactly as the node sent it (gzip).</summary>
    public byte[] SignedXmlGzip { get; set; } = [];
    public DateTime ReceivedUtc { get; set; }
    public EcfRelayStatus Status { get; set; }
    public string TrackId { get; set; } = "";
    /// <summary>The DGII messages.</summary>
    public string Message { get; set; } = "";
    /// <summary>TesteCF, CerteCF or eCF.</summary>
    public string Environment { get; set; } = "";
    /// <summary>The signed RFCE (gzip) of a 32 under RD$250,000.</summary>
    public byte[]? RfceGzip { get; set; }
    public int? DgiiCode { get; set; }
    public DateTime? SignedUtc { get; set; }
    public string ReplacesENcf { get; set; } = "";
    public string ReplacedByENcf { get; set; } = "";
    public DateTime? UpdatedUtc { get; set; }
    /// <summary>TK-207: where the XML went once it moved to cold storage (<see cref="Relay.IColdArchive"/>): the blob name without extension.
    /// Empty while it is still in <see cref="SignedXmlGzip"/>.</summary>
    public string ColdKey { get; set; } = "";
    /// <summary>When the XML (and the RFCE, if <see cref="ColdHasRfce"/>) moved to cold storage; the two XML columns are then empty.</summary>
    public DateTime? ColdUtc { get; set; }
    public bool ColdHasRfce { get; set; }
}

/// <summary>An event as a node uploaded it. Received cheaply; applied later by <see cref="Ingest.InboxApplier"/>.</summary>
public sealed class InboxEvent
{
    public long Sequence { get; set; }
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public Guid StoreId { get; set; }
    public Guid NodeId { get; set; }
    public string Type { get; set; } = "";
    public Guid AggregateId { get; set; }
    public DateTime OccurredUtc { get; set; }
    public string Payload { get; set; } = "";
    public DateTime ReceivedUtc { get; set; }
    public DateTime? AppliedUtc { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

/// <summary>Every replica row belongs to one tenant and one store.</summary>
public interface IReplicaRow
{
    Guid TenantId { get; }
    Guid StoreId { get; }
}

public sealed class ReplicaSale : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public long Number { get; set; }
    public Guid ShiftId { get; set; }
    public Guid CashierId { get; set; }
    public Guid? CustomerId { get; set; }
    public string ENcf { get; set; } = "";
    public decimal Total { get; set; }
    public decimal Itbis { get; set; }
    public decimal Discount { get; set; }
    public DateTime SoldUtc { get; set; }
}

public sealed class ReplicaSaleLine : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid SaleId { get; set; }
    public int LineNumber { get; set; }
    public Guid StoreId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? PresentationId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    /// <summary>"Pound" or "Unit" (SaleRecordedV2 on, TK-180); null for sales uploaded as V1.</summary>
    public string? Unit { get; set; }
    public decimal Total { get; set; }
    public decimal Itbis { get; set; }
    public decimal UnitCost { get; set; }
}

public sealed class ReplicaSalePayment : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid SaleId { get; set; }
    public int LineNumber { get; set; }
    public Guid StoreId { get; set; }
    public string Method { get; set; } = "";
    public decimal Amount { get; set; }
}

public sealed class ReplicaSaleVoid : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid SaleId { get; set; }
    public Guid StoreId { get; set; }
    public string CreditNoteENcf { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid? SupervisorId { get; set; }
    public DateTime VoidedUtc { get; set; }
}

public sealed class ReplicaReturn : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public Guid SaleId { get; set; }
    public string CreditNoteENcf { get; set; } = "";
    public string Method { get; set; } = "";
    public decimal Total { get; set; }
    public decimal Itbis { get; set; }
    public DateTime ReturnedUtc { get; set; }
}

/// <summary>A closed shift (Z): what the cash drawer should have had, what was counted and the difference.</summary>
public sealed class ReplicaClosedShift : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid ShiftId { get; set; }
    public Guid StoreId { get; set; }
    public Guid RegisterId { get; set; }
    public Guid CashierId { get; set; }
    public int ZNumber { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal CountedCash { get; set; }
    public decimal Difference { get; set; }
    public DateTime ClosedUtc { get; set; }
}

public sealed class ReplicaStockMovement : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public Guid ProductId { get; set; }
    public Guid WarehouseId { get; set; }
    public string Type { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public Guid? ReferenceId { get; set; }
    public DateTime OccurredUtc { get; set; }
}

/// <summary>Keyed by the event: a price change has no id of its own.</summary>
public sealed class ReplicaPriceChange : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ProductId { get; set; }
    public decimal OldPrice { get; set; }
    public decimal NewPrice { get; set; }
    public decimal? OldWholesalePrice { get; set; }
    public decimal? NewWholesalePrice { get; set; }
    public DateTime ChangedUtc { get; set; }
}

/// <summary>A store user's display name (cashiers in the panel). No PIN, code or permissions ever reach the cloud.</summary>
public sealed class ReplicaStoreUser : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    /// <summary>When the applied event happened: an older one arriving later never overwrites a newer name.</summary>
    public DateTime ChangedUtc { get; set; }
}

public sealed class ReplicaRegister : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public string Name { get; set; } = "";
    public DateTime ChangedUtc { get; set; }
}

/// <summary>A returned line (ReturnRecordedV2 on). Returns uploaded as V1 have no lines.</summary>
public sealed class ReplicaReturnLine : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid ReturnId { get; set; }
    public int LineNumber { get; set; }
    public Guid StoreId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? PresentationId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Total { get; set; }
    public decimal Itbis { get; set; }
    public decimal UnitCost { get; set; }
}

public sealed class ReplicaPurchase : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public Guid SupplierId { get; set; }
    public string Ncf { get; set; } = "";
    public decimal Subtotal { get; set; }
    public decimal Itbis { get; set; }
    public decimal Total { get; set; }
    public bool OnCredit { get; set; }
    public DateTime PostedUtc { get; set; }
}

public sealed class ReplicaPurchaseVoid : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid PurchaseId { get; set; }
    public Guid StoreId { get; set; }
    public string Reason { get; set; } = "";
    public DateTime VoidedUtc { get; set; }
}

/// <summary>A credit (on account) sale charged to a customer. One per sale.</summary>
public sealed class ReplicaCustomerCharge : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid SaleId { get; set; }
    public Guid StoreId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal Amount { get; set; }
    public DateTime ChargedUtc { get; set; }
}

/// <summary>What a customer paid on their credit at the register (the amount applied to their charges).</summary>
public sealed class ReplicaCustomerPayment : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal Amount { get; set; }
    public DateTime PaidUtc { get; set; }
}

/// <summary>A customer's name and account as the store last sent them (CustomerBalanceChangedV1, TK-174).</summary>
public sealed class ReplicaCustomer : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public string Name { get; set; } = "";
    public decimal Debt { get; set; }
    public decimal CreditBalance { get; set; }
    /// <summary>When the applied event happened: an older one arriving later never overwrites newer figures.</summary>
    public DateTime ChangedUtc { get; set; }
}

public sealed class ReplicaOpenedShift : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid ShiftId { get; set; }
    public Guid StoreId { get; set; }
    public Guid RegisterId { get; set; }
    public Guid CashierId { get; set; }
    public decimal OpeningFloat { get; set; }
    public DateTime OpenedUtc { get; set; }
}

/// <summary>Keyed by the event: the movement has no id in CashMovementRecordedV1.</summary>
public sealed class ReplicaCashMovement : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ShiftId { get; set; }
    public string Type { get; set; } = "";
    public string Method { get; set; } = "";
    public decimal Amount { get; set; }
    public Guid? ReferenceId { get; set; }
    public DateTime OccurredUtc { get; set; }
}

public sealed class ReplicaFiscalDocument : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public string ENcf { get; set; } = "";
    public string Type { get; set; } = "";
    public Guid ReferenceId { get; set; }
    public decimal Total { get; set; }
    public decimal Itbis { get; set; }
    public DateTime IssuedUtc { get; set; }
}

/// <summary>The newest <c>NodeHeartbeatV1</c> of a node (TK-190): version, health, features and setup. Counts and flags only.</summary>
public sealed class ReplicaNodeHeartbeat : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid NodeId { get; set; }
    public Guid StoreId { get; set; }
    public DateTime SentUtc { get; set; }
    public string AppVersion { get; set; } = "";
    public string Os { get; set; } = "";
    public int UptimeMinutes { get; set; }
    public int ActiveRegisters { get; set; }
    public int OpenShifts { get; set; }
    public int OutboxBacklog { get; set; }
    public string? LastErrorCode { get; set; }
    public bool OnAccount { get; set; }
    public bool? ScaleConfigured { get; set; }
    public bool? PrinterConfigured { get; set; }
    public string FiscalProvider { get; set; } = "";
    public bool BusinessNameSet { get; set; }
    public bool RncSet { get; set; }
    public int ActiveProducts { get; set; }
    /// <summary>TK-196: Trial, Active, Grace or Restricted; null from versions that do not send it.</summary>
    public string? LicenseStatus { get; set; }
    /// <summary>When the trial ends or the license expires; null before the first sale of a trial.</summary>
    public DateTime? LicenseEndsUtc { get; set; }
    /// <summary>TK-222: the fiscal signals the alerts read; null from stores on versions before it.</summary>
    public int? RejectedWithoutReplacement { get; set; }
    public DateTime? OldestPendingUtc { get; set; }
    public DateTime? CertificateExpiresUtc { get; set; }
}

/// <summary>TK-225: where a store is in the DGII certification (its newest <c>CertificationProgressedV1</c>).</summary>
public sealed class ReplicaCertification : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    /// <summary>The first step not ticked (0-based); <see cref="TotalSteps"/> when all are.</summary>
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public int StepsDone { get; set; }
    /// <summary>The last step (the authorization) is ticked.</summary>
    public bool Certified { get; set; }
    /// <summary>Since when the store is on <see cref="CurrentStep"/>: when its newest ticked step was ticked.</summary>
    public DateTime StepSinceUtc { get; set; }
    public DateTime ChangedUtc { get; set; }
}

/// <summary>A store day as its heartbeats reported it (the newest one wins). Sales also reach the replica one by one; this is the store's own count.</summary>
public sealed class ReplicaDailyUsage : IReplicaRow
{
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public DateOnly Day { get; set; }
    public DateTime SentUtc { get; set; }
    public int Sales { get; set; }
    public decimal SalesAmount { get; set; }
    public int ActiveCashiers { get; set; }
    public int Voids { get; set; }
    public decimal Returns { get; set; }
}
