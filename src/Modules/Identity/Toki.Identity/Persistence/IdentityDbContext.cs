using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Toki.BuildingBlocks.Persistence;
using Toki.Identity.Domain;

namespace Toki.Identity.Persistence;

internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "identity";

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<PairingCode> PairingCodes => Set<PairingCode>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SupervisorApproval> Approvals => Set<SupervisorApproval>();

    public Task<bool> IsAdministratorAsync(Guid userId, CancellationToken ct) =>
        Users.AnyAsync(u => u.Id == userId && u.RoleId == Role.AdministratorId, ct);
}

internal sealed class IdentityConfiguration :
    IEntityTypeConfiguration<User>, IEntityTypeConfiguration<Role>, IEntityTypeConfiguration<Device>,
    IEntityTypeConfiguration<PairingCode>, IEntityTypeConfiguration<Session>, IEntityTypeConfiguration<SupervisorApproval>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.HasIndex(u => u.Code).IsUnique();
        b.Property(u => u.Code).HasMaxLength(20);
        // Parallel wrong PINs must not overwrite each other's count (lockout bypass on SQL Server).
        b.Property(u => u.FailedAttempts).IsConcurrencyToken();
        b.Property(u => u.PinHash).HasMaxLength(500);
    }

    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("Roles");
        b.Property(r => r.Permissions)
            .HasConversion(p => string.Join(',', p), s => s.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList())
            .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                (a, c) => a!.SequenceEqual(c!), l => l.Aggregate(0, (h, s) => HashCode.Combine(h, s)), l => l.ToList()));
        b.Property(r => r.Permissions).HasMaxLength(4000);
        b.HasData(Role.Defaults());
    }

    public void Configure(EntityTypeBuilder<Device> b)
    {
        b.Property(d => d.TokenHash).HasMaxLength(32);
        b.HasIndex(d => d.TokenHash).IsUnique();
        b.Property(d => d.CertificateThumbprint).HasMaxLength(64);
        b.HasIndex(d => d.CertificateThumbprint).IsUnique();
    }

    public void Configure(EntityTypeBuilder<PairingCode> b)
    {
        b.Property(c => c.CodeHash).HasMaxLength(32);
        b.HasIndex(c => c.CodeHash);
    }

    public void Configure(EntityTypeBuilder<Session> b)
    {
        b.ToTable("Sessions");
        b.Property(s => s.TokenHash).HasMaxLength(32);
        b.HasIndex(s => s.TokenHash).IsUnique();
    }

    public void Configure(EntityTypeBuilder<SupervisorApproval> b) => b.ToTable("SupervisorApprovals");
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class IdentityDesignTimeFactory : Toki.BuildingBlocks.Persistence.DesignTimeFactory<IdentityDbContext>;
