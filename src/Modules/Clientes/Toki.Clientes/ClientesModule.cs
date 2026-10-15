using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Clientes.Contracts;
using Toki.Clientes.Domain;
using Toki.SharedKernel;

namespace Toki.Clientes;

public static class ClientesModule
{
    public static IServiceCollection AddClientesModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<ClientesDbContext>();
        services.AddScoped<ICuentasClientes, CuentasClientes>();
        return services;
    }
}

internal sealed class ClientesDbContext(DbContextOptions<ClientesDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "clientes";

    public DbSet<Cliente> Clientes => Set<Cliente>();
    public DbSet<CargoFiado> Cargos => Set<CargoFiado>();
    public DbSet<AbonoFiado> Abonos => Set<AbonoFiado>();
    public DbSet<AplicacionAbono> Aplicaciones => Set<AplicacionAbono>();
    public DbSet<MovimientoSaldo> MovimientosSaldo => Set<MovimientoSaldo>();

    /// <summary>Locks the customer row until commit: limit checks, payments and balance use run one at a time per customer.</summary>
    public Task<int> BloquearAsync(Guid clienteId, CancellationToken ct) =>
        Clientes.Where(c => c.Id == clienteId).ExecuteUpdateAsync(s => s.SetProperty(c => c.DiasCredito, c => c.DiasCredito), ct);

    /// <summary>Open credit charges with their balance, oldest first.</summary>
    public async Task<List<(CargoFiado Cargo, decimal Saldo)>> CargosAbiertosAsync(Guid clienteId, CancellationToken ct)
    {
        var cargos = await Cargos.Where(c => c.ClienteId == clienteId && !c.Anulado).AsNoTracking().ToListAsync(ct);
        var ids = cargos.Select(c => c.Id).ToList();
        var pagado = await Aplicaciones.Where(a => ids.Contains(a.CargoId)).GroupBy(a => a.CargoId)
            .Select(g => new { g.Key, Monto = g.Sum(a => a.Monto) }).ToDictionaryAsync(x => x.Key, x => x.Monto, ct);
        pagado = Aplicaciones.Local.Where(a => ids.Contains(a.CargoId)).Aggregate(pagado, (d, a) => { d[a.CargoId] = d.GetValueOrDefault(a.CargoId) + a.Monto; return d; });
        return cargos.Select(c => (c, c.Monto - pagado.GetValueOrDefault(c.Id))).Where(x => x.Item2 > 0)
            .OrderBy(x => x.c.FechaUtc).ToList();
    }

    public async Task<decimal> SaldoFavorAsync(Guid clienteId, CancellationToken ct) =>
        await MovimientosSaldo.Where(m => m.ClienteId == clienteId).SumAsync(m => m.Monto, ct)
        + MovimientosSaldo.Local.Where(m => m.ClienteId == clienteId && Entry(m).State == EntityState.Added).Sum(m => m.Monto);
}

internal sealed class ClientesConfiguration :
    IEntityTypeConfiguration<Cliente>, IEntityTypeConfiguration<CargoFiado>, IEntityTypeConfiguration<AbonoFiado>,
    IEntityTypeConfiguration<AplicacionAbono>, IEntityTypeConfiguration<MovimientoSaldo>
{
    public void Configure(EntityTypeBuilder<Cliente> b)
    {
        b.Property(c => c.Nombre).HasMaxLength(150);
        b.HasIndex(c => c.Nombre);
        b.Property(c => c.RncCedula).HasMaxLength(11);
        b.HasIndex(c => c.RncCedula).IsUnique().HasFilter("[RncCedula] IS NOT NULL");
        b.Property(c => c.Telefono).HasMaxLength(30);
        b.Property(c => c.Nivel).HasConversion<string>().HasMaxLength(10);
    }

    public void Configure(EntityTypeBuilder<CargoFiado> b)
    {
        b.ToTable("CargosFiado");
        b.HasIndex(c => c.ClienteId);
        b.HasIndex(c => c.VentaId);
        b.Property(c => c.Documento).HasMaxLength(13);
    }

    public void Configure(EntityTypeBuilder<AbonoFiado> b)
    {
        b.ToTable("AbonosFiado");
        b.HasIndex(a => a.ClienteId);
        b.Property(a => a.Origen).HasConversion<string>().HasMaxLength(12);
        b.Property(a => a.Medio).HasConversion<string>().HasMaxLength(20);
        b.Property(a => a.Referencia).HasMaxLength(60);
        b.HasMany(a => a.Aplicaciones).WithOne().HasForeignKey(x => x.AbonoId);
    }

    public void Configure(EntityTypeBuilder<AplicacionAbono> b)
    {
        b.ToTable("AplicacionesAbono");
        b.HasKey(a => new { a.AbonoId, a.CargoId });
        b.HasIndex(a => a.CargoId);
    }

    public void Configure(EntityTypeBuilder<MovimientoSaldo> b)
    {
        b.ToTable("MovimientosSaldoFavor");
        b.HasKey(m => m.Secuencia);
        b.Property(m => m.Secuencia).ValueGeneratedOnAdd();
        b.HasIndex(m => m.ClienteId);
        b.HasIndex(m => m.ReferenciaId);
        b.Property(m => m.Tipo).HasConversion<string>().HasMaxLength(20);
    }
}

internal sealed class CuentasClientes(ClientesDbContext db, IUserContext usuario, IOutbox outbox, TimeProvider reloj) : ICuentasClientes
{
    public async Task<ClienteParaVenta?> ObtenerAsync(Guid clienteId, CancellationToken ct)
    {
        var c = await db.Clientes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == clienteId, ct);
        if (c is null) return null;
        var deuda = (await db.CargosAbiertosAsync(clienteId, ct)).Sum(x => x.Saldo);
        return new ClienteParaVenta(c.Id, c.Nombre, c.RncCedula, c.CreditoFiscal, c.Nivel, c.LimiteCredito, deuda, await db.SaldoFavorAsync(clienteId, ct), c.Activo);
    }

    public async Task<Result> CargarFiadoAsync(Guid clienteId, Guid ventaId, string documento, decimal monto, bool permitirExceso, CancellationToken ct)
    {
        if (await db.BloquearAsync(clienteId, ct) == 0) return ClientesErrors.ClienteNoEncontrado;
        var cliente = await db.Clientes.AsNoTracking().SingleAsync(c => c.Id == clienteId, ct);
        if (!cliente.Activo) return ClientesErrors.ClienteNoEncontrado;
        var deuda = (await db.CargosAbiertosAsync(clienteId, ct)).Sum(x => x.Saldo);
        if (!permitirExceso && deuda + monto > cliente.LimiteCredito) return ClientesErrors.LimiteExcedido(Math.Max(0, cliente.LimiteCredito - deuda));

        var ahora = reloj.GetUtcNow().UtcDateTime;
        db.Cargos.Add(new CargoFiado
        {
            Id = Guid.CreateVersion7(), ClienteId = clienteId, VentaId = ventaId, Documento = documento, FechaUtc = ahora,
            Vence = DiaNegocio.De(ahora).AddDays(cliente.DiasCredito), Monto = monto,
        });
        outbox.Agregar(new CargoFiadoRegistradoV1(clienteId, ventaId, monto));
        return Result.Success();
    }

    public async Task<Result> UsarSaldoFavorAsync(Guid clienteId, Guid ventaId, decimal monto, CancellationToken ct)
    {
        if (await db.BloquearAsync(clienteId, ct) == 0) return ClientesErrors.ClienteNoEncontrado;
        if (await db.SaldoFavorAsync(clienteId, ct) < monto) return ClientesErrors.SinSaldoFavor;
        AgregarSaldo(clienteId, TipoMovimientoSaldo.UsoEnVenta, -monto, ventaId);
        return Result.Success();
    }

    public async Task AnularVentaAsync(Guid ventaId, CancellationToken ct)
    {
        var cargo = await db.Cargos.FirstOrDefaultAsync(c => c.VentaId == ventaId && !c.Anulado, ct);
        if (cargo is not null)
        {
            await db.BloquearAsync(cargo.ClienteId, ct);
            cargo.Anulado = true;
            var pagado = await db.Aplicaciones.Where(a => a.CargoId == cargo.Id).SumAsync(a => a.Monto, ct);
            if (pagado > 0) AgregarSaldo(cargo.ClienteId, TipoMovimientoSaldo.CargoAnuladoPagado, pagado, ventaId);
        }
        var usado = await db.MovimientosSaldo.Where(m => m.ReferenciaId == ventaId && m.Tipo == TipoMovimientoSaldo.UsoEnVenta).ToListAsync(ct);
        foreach (var m in usado) AgregarSaldo(m.ClienteId, TipoMovimientoSaldo.AnulacionUso, -m.Monto, ventaId);
    }

    public async Task AcreditarDevolucionAsync(Guid clienteId, Guid ventaId, Guid devolucionId, decimal monto, CancellationToken ct)
    {
        await db.BloquearAsync(clienteId, ct);
        var resto = monto;
        var abierto = (await db.CargosAbiertosAsync(clienteId, ct)).FirstOrDefault(x => x.Cargo.VentaId == ventaId);
        if (abierto.Cargo is not null)
        {
            var aplicado = Math.Min(resto, abierto.Saldo);
            resto -= aplicado;
            var abono = new AbonoFiado
            {
                Id = Guid.CreateVersion7(), ClienteId = clienteId, FechaUtc = reloj.GetUtcNow().UtcDateTime, Monto = aplicado, Origen = OrigenAbono.Devolucion,
                ReferenciaId = devolucionId, UsuarioId = usuario.UsuarioId == Guid.Empty ? null : usuario.UsuarioId,
            };
            abono.Aplicaciones.Add(new AplicacionAbono { AbonoId = abono.Id, CargoId = abierto.Cargo.Id, Monto = aplicado });
            db.Abonos.Add(abono);
        }
        if (resto > 0) AgregarSaldo(clienteId, TipoMovimientoSaldo.Devolucion, resto, devolucionId);
    }

    private void AgregarSaldo(Guid clienteId, TipoMovimientoSaldo tipo, decimal monto, Guid referenciaId) =>
        db.MovimientosSaldo.Add(new MovimientoSaldo
        {
            Id = Guid.CreateVersion7(), ClienteId = clienteId, Tipo = tipo, Monto = monto, ReferenciaId = referenciaId, FechaUtc = reloj.GetUtcNow().UtcDateTime,
        });
}
