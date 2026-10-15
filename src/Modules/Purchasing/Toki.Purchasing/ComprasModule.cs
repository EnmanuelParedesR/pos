using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.Purchasing.Domain;

namespace Toki.Purchasing;

public static class ComprasModule
{
    public static IServiceCollection AddComprasModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<ComprasDbContext>();
        services.AddScoped<Contracts.IComprasReportes, Features.ComprasReportes>();
        return services;
    }
}

internal sealed class ComprasDbContext(DbContextOptions<ComprasDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "compras";

    public DbSet<Suplidor> Suplidores => Set<Suplidor>();
    public DbSet<Compra> Compras => Set<Compra>();
    public DbSet<Gasto> Gastos => Set<Gasto>();
    public DbSet<CuentaPorPagar> CuentasPorPagar => Set<CuentaPorPagar>();
    public DbSet<PagoSuplidor> Pagos => Set<PagoSuplidor>();
    public DbSet<AplicacionPago> Aplicaciones => Set<AplicacionPago>();

    /// <summary>Locks the supplier row until commit: payments and voids of one supplier run one at a time (no over-applied balance).</summary>
    public Task<int> BloquearSuplidorAsync(Guid id, CancellationToken ct) =>
        Suplidores.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DiasCredito, x => x.DiasCredito), ct);

    /// <summary>Locks the purchase row (until the transaction ends) and only then reads it with its lines. Null when it does not exist.</summary>
    public async Task<Compra?> BloquearCompraAsync(Guid id, CancellationToken ct)
    {
        await Compras.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Version, c => c.Version + 1), ct);
        return await Compras.Include(c => c.Lineas).FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    /// <summary>A payable with live (not voided) payments on it cannot be voided.</summary>
    public Task<bool> TienePagosAsync(Guid cuentaId, CancellationToken ct) =>
        (from a in Aplicaciones join p in Pagos on a.PagoId equals p.Id where a.CuentaId == cuentaId && !p.Anulado select a).AnyAsync(ct);

    /// <summary>Open payables with their balance, oldest due first: of one supplier, or of all (aging). Paid ones stay in the database.</summary>
    public async Task<List<(CuentaPorPagar Cuenta, decimal Saldo)>> AbiertasAsync(Guid? suplidorId, CancellationToken ct)
    {
        var abiertas = await (from c in CuentasPorPagar
                              where !c.Anulada && (suplidorId == null || c.SuplidorId == suplidorId)
                              let pagado = (from a in Aplicaciones
                                            join p in Pagos on a.PagoId equals p.Id
                                            where a.CuentaId == c.Id && !p.Anulado
                                            select (decimal?)a.Monto).Sum() ?? 0
                              where c.Total > pagado
                              select new { c, Saldo = c.Total - pagado }).AsNoTracking().ToListAsync(ct);
        return abiertas.OrderBy(x => x.c.Vence).ThenBy(x => x.c.Fecha).Select(x => (x.c, x.Saldo)).ToList();
    }
}

internal sealed class ComprasConfiguration :
    IEntityTypeConfiguration<Suplidor>, IEntityTypeConfiguration<Compra>, IEntityTypeConfiguration<LineaCompra>, IEntityTypeConfiguration<Gasto>,
    IEntityTypeConfiguration<CuentaPorPagar>, IEntityTypeConfiguration<PagoSuplidor>, IEntityTypeConfiguration<AplicacionPago>
{
    public void Configure(EntityTypeBuilder<Suplidor> b)
    {
        b.ToTable("Suplidores");
        b.Property(s => s.Rnc).HasMaxLength(11);
        b.HasIndex(s => s.Nombre);
    }

    public void Configure(EntityTypeBuilder<Compra> b)
    {
        b.HasKey(c => c.Numero);
        b.Property(c => c.Numero).ValueGeneratedOnAdd();
        b.HasAlternateKey(c => c.Id);
        // One live invoice per supplier NCF, enforced by the database too (a double click cannot create two drafts).
        b.HasIndex(c => new { c.SuplidorId, c.Ncf }).IsUnique().HasFilter("[Estado] <> 'Anulada'");
        b.Property(c => c.Version).IsConcurrencyToken();
        b.HasIndex(c => c.FechaFactura);
        b.Property(c => c.Ncf).HasMaxLength(13);
        b.Property(c => c.RncSuplidor).HasMaxLength(11);
        b.Property(c => c.Notas).HasMaxLength(500);
        b.Property(c => c.Estado).HasConversion<string>().HasMaxLength(12);
        b.Property(c => c.Condicion).HasConversion<string>().HasMaxLength(10);
        b.Property(c => c.Medio).HasConversion<string>().HasMaxLength(15);
        b.HasMany(c => c.Lineas).WithOne().HasForeignKey(l => l.CompraId).HasPrincipalKey(c => c.Id);
    }

    public void Configure(EntityTypeBuilder<LineaCompra> b)
    {
        b.ToTable("LineasCompra");
        b.Property(l => l.Descripcion).HasMaxLength(220); // "{product} ({presentation})": 150 + 60 + 3
        b.Property(l => l.TasaItbis).HasConversion<string>().HasMaxLength(12);
    }

    public void Configure(EntityTypeBuilder<Gasto> b)
    {
        b.HasIndex(g => new { g.SuplidorId, g.Ncf }).IsUnique().HasFilter("[Anulado] = 0");
        b.HasIndex(g => g.Fecha);
        b.Property(g => g.Ncf).HasMaxLength(13);
        b.Property(g => g.RncSuplidor).HasMaxLength(11);
        b.Property(g => g.Tipo).HasConversion<int>();
        b.Property(g => g.Condicion).HasConversion<string>().HasMaxLength(10);
        b.Property(g => g.Medio).HasConversion<string>().HasMaxLength(15);
    }

    public void Configure(EntityTypeBuilder<CuentaPorPagar> b)
    {
        b.ToTable("CuentasPorPagar");
        b.HasIndex(c => c.SuplidorId);
        b.HasIndex(c => c.DocumentoId);
        b.Property(c => c.Ncf).HasMaxLength(13);
        b.Property(c => c.Tipo).HasConversion<string>().HasMaxLength(10);
    }

    public void Configure(EntityTypeBuilder<PagoSuplidor> b)
    {
        b.ToTable("PagosSuplidor");
        b.HasIndex(p => p.SuplidorId);
        b.Property(p => p.Medio).HasConversion<string>().HasMaxLength(15);
        b.Property(p => p.Referencia).HasMaxLength(60);
        b.HasMany(p => p.Aplicaciones).WithOne().HasForeignKey(a => a.PagoId);
    }

    public void Configure(EntityTypeBuilder<AplicacionPago> b)
    {
        b.ToTable("AplicacionesPago");
        b.HasKey(a => new { a.PagoId, a.CuentaId });
        b.HasIndex(a => a.CuentaId);
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class DisenoCompras : Toki.BuildingBlocks.Persistence.DisenoDbContext<ComprasDbContext>;
