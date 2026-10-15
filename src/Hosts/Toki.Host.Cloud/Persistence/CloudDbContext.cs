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
    public DbSet<Store> Stores => Set<Store>();
    public DbSet<ConnectionCode> ConnectionCodes => Set<ConnectionCode>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<EcfRelayRecord> EcfRelays => Set<EcfRelayRecord>();

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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("cloud");

        builder.Entity<CloudUser>().HasIndex(u => u.TenantId);
        builder.Entity<Tenant>(e =>
        {
            e.ToTable("Tenants");
            e.Property(t => t.Name).HasMaxLength(256);
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
            e.Property(n => n.SecretHash).HasMaxLength(32);
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
            e.HasIndex(r => new { r.TenantId, r.ENcf }).IsUnique();
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
    public Guid TenantId { get; set; }
}

public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
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

/// <summary>A store's main PC. Only the SHA-256 of its secret is kept.</summary>
public sealed class Node
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public string Name { get; set; } = "";
    public byte[] SecretHash { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
    /// <summary>When the last batch arrived (the node is alive).</summary>
    public DateTime? LastSyncUtc { get; set; }
    /// <summary>Newest event time received (how fresh the store's data is).</summary>
    public DateTime? LastEventUtc { get; set; }
}

public enum EcfRelayStatus { Accepted, AcceptedConditional, Rejected, Pending, Simulated }

/// <summary>A signed e-CF a node sent through the cloud. The e-NCF is the natural key: sending it again returns this result.</summary>
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
    public string Message { get; set; } = "";
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
