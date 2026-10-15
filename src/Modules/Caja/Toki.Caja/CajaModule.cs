using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Caja.Contracts;
using Toki.Caja.Domain;
using Toki.SharedKernel;

namespace Toki.Caja;

public static class CajaModule
{
    public static IServiceCollection AddCajaModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<CajaDbContext>();
        services.AddScoped<ICajaOperaciones, CajaOperaciones>();
        return services;
    }
}

internal sealed class CajaDbContext(DbContextOptions<CajaDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "caja";

    public DbSet<CajaFisica> Cajas => Set<CajaFisica>();
    public DbSet<Turno> Turnos => Set<Turno>();
    public DbSet<MovimientoCaja> Movimientos => Set<MovimientoCaja>();

    public Task<Turno?> TurnoAsync(Guid id, CancellationToken ct) => Turnos.FirstOrDefaultAsync(t => t.Id == id, ct);
}

internal sealed class CajaConfiguration : IEntityTypeConfiguration<CajaFisica>, IEntityTypeConfiguration<Turno>, IEntityTypeConfiguration<MovimientoCaja>
{
    public void Configure(EntityTypeBuilder<CajaFisica> b) => b.ToTable("CajasFisicas");

    public void Configure(EntityTypeBuilder<Turno> b)
    {
        b.Property(t => t.Estado).HasConversion<string>().HasMaxLength(10);
        b.Property(t => t.Conteo).HasMaxLength(ModuleDbContext.TextoLargo);
        b.Property(t => t.Version).IsConcurrencyToken();
        // One open shift per register, enforced by the database too.
        b.HasIndex(t => t.CajaId).IsUnique().HasFilter("[CerradoUtc] IS NULL").HasDatabaseName("IX_Turnos_CajaAbierta");
        b.HasIndex(t => t.AbiertoUtc);
    }

    public void Configure(EntityTypeBuilder<MovimientoCaja> b)
    {
        b.ToTable("MovimientosCaja");
        b.HasKey(m => m.Secuencia);
        b.Property(m => m.Secuencia).ValueGeneratedOnAdd();
        b.HasIndex(m => m.Id).IsUnique();
        b.HasIndex(m => m.TurnoId);
        b.HasIndex(m => m.ReferenciaId);
        b.Property(m => m.Tipo).HasConversion<string>().HasMaxLength(20);
        b.Property(m => m.Medio).HasConversion<string>().HasMaxLength(20);
        b.Property(m => m.Motivo).HasConversion<string>().HasMaxLength(20);
        b.Ignore(m => m.Entra);
    }
}

internal sealed class CajaOperaciones(CajaDbContext db, IUserContext usuario, IOutbox outbox, TimeProvider reloj) : ICajaOperaciones
{
    /// <remarks>Also locks the shift row until the caller's transaction ends: a closing cannot count cash while a sale on this shift is still committing.</remarks>
    public async Task<Result<TurnoAbierto>> ObtenerTurnoAbiertoAsync(Guid turnoId, CancellationToken ct)
    {
        await db.Turnos.Where(x => x.Id == turnoId && x.Estado == EstadoTurno.Abierto)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, x => x.Version + 1), ct);
        var t = await (from turno in db.Turnos
                       join caja in db.Cajas on turno.CajaId equals caja.Id
                       where turno.Id == turnoId
                       select new { turno, caja.Nombre }).AsNoTracking().FirstOrDefaultAsync(ct);
        if (t is null) return CajaErrors.TurnoNoEncontrado;
        if (t.turno.Estado != EstadoTurno.Abierto) return CajaErrors.TurnoCerrado;
        if (t.turno.CajeroId != usuario.UsuarioId && !usuario.Tiene(Permisos.CajaAprobarCierre)) return CajaErrors.TurnoAjeno;
        return new TurnoAbierto(t.turno.Id, t.turno.CajaId, t.Nombre, t.turno.CajeroId);
    }

    public Task RegistrarCobroAsync(Guid turnoId, Guid ventaId, IReadOnlyList<CobroMedio> pagos, CancellationToken ct)
    {
        foreach (var p in pagos.Where(p => p.Monto > 0))
            Agregar(turnoId, TipoMovimientoCaja.Cobro, p.Medio, p.Monto, ventaId, p.Referencia);
        return Task.CompletedTask;
    }

    public async Task<Result> RegistrarReembolsoAsync(Guid turnoId, Guid referenciaId, MedioPago medio, decimal monto, CancellationToken ct)
    {
        var turno = await ObtenerTurnoAbiertoAsync(turnoId, ct);
        if (turno.IsFailure) return turno.Error!;
        if (medio == MedioPago.Efectivo && await EfectivoEsperadoAsync(turnoId, ct) < monto) return CajaErrors.SinEfectivo;
        Agregar(turnoId, TipoMovimientoCaja.Reembolso, medio, monto, referenciaId, null);
        return Result.Success();
    }

    public async Task<Result> AnularCobrosAsync(Guid ventaId, CancellationToken ct)
    {
        var cobros = await db.Movimientos.Where(m => m.ReferenciaId == ventaId && m.Tipo == TipoMovimientoCaja.Cobro).AsNoTracking().ToListAsync(ct);
        if (cobros.Count == 0) return Result.Success();
        var turnoId = cobros[0].TurnoId;
        var turno = await db.TurnoAsync(turnoId, ct);
        if (turno is not { Estado: EstadoTurno.Abierto }) return CajaErrors.TurnoCerrado;
        foreach (var c in cobros) Agregar(turnoId, TipoMovimientoCaja.AnulacionCobro, c.Medio, c.Monto, ventaId, c.Referencia);
        return Result.Success();
    }

    public async Task<decimal> EfectivoEsperadoAsync(Guid turnoId, CancellationToken ct)
    {
        var turno = await db.Turnos.AsNoTracking().SingleAsync(t => t.Id == turnoId, ct);
        var movimientos = await db.Movimientos.Where(m => m.TurnoId == turnoId).AsNoTracking().ToListAsync(ct);
        movimientos.AddRange(db.Movimientos.Local.Where(m => m.TurnoId == turnoId && db.Entry(m).State == EntityState.Added));
        return CuadreTurno.EfectivoEsperado(turno.FondoInicial, movimientos);
    }

    public MovimientoCaja Agregar(Guid turnoId, TipoMovimientoCaja tipo, MedioPago medio, decimal monto, Guid? referenciaId, string? referencia,
        MotivoMovimiento? motivo = null, string? nota = null)
    {
        var m = new MovimientoCaja
        {
            Id = Guid.CreateVersion7(), TurnoId = turnoId, Tipo = tipo, Medio = medio, Monto = monto, ReferenciaId = referenciaId,
            Referencia = referencia, Motivo = motivo, Nota = nota, UsuarioId = usuario.UsuarioId == Guid.Empty ? null : usuario.UsuarioId,
            SupervisorId = usuario.SupervisorId, FechaUtc = reloj.GetUtcNow().UtcDateTime,
        };
        db.Movimientos.Add(m);
        outbox.Agregar(new MovimientoCajaRegistradoV1(turnoId, tipo.ToString(), medio, monto, referenciaId));
        return m;
    }
}
