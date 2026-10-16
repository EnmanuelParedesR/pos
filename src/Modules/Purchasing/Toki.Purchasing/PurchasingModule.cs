using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.Purchasing.Domain;

namespace Toki.Purchasing;

public static class PurchasingModule
{
    public static IServiceCollection AddPurchasingModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<PurchasingDbContext>();
        services.AddScoped<Contracts.IPurchasingReports, Features.PurchasingReports>();
        return services;
    }
}

internal sealed class PurchasingDbContext(DbContextOptions<PurchasingDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "purchasing";

    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<Payable> Payables => Set<Payable>();
    public DbSet<SupplierPayment> Payments => Set<SupplierPayment>();
    public DbSet<PaymentAllocation> Allocations => Set<PaymentAllocation>();

    /// <summary>Locks the supplier row until commit: payments and voids of one supplier run one at a time (no over-allocated balance).</summary>
    public Task<int> LockSupplierAsync(Guid id, CancellationToken ct) =>
        Suppliers.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreditDays, x => x.CreditDays), ct);

    /// <summary>Locks the purchase row (until the transaction ends) and only then reads it with its lines. Null when it does not exist.</summary>
    public async Task<Purchase?> LockPurchaseAsync(Guid id, CancellationToken ct)
    {
        await Purchases.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Version, p => p.Version + 1), ct);
        return await Purchases.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    /// <summary>A payable with live (not voided) payments on it cannot be voided.</summary>
    public Task<bool> HasPaymentsAsync(Guid payableId, CancellationToken ct) =>
        (from a in Allocations join p in Payments on a.SupplierPaymentId equals p.Id where a.PayableId == payableId && !p.IsVoided select a).AnyAsync(ct);

    /// <summary>Open payables with their balance, oldest due first: of one supplier, or of all (aging). Paid ones stay in the database.</summary>
    public async Task<List<(Payable Payable, decimal Balance)>> OpenPayablesAsync(Guid? supplierId, CancellationToken ct)
    {
        var open = await (from c in Payables
                          where !c.IsVoided && (supplierId == null || c.SupplierId == supplierId)
                          let paid = (from a in Allocations
                                      join p in Payments on a.SupplierPaymentId equals p.Id
                                      where a.PayableId == c.Id && !p.IsVoided
                                      select (decimal?)a.Amount).Sum() ?? 0
                          where c.Total > paid
                          select new { c, Balance = c.Total - paid }).AsNoTracking().ToListAsync(ct);
        return open.OrderBy(x => x.c.DueDate).ThenBy(x => x.c.Date).Select(x => (x.c, x.Balance)).ToList();
    }
}

internal sealed class PurchasingConfiguration :
    IEntityTypeConfiguration<Supplier>, IEntityTypeConfiguration<Purchase>, IEntityTypeConfiguration<PurchaseLine>, IEntityTypeConfiguration<Expense>,
    IEntityTypeConfiguration<Payable>, IEntityTypeConfiguration<SupplierPayment>, IEntityTypeConfiguration<PaymentAllocation>, IEntityTypeConfiguration<PurchaseFile>
{
    public void Configure(EntityTypeBuilder<PurchaseFile> b)
    {
        b.ToTable("PurchaseFiles");
        b.HasKey(f => f.PurchaseId);
        b.Property(f => f.ContentType).HasMaxLength(40);
    }

    public void Configure(EntityTypeBuilder<Supplier> b)
    {
        b.ToTable("Suppliers");
        b.Property(s => s.Rnc).HasMaxLength(11);
        b.HasIndex(s => s.Name);
    }

    public void Configure(EntityTypeBuilder<Purchase> b)
    {
        b.HasKey(p => p.Number);
        b.Property(p => p.Number).ValueGeneratedOnAdd();
        b.HasAlternateKey(p => p.Id);
        // One live invoice per supplier NCF, enforced by the database too (a double click cannot create two drafts).
        // A draft from the suggested order (TK-191) has no NCF yet: those do not collide.
        b.HasIndex(p => new { p.SupplierId, p.Ncf }).IsUnique().HasFilter("[Status] <> 'Voided' AND [Ncf] <> ''");
        b.Property(p => p.Version).IsConcurrencyToken();
        b.HasIndex(p => p.InvoiceDate);
        b.Property(p => p.Ncf).HasMaxLength(13);
        b.Property(p => p.SupplierRnc).HasMaxLength(11);
        b.Property(p => p.Notes).HasMaxLength(500);
        b.Property(p => p.AttachmentName).HasMaxLength(200);
        b.Property(p => p.Status).HasConversion<string>().HasMaxLength(12);
        b.Property(p => p.Terms).HasConversion<string>().HasMaxLength(10);
        b.Property(p => p.Method).HasConversion<string>().HasMaxLength(15);
        b.HasMany(p => p.Lines).WithOne().HasForeignKey(l => l.PurchaseId).HasPrincipalKey(p => p.Id);
    }

    public void Configure(EntityTypeBuilder<PurchaseLine> b)
    {
        b.ToTable("PurchaseLines");
        b.Property(l => l.Description).HasMaxLength(220); // "{product} ({presentation})": 150 + 60 + 3
        b.Property(l => l.ItbisRate).HasConversion<string>().HasMaxLength(12);
    }

    public void Configure(EntityTypeBuilder<Expense> b)
    {
        b.HasIndex(e => new { e.SupplierId, e.Ncf }).IsUnique().HasFilter("[IsVoided] = 0");
        b.HasIndex(e => e.Date);
        b.Property(e => e.Ncf).HasMaxLength(13);
        b.Property(e => e.SupplierRnc).HasMaxLength(11);
        b.Property(e => e.Type).HasConversion<int>();
        b.Property(e => e.Terms).HasConversion<string>().HasMaxLength(10);
        b.Property(e => e.Method).HasConversion<string>().HasMaxLength(15);
    }

    public void Configure(EntityTypeBuilder<Payable> b)
    {
        b.ToTable("Payables");
        b.HasIndex(c => c.SupplierId);
        b.HasIndex(c => c.DocumentId);
        b.Property(c => c.Ncf).HasMaxLength(13);
        b.Property(c => c.Type).HasConversion<string>().HasMaxLength(10);
    }

    public void Configure(EntityTypeBuilder<SupplierPayment> b)
    {
        b.ToTable("SupplierPayments");
        b.HasIndex(p => p.SupplierId);
        b.Property(p => p.Method).HasConversion<string>().HasMaxLength(15);
        b.Property(p => p.Reference).HasMaxLength(60);
        b.HasMany(p => p.Allocations).WithOne().HasForeignKey(a => a.SupplierPaymentId);
    }

    public void Configure(EntityTypeBuilder<PaymentAllocation> b)
    {
        b.ToTable("PaymentAllocations");
        b.HasKey(a => new { a.SupplierPaymentId, a.PayableId });
        b.HasIndex(a => a.PayableId);
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class PurchasingDesignTimeFactory : Toki.BuildingBlocks.Persistence.DesignTimeFactory<PurchasingDbContext>;
