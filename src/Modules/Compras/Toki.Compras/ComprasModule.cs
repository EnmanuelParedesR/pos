using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.Compras.Domain;

namespace Toki.Compras;

public static class ComprasModule
{
    public static IServiceCollection AddComprasModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<ComprasDbContext>();
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

    /// <summary>Open payables of a supplier with their balance, oldest due first.</summary>
    public async Task<List<(CuentaPorPagar Cuenta, decimal Saldo)>> AbiertasAsync(Guid suplidorId, CancellationToken ct)
    {
        var cuentas = await CuentasPorPagar.Where(c => c.SuplidorId == suplidorId && !c.Anulada).AsNoTracking().ToListAsync(ct);
        var ids = cuentas.Select(c => c.Id).ToList();
        var pagado = await Aplicaciones.Where(a => ids.Contains(a.CuentaId)).GroupBy(a => a.CuentaId)
            .Select(g => new { g.Key, Monto = g.Sum(a => a.Monto) }).ToDictionaryAsync(x => x.Key, x => x.Monto, ct);
        return cuentas.Select(c => (c, c.Total - pagado.GetValueOrDefault(c.Id))).Where(x => x.Item2 > 0)
            .OrderBy(x => x.c.Vence).ThenBy(x => x.c.Fecha).ToList();
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
        b.HasIndex(c => new { c.SuplidorId, c.Ncf });
        b.HasIndex(c => c.FechaFactura);
        b.Property(c => c.Ncf).HasMaxLength(13);
        b.Property(c => c.RncSuplidor).HasMaxLength(11);
        b.Property(c => c.Notas).HasMaxLength(500);
        b.Property(c => c.Estado).HasConversion<string>().HasMaxLength(12);
        b.Property(c => c.Condicion).HasConversion<string>().HasMaxLength(10);
        b.HasMany(c => c.Lineas).WithOne().HasForeignKey(l => l.CompraId).HasPrincipalKey(c => c.Id);
    }

    public void Configure(EntityTypeBuilder<LineaCompra> b)
    {
        b.ToTable("LineasCompra");
        b.Property(l => l.TasaItbis).HasConversion<string>().HasMaxLength(12);
    }

    public void Configure(EntityTypeBuilder<Gasto> b)
    {
        b.HasIndex(g => new { g.SuplidorId, g.Ncf });
        b.HasIndex(g => g.Fecha);
        b.Property(g => g.Ncf).HasMaxLength(13);
        b.Property(g => g.RncSuplidor).HasMaxLength(11);
        b.Property(g => g.Tipo).HasConversion<int>();
        b.Property(g => g.Condicion).HasConversion<string>().HasMaxLength(10);
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
