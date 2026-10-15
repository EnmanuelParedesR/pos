using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Toki.BuildingBlocks.Persistence;
using Toki.Inventory.Domain;

namespace Toki.Inventory.Persistence;

internal sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "inventory";

    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Presentation> Presentations => Set<Presentation>();
    public DbSet<Barcode> Barcodes => Set<Barcode>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Stock> Stock => Set<Stock>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<LabelFormat> LabelFormats => Set<LabelFormat>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // A cashier who types "jamon" finds "Jamón" (SQL Server only; Sqlite has no such collation).
        if (Database.IsSqlServer()) modelBuilder.Entity<Product>().Property(p => p.Name).UseCollation("Latin1_General_100_CI_AI");
    }
}

internal sealed class StockCountConfiguration : IEntityTypeConfiguration<StockCount>, IEntityTypeConfiguration<StockCountLine>
{
    public void Configure(EntityTypeBuilder<StockCount> b)
    {
        b.ToTable("StockCounts");
        b.Property(c => c.Status).HasConversion<string>().HasMaxLength(12);
        b.HasMany(c => c.Lines).WithOne().HasForeignKey(l => l.StockCountId);
    }

    public void Configure(EntityTypeBuilder<StockCountLine> b)
    {
        b.ToTable("StockCountLines");
        b.HasKey(l => new { l.StockCountId, l.ProductId });
    }
}

internal sealed class InventoryConfiguration :
    IEntityTypeConfiguration<Category>, IEntityTypeConfiguration<Product>, IEntityTypeConfiguration<Presentation>,
    IEntityTypeConfiguration<Barcode>, IEntityTypeConfiguration<Warehouse>, IEntityTypeConfiguration<Stock>,
    IEntityTypeConfiguration<StockMovement>, IEntityTypeConfiguration<LabelFormat>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.Property(c => c.Color).HasMaxLength(7);
        b.Property(c => c.Notes)
            .HasConversion(n => string.Join('\n', n), s => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList())
            .HasMaxLength(2000)
            .Metadata.SetValueComparer(new ValueComparer<List<string>>((a, c) => a!.SequenceEqual(c!), l => l.Aggregate(0, (h, s) => HashCode.Combine(h, s)), l => l.ToList()));
    }

    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.Property(p => p.Name).HasMaxLength(150);
        b.HasIndex(p => p.Name);
        b.Property(p => p.Plu).HasMaxLength(6);
        b.HasIndex(p => p.Plu).IsUnique().HasFilter("[Plu] IS NOT NULL");
        b.Property(p => p.Version).IsConcurrencyToken();
        b.Property(p => p.Unit).HasConversion<string>().HasMaxLength(10);
        b.Property(p => p.ItbisRate).HasConversion<string>().HasMaxLength(12);
    }

    public void Configure(EntityTypeBuilder<Presentation> b)
    {
        b.ToTable("Presentations");
        b.HasIndex(p => p.ProductId);
    }

    public void Configure(EntityTypeBuilder<Barcode> b)
    {
        b.ToTable("Barcodes");
        b.HasKey(c => c.Code);
        b.Property(c => c.Code).HasMaxLength(50);
        b.HasIndex(c => c.ProductId);
    }

    public void Configure(EntityTypeBuilder<Warehouse> b)
    {
        b.ToTable("Warehouses");
        b.HasData(new Warehouse { Id = Warehouse.MainId, Name = "Tienda" });
    }

    public void Configure(EntityTypeBuilder<Stock> b) => b.HasKey(e => new { e.ProductId, e.WarehouseId });

    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.ToTable("StockMovements");
        b.HasKey(m => m.Sequence);
        b.Property(m => m.Sequence).ValueGeneratedOnAdd();
        b.HasIndex(m => m.Id).IsUnique();
        b.HasIndex(m => new { m.ProductId, m.Sequence });
        b.HasIndex(m => m.ReferenceId);
        b.Property(m => m.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(m => m.Reason).HasMaxLength(250);
    }

    public void Configure(EntityTypeBuilder<LabelFormat> b)
    {
        b.ToTable("LabelFormats");
        b.HasKey(f => f.Prefix);
        b.Property(f => f.Prefix).HasMaxLength(2);
        b.HasData(LabelFormat.Defaults());
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class InventoryDesignTimeFactory : Toki.BuildingBlocks.Persistence.DesignTimeFactory<InventoryDbContext>;
