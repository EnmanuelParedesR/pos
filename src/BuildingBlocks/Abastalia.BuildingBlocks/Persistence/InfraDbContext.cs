using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Abastalia.BuildingBlocks.Persistence;

/// <summary>Infrastructure tables: the outbox and the hash-chained audit trail. Modules write them only through IOutbox and IAuditLog.</summary>
public sealed class InfraDbContext(DbContextOptions<InfraDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "infra";

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<AuditHead> AuditHead => Set<AuditHead>();
}

public sealed class OutboxMessage
{
    public long Sequence { get; set; }
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public Guid AggregateId { get; set; }
    public string Payload { get; set; } = "";
    public DateTime OccurredUtc { get; set; }
    public Guid? UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public DateTime? ProcessedUtc { get; set; }
    public DateTime? UploadedUtc { get; set; }
}

public sealed class AuditRecord
{
    public long Sequence { get; set; }
    public Guid Id { get; set; }
    public DateTime DateUtc { get; set; }
    public Guid? UserId { get; set; }
    public Guid? SupervisorId { get; set; }
    public Guid? DeviceId { get; set; }
    public string Action { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public Guid ResourceId { get; set; }
    public string Data { get; set; } = "";
    public byte[] PreviousHash { get; set; } = [];
    public byte[] Hash { get; set; } = [];
}

/// <summary>Single row with the head of the audit chain. Updating it first serializes audit writers.</summary>
public sealed class AuditHead
{
    public int Id { get; set; }
    /// <summary>TK-319: a property, not a shadow one, because EF seeds skip shadow properties; set on save.</summary>
    public Guid TenantId { get; set; }
    public long Sequence { get; set; }
    public byte[] Hash { get; set; } = [];

    /// <summary>"#seq hash8", printed on Z reports: the paper keeps how far the chain went, so a rewound head shows up.</summary>
    public static async Task<string> TextAsync(InfraDbContext db, CancellationToken ct)
    {
        var h = await db.AuditHead.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        return $"#{h.Sequence} {Convert.ToHexString(h.Hash)[..8]}";
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("OutboxMessages");
        b.HasKey(m => m.Sequence);
        b.Property(m => m.Sequence).ValueGeneratedOnAdd();
        b.HasIndex(m => m.Id).IsUnique();
        b.Property(m => m.Payload).HasMaxLength(ModuleDbContext.LongText);
        b.HasIndex(m => m.ProcessedUtc);
        b.HasIndex(m => m.UploadedUtc); // the cloud uploader reads WHERE UploadedUtc IS NULL every 5 s; rows go 90 days after both (TK-304)
    }
}

internal sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> b)
    {
        b.ToTable("AuditRecords");
        b.HasKey(r => r.Sequence);
        b.Property(r => r.Sequence).ValueGeneratedNever();
        b.Property(r => r.Data).HasMaxLength(ModuleDbContext.LongText);
        b.Property(r => r.PreviousHash).HasMaxLength(32);
        b.Property(r => r.Hash).HasMaxLength(32);
        b.HasIndex(r => new { r.ResourceType, r.ResourceId });
    }
}

internal sealed class AuditHeadConfiguration : IEntityTypeConfiguration<AuditHead>
{
    public void Configure(EntityTypeBuilder<AuditHead> b)
    {
        b.ToTable("AuditHead");
        b.Property(h => h.Id).ValueGeneratedNever();
        b.Property(h => h.Hash).HasMaxLength(32);
        b.HasData(new AuditHead { Id = 1, Sequence = 0, Hash = new byte[32] });
    }
}
