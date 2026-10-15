using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Toki.BuildingBlocks.Persistence;
using Toki.Identidad.Domain;

namespace Toki.Identidad.Persistence;

internal sealed class IdentidadDbContext(DbContextOptions<IdentidadDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "identidad";

    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Rol> Roles => Set<Rol>();
    public DbSet<Dispositivo> Dispositivos => Set<Dispositivo>();
    public DbSet<CodigoEmparejamiento> CodigosEmparejamiento => Set<CodigoEmparejamiento>();
    public DbSet<Sesion> Sesiones => Set<Sesion>();
    public DbSet<AutorizacionSupervisor> Autorizaciones => Set<AutorizacionSupervisor>();

    public Task<bool> EsAdministradorAsync(Guid usuarioId, CancellationToken ct) =>
        Usuarios.AnyAsync(u => u.Id == usuarioId && u.RolId == Rol.AdministradorId, ct);
}

internal sealed class IdentidadConfiguration :
    IEntityTypeConfiguration<Usuario>, IEntityTypeConfiguration<Rol>, IEntityTypeConfiguration<Dispositivo>,
    IEntityTypeConfiguration<CodigoEmparejamiento>, IEntityTypeConfiguration<Sesion>, IEntityTypeConfiguration<AutorizacionSupervisor>
{
    public void Configure(EntityTypeBuilder<Usuario> b)
    {
        b.HasIndex(u => u.Codigo).IsUnique();
        b.Property(u => u.Codigo).HasMaxLength(20);
        // Parallel wrong PINs must not overwrite each other's count (lockout bypass on SQL Server).
        b.Property(u => u.IntentosFallidos).IsConcurrencyToken();
        b.Property(u => u.PinHash).HasMaxLength(500);
    }

    public void Configure(EntityTypeBuilder<Rol> b)
    {
        b.ToTable("Roles");
        b.Property(r => r.Permisos)
            .HasConversion(p => string.Join(',', p), s => s.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList())
            .Metadata.SetValueComparer(new ValueComparer<List<string>>(
                (a, c) => a!.SequenceEqual(c!), l => l.Aggregate(0, (h, s) => HashCode.Combine(h, s)), l => l.ToList()));
        b.Property(r => r.Permisos).HasMaxLength(4000);
        b.HasData(Rol.Predeterminados());
    }

    public void Configure(EntityTypeBuilder<Dispositivo> b)
    {
        b.Property(d => d.TokenHash).HasMaxLength(32);
        b.HasIndex(d => d.TokenHash).IsUnique();
    }

    public void Configure(EntityTypeBuilder<CodigoEmparejamiento> b)
    {
        b.Property(c => c.CodigoHash).HasMaxLength(32);
        b.HasIndex(c => c.CodigoHash);
    }

    public void Configure(EntityTypeBuilder<Sesion> b)
    {
        b.ToTable("Sesiones");
        b.Property(s => s.TokenHash).HasMaxLength(32);
        b.HasIndex(s => s.TokenHash).IsUnique();
    }

    public void Configure(EntityTypeBuilder<AutorizacionSupervisor> b) => b.ToTable("AutorizacionesSupervisor");
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class DisenoIdentidad : Toki.BuildingBlocks.Persistence.DisenoDbContext<IdentidadDbContext>;
