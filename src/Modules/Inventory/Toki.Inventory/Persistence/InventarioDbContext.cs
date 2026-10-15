using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Toki.BuildingBlocks.Persistence;
using Toki.Inventory.Domain;

namespace Toki.Inventory.Persistence;

internal sealed class InventarioDbContext(DbContextOptions<InventarioDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "inventario";

    public DbSet<Categoria> Categorias => Set<Categoria>();
    public DbSet<Producto> Productos => Set<Producto>();
    public DbSet<Presentacion> Presentaciones => Set<Presentacion>();
    public DbSet<CodigoBarras> Codigos => Set<CodigoBarras>();
    public DbSet<Almacen> Almacenes => Set<Almacen>();
    public DbSet<Existencia> Existencias => Set<Existencia>();
    public DbSet<Movimiento> Movimientos => Set<Movimiento>();
    public DbSet<FormatoEtiqueta> FormatosEtiqueta => Set<FormatoEtiqueta>();
    public DbSet<ConteoFisico> Conteos => Set<ConteoFisico>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // A cashier who types "jamon" finds "Jamón" (SQL Server only; Sqlite has no such collation).
        if (Database.IsSqlServer()) modelBuilder.Entity<Producto>().Property(p => p.Nombre).UseCollation("Latin1_General_100_CI_AI");
    }
}

internal sealed class ConteoConfiguration : IEntityTypeConfiguration<ConteoFisico>, IEntityTypeConfiguration<ConteoLinea>
{
    public void Configure(EntityTypeBuilder<ConteoFisico> b)
    {
        b.ToTable("ConteosFisicos");
        b.Property(c => c.Estado).HasConversion<string>().HasMaxLength(12);
        b.HasMany(c => c.Lineas).WithOne().HasForeignKey(l => l.ConteoId);
    }

    public void Configure(EntityTypeBuilder<ConteoLinea> b)
    {
        b.ToTable("ConteosFisicosLineas");
        b.HasKey(l => new { l.ConteoId, l.ProductoId });
    }
}

internal sealed class InventarioConfiguration :
    IEntityTypeConfiguration<Categoria>, IEntityTypeConfiguration<Producto>, IEntityTypeConfiguration<Presentacion>,
    IEntityTypeConfiguration<CodigoBarras>, IEntityTypeConfiguration<Almacen>, IEntityTypeConfiguration<Existencia>,
    IEntityTypeConfiguration<Movimiento>, IEntityTypeConfiguration<FormatoEtiqueta>
{
    public void Configure(EntityTypeBuilder<Categoria> b)
    {
        b.Property(c => c.Color).HasMaxLength(7);
        b.Property(c => c.Notas)
            .HasConversion(n => string.Join('\n', n), s => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList())
            .HasMaxLength(2000)
            .Metadata.SetValueComparer(new ValueComparer<List<string>>((a, c) => a!.SequenceEqual(c!), l => l.Aggregate(0, (h, s) => HashCode.Combine(h, s)), l => l.ToList()));
    }

    public void Configure(EntityTypeBuilder<Producto> b)
    {
        b.Property(p => p.Nombre).HasMaxLength(150);
        b.HasIndex(p => p.Nombre);
        b.Property(p => p.Plu).HasMaxLength(6);
        b.HasIndex(p => p.Plu).IsUnique().HasFilter("[Plu] IS NOT NULL");
        b.Property(p => p.Version).IsConcurrencyToken();
        b.Property(p => p.Unidad).HasConversion<string>().HasMaxLength(10);
        b.Property(p => p.TasaItbis).HasConversion<string>().HasMaxLength(12);
    }

    public void Configure(EntityTypeBuilder<Presentacion> b)
    {
        b.ToTable("Presentaciones");
        b.HasIndex(p => p.ProductoId);
    }

    public void Configure(EntityTypeBuilder<CodigoBarras> b)
    {
        b.ToTable("CodigosBarras");
        b.HasKey(c => c.Codigo);
        b.Property(c => c.Codigo).HasMaxLength(50);
        b.HasIndex(c => c.ProductoId);
    }

    public void Configure(EntityTypeBuilder<Almacen> b)
    {
        b.ToTable("Almacenes");
        b.HasData(new Almacen { Id = Almacen.PrincipalId, Nombre = "Tienda" });
    }

    public void Configure(EntityTypeBuilder<Existencia> b) => b.HasKey(e => new { e.ProductoId, e.AlmacenId });

    public void Configure(EntityTypeBuilder<Movimiento> b)
    {
        b.ToTable("MovimientosInventario");
        b.HasKey(m => m.Secuencia);
        b.Property(m => m.Secuencia).ValueGeneratedOnAdd();
        b.HasIndex(m => m.Id).IsUnique();
        b.HasIndex(m => new { m.ProductoId, m.Secuencia });
        b.HasIndex(m => m.ReferenciaId);
        b.Property(m => m.Tipo).HasConversion<string>().HasMaxLength(30);
        b.Property(m => m.Motivo).HasMaxLength(250);
    }

    public void Configure(EntityTypeBuilder<FormatoEtiqueta> b)
    {
        b.ToTable("FormatosEtiqueta");
        b.HasKey(f => f.Prefijo);
        b.Property(f => f.Prefijo).HasMaxLength(2);
        b.HasData(FormatoEtiqueta.Predeterminados());
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class DisenoInventario : Toki.BuildingBlocks.Persistence.DisenoDbContext<InventarioDbContext>;
