using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Toki.BuildingBlocks.Persistence;
using Toki.Sales.Domain;

namespace Toki.Sales;

internal sealed class SalesDbContext(DbContextOptions<SalesDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "sales";

    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> Lines => Set<SaleLine>();
    public DbSet<SalePayment> Payments => Set<SalePayment>();
    public DbSet<SaleReturn> Returns => Set<SaleReturn>();
    public DbSet<HeldSale> HeldSales => Set<HeldSale>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<CatalogSettings> CatalogSettings => Set<CatalogSettings>();

    /// <summary>Locks the order row until the transaction ends, then reads it (as <see cref="LockSaleAsync"/> does).</summary>
    public async Task<Order?> LockOrderAsync(Guid id, CancellationToken ct)
    {
        await Orders.Where(o => o.Id == id).ExecuteUpdateAsync(u => u.SetProperty(o => o.Version, o => o.Version + 1), ct);
        return await Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
    }

    public Task<Sale?> FullSaleAsync(Guid id, CancellationToken ct) =>
        Sales.Include(s => s.Lines.OrderBy(l => l.Position)).Include(s => s.Payments).FirstOrDefaultAsync(s => s.Id == id, ct);

    /// <summary>Locks the sale row (until the transaction ends) and only then reads it, so the checks see committed data.</summary>
    public async Task<Sale?> LockSaleAsync(Guid id, CancellationToken ct)
    {
        await Sales.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Version, s => s.Version + 1), ct);
        return await FullSaleAsync(id, ct);
    }
}

internal sealed class SalesConfiguration :
    IEntityTypeConfiguration<Sale>, IEntityTypeConfiguration<SaleLine>, IEntityTypeConfiguration<SalePayment>,
    IEntityTypeConfiguration<SaleReturn>, IEntityTypeConfiguration<ReturnLine>, IEntityTypeConfiguration<HeldSale>,
    IEntityTypeConfiguration<Order>, IEntityTypeConfiguration<CatalogItem>, IEntityTypeConfiguration<CatalogSettings>
{
    public void Configure(EntityTypeBuilder<Sale> b)
    {
        // Number is the identity key (Sqlite only auto-increments the primary key); Id is the business key.
        b.HasKey(s => s.Number);
        b.Property(s => s.Number).ValueGeneratedOnAdd();
        b.HasAlternateKey(s => s.Id);
        b.HasIndex(s => s.ShiftId);
        b.HasIndex(s => s.DateUtc);
        b.HasIndex(s => s.CustomerId);
        b.Property(s => s.ENcf).HasMaxLength(13);
        b.Property(s => s.CustomerRnc).HasMaxLength(11);
        b.Property(s => s.QrUrl).HasMaxLength(1000);
        b.Property(s => s.DocumentType).HasConversion<int>();
        b.Property(s => s.PriceLevel).HasConversion<string>().HasMaxLength(10);
        b.Property(s => s.Status).HasConversion<string>().HasMaxLength(12);
        b.Property(s => s.Version).IsConcurrencyToken();
        b.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.SaleId).HasPrincipalKey(s => s.Id);
        b.HasMany(s => s.Payments).WithOne().HasForeignKey(p => p.SaleId).HasPrincipalKey(s => s.Id);
    }

    public void Configure(EntityTypeBuilder<SaleLine> b)
    {
        b.ToTable("SaleLines");
        b.HasIndex(l => l.ProductId);
        b.Property(l => l.Description).HasMaxLength(220); // "{product} ({presentation})": 150 + 60 + 3
        b.Property(l => l.ItbisRate).HasConversion<string>().HasMaxLength(12);
        b.Property(l => l.ScaleLabel).HasMaxLength(20);
    }

    public void Configure(EntityTypeBuilder<SalePayment> b)
    {
        b.ToTable("SalePayments");
        b.Property(p => p.Method).HasConversion<string>().HasMaxLength(20);
    }

    public void Configure(EntityTypeBuilder<SaleReturn> b)
    {
        b.ToTable("Returns");
        b.HasIndex(r => r.SaleId);
        b.Property(r => r.Method).HasConversion<string>().HasMaxLength(20);
        b.Property(r => r.ENcf).HasMaxLength(13);
        b.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.ReturnId);
    }

    public void Configure(EntityTypeBuilder<ReturnLine> b)
    {
        b.ToTable("ReturnLines");
        b.HasKey(l => new { l.ReturnId, l.SaleLineId });
    }

    public void Configure(EntityTypeBuilder<HeldSale> b)
    {
        b.ToTable("HeldSales");
        b.HasIndex(h => h.ShiftId);
        b.Property(h => h.Content).HasMaxLength(ModuleDbContext.LongText);
    }

    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("Orders");
        b.HasIndex(o => new { o.Kind, o.PickupDate });
        b.HasIndex(o => new { o.Kind, o.CreatedUtc });
        b.Property(o => o.Kind).HasConversion<string>().HasMaxLength(10);
        b.Property(o => o.Status).HasConversion<string>().HasMaxLength(12);
        b.Property(o => o.DepositMethod).HasConversion<string>().HasMaxLength(20);
        b.Property(o => o.CustomerName).HasMaxLength(150);
        b.Property(o => o.Lines).HasMaxLength(ModuleDbContext.LongText);
        b.Property(o => o.Version).IsConcurrencyToken();
        b.Ignore(o => o.IsOpen);
    }

    public void Configure(EntityTypeBuilder<CatalogItem> b)
    {
        b.ToTable("CatalogItems");
        b.HasKey(i => i.ProductId);
    }

    public void Configure(EntityTypeBuilder<CatalogSettings> b)
    {
        b.ToTable("CatalogSettings");
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.WhatsApp).HasMaxLength(15);
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class SalesDesignTimeFactory : Toki.BuildingBlocks.Persistence.DesignTimeFactory<SalesDbContext>;
