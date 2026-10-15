using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Toki.BuildingBlocks.Persistence;
using Toki.Ventas.Domain;

namespace Toki.Ventas;

internal sealed class VentasDbContext(DbContextOptions<VentasDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "ventas";

    public DbSet<Venta> Ventas => Set<Venta>();
    public DbSet<LineaVenta> Lineas => Set<LineaVenta>();
    public DbSet<PagoVenta> Pagos => Set<PagoVenta>();
    public DbSet<Devolucion> Devoluciones => Set<Devolucion>();
    public DbSet<VentaEnEspera> EnEspera => Set<VentaEnEspera>();

    public Task<Venta?> VentaCompletaAsync(Guid id, CancellationToken ct) =>
        Ventas.Include(v => v.Lineas.OrderBy(l => l.Orden)).Include(v => v.Pagos).FirstOrDefaultAsync(v => v.Id == id, ct);

    /// <summary>Locks the sale row (until the transaction ends) and only then reads it, so the checks see committed data.</summary>
    public async Task<Venta?> BloquearVentaAsync(Guid id, CancellationToken ct)
    {
        await Ventas.Where(v => v.Id == id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Version, v => v.Version + 1), ct);
        return await VentaCompletaAsync(id, ct);
    }
}

internal sealed class VentasConfiguration :
    IEntityTypeConfiguration<Venta>, IEntityTypeConfiguration<LineaVenta>, IEntityTypeConfiguration<PagoVenta>,
    IEntityTypeConfiguration<Devolucion>, IEntityTypeConfiguration<LineaDevolucion>, IEntityTypeConfiguration<VentaEnEspera>
{
    public void Configure(EntityTypeBuilder<Venta> b)
    {
        // Numero is the identity key (Sqlite only auto-increments the primary key); Id is the business key.
        b.HasKey(v => v.Numero);
        b.Property(v => v.Numero).ValueGeneratedOnAdd();
        b.HasAlternateKey(v => v.Id);
        b.HasIndex(v => v.TurnoId);
        b.HasIndex(v => v.FechaUtc);
        b.HasIndex(v => v.ClienteId);
        b.Property(v => v.ENcf).HasMaxLength(13);
        b.Property(v => v.RncCliente).HasMaxLength(11);
        b.Property(v => v.UrlQr).HasMaxLength(1000);
        b.Property(v => v.TipoComprobante).HasConversion<int>();
        b.Property(v => v.Nivel).HasConversion<string>().HasMaxLength(10);
        b.Property(v => v.Estado).HasConversion<string>().HasMaxLength(12);
        b.Property(v => v.Version).IsConcurrencyToken();
        b.HasMany(v => v.Lineas).WithOne().HasForeignKey(l => l.VentaId).HasPrincipalKey(v => v.Id);
        b.HasMany(v => v.Pagos).WithOne().HasForeignKey(p => p.VentaId).HasPrincipalKey(v => v.Id);
    }

    public void Configure(EntityTypeBuilder<LineaVenta> b)
    {
        b.ToTable("LineasVenta");
        b.HasIndex(l => l.ProductoId);
        b.Property(l => l.Descripcion).HasMaxLength(220); // "{product} ({presentation})": 150 + 60 + 3
        b.Property(l => l.TasaItbis).HasConversion<string>().HasMaxLength(12);
        b.Property(l => l.CodigoEtiqueta).HasMaxLength(20);
    }

    public void Configure(EntityTypeBuilder<PagoVenta> b)
    {
        b.ToTable("PagosVenta");
        b.Property(p => p.Medio).HasConversion<string>().HasMaxLength(20);
    }

    public void Configure(EntityTypeBuilder<Devolucion> b)
    {
        b.ToTable("Devoluciones");
        b.HasIndex(d => d.VentaId);
        b.Property(d => d.Medio).HasConversion<string>().HasMaxLength(20);
        b.Property(d => d.ENcf).HasMaxLength(13);
        b.HasMany(d => d.Lineas).WithOne().HasForeignKey(l => l.DevolucionId);
    }

    public void Configure(EntityTypeBuilder<LineaDevolucion> b)
    {
        b.ToTable("LineasDevolucion");
        b.HasKey(l => new { l.DevolucionId, l.LineaVentaId });
    }

    public void Configure(EntityTypeBuilder<VentaEnEspera> b)
    {
        b.ToTable("VentasEnEspera");
        b.HasIndex(v => v.TurnoId);
        b.Property(v => v.Contenido).HasMaxLength(ModuleDbContext.TextoLargo);
    }
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class DisenoVentas : Toki.BuildingBlocks.Persistence.DisenoDbContext<VentasDbContext>;
