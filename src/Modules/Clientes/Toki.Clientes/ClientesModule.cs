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
        services.AddScoped<IClientesReportes, Features.ClientesReportes>();
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
        Clientes.Where(c => c.Id == clienteId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Version, c => c.Version + 1), ct);

    /// <summary>Open credit charges with their balance, oldest first: of one customer, or of all (aging). Paid charges stay in the database.</summary>
    public async Task<List<(CargoFiado Cargo, decimal Saldo)>> CargosAbiertosAsync(Guid? clienteId, CancellationToken ct)
    {
        var abiertos = await (from c in Cargos
                              where !c.Anulado && (clienteId == null || c.ClienteId == clienteId)
                              let pagado = (from a in Aplicaciones
                                            join ab in Abonos on a.AbonoId equals ab.Id
                                            where a.CargoId == c.Id && !ab.Anulado
                                            select (decimal?)a.Monto).Sum() ?? 0
                              where c.Monto > pagado
                              select new { c, Saldo = c.Monto - pagado }).AsNoTracking().ToListAsync(ct);
        return abiertos.OrderBy(x => x.c.FechaUtc).Select(x => (x.c, x.Saldo)).ToList();
    }

    /// <summary>What live payments have paid on one charge.</summary>
    public Task<decimal> PagadoAsync(Guid cargoId, CancellationToken ct) =>
        (from a in Aplicaciones join ab in Abonos on a.AbonoId equals ab.Id where a.CargoId == cargoId && !ab.Anulado select a.Monto).SumAsync(ct);

    public Task<decimal> SaldoFavorAsync(Guid clienteId, CancellationToken ct) =>
        MovimientosSaldo.Where(m => m.ClienteId == clienteId).SumAsync(m => m.Monto, ct);
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
        b.Property(a => a.Origen).HasConversion<string>().HasMaxLength(20);
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

        db.Cargos.Add(new CargoFiado
        {
            Id = Guid.CreateVersion7(), ClienteId = clienteId, VentaId = ventaId, Documento = documento, FechaUtc = reloj.GetUtcNow().UtcDateTime, Monto = monto,
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
        var clienteId = await db.Cargos.Where(c => c.VentaId == ventaId).Select(c => (Guid?)c.ClienteId)
            .Concat(db.MovimientosSaldo.Where(m => m.ReferenciaId == ventaId).Select(m => (Guid?)m.ClienteId)).FirstOrDefaultAsync(ct);
        if (clienteId is null) return;
        await db.BloquearAsync(clienteId.Value, ct);

        var cargo = await db.Cargos.FirstOrDefaultAsync(c => c.VentaId == ventaId && !c.Anulado, ct);
        if (cargo is not null)
        {
            cargo.PagadoAlAnular = await db.PagadoAsync(cargo.Id, ct);
            cargo.Anulado = true;
            cargo.AnuladoUtc = reloj.GetUtcNow().UtcDateTime;
            // What was already paid on the voided sale pays the customer's other debts first; only the rest becomes balance in favor.
            var resto = cargo.PagadoAlAnular - Abonar(clienteId.Value, OrigenAbono.AnulacionVenta, ventaId, cargo.PagadoAlAnular,
                (await db.CargosAbiertosAsync(clienteId, ct)).Where(x => x.Cargo.Id != cargo.Id));
            if (resto > 0) AgregarSaldo(clienteId.Value, TipoMovimientoSaldo.CargoAnuladoPagado, resto, ventaId);
        }
        var usado = await db.MovimientosSaldo.Where(m => m.ReferenciaId == ventaId && m.Tipo == TipoMovimientoSaldo.UsoEnVenta).ToListAsync(ct);
        foreach (var m in usado) AgregarSaldo(m.ClienteId, TipoMovimientoSaldo.AnulacionUso, -m.Monto, ventaId);
    }

    public async Task AcreditarDevolucionAsync(Guid clienteId, Guid ventaId, Guid devolucionId, decimal monto, CancellationToken ct)
    {
        await db.BloquearAsync(clienteId, ct);
        // That sale's own charge first, then the oldest other debts; only the rest becomes balance in favor.
        var abiertos = (await db.CargosAbiertosAsync(clienteId, ct)).OrderBy(x => x.Cargo.VentaId == ventaId ? 0 : 1).ToList();
        var resto = monto - Abonar(clienteId, OrigenAbono.Devolucion, devolucionId, monto, abiertos);
        if (resto > 0) AgregarSaldo(clienteId, TipoMovimientoSaldo.Devolucion, resto, devolucionId);
    }

    /// <summary>Applies an amount to open charges in the given order with one payment record. Returns what was applied.</summary>
    private decimal Abonar(Guid clienteId, OrigenAbono origen, Guid referenciaId, decimal monto, IEnumerable<(CargoFiado Cargo, decimal Saldo)> abiertos)
    {
        var asignado = Asignacion.EnOrden(monto, abiertos);
        if (asignado.Count == 0) return 0;
        var abono = new AbonoFiado
        {
            Id = Guid.CreateVersion7(), ClienteId = clienteId, FechaUtc = reloj.GetUtcNow().UtcDateTime, Monto = asignado.Sum(x => x.Monto), Origen = origen,
            ReferenciaId = referenciaId, UsuarioId = usuario.UsuarioId == Guid.Empty ? null : usuario.UsuarioId,
        };
        abono.Aplicaciones.AddRange(asignado.Select(x => new AplicacionAbono { AbonoId = abono.Id, CargoId = x.Item.Id, Monto = x.Monto }));
        db.Abonos.Add(abono);
        return abono.Monto;
    }

    private void AgregarSaldo(Guid clienteId, TipoMovimientoSaldo tipo, decimal monto, Guid referenciaId) =>
        db.MovimientosSaldo.Add(new MovimientoSaldo
        {
            Id = Guid.CreateVersion7(), ClienteId = clienteId, Tipo = tipo, Monto = monto, ReferenciaId = referenciaId, FechaUtc = reloj.GetUtcNow().UtcDateTime,
        });
}

/// <summary>For dotnet-ef: SQL Server migrations of this module.</summary>
internal sealed class DisenoClientes : Toki.BuildingBlocks.Persistence.DisenoDbContext<ClientesDbContext>;
