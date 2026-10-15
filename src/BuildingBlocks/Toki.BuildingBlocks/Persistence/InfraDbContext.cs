using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Toki.BuildingBlocks.Persistence;

/// <summary>Infrastructure tables: the outbox and the hash-chained audit trail. Modules write them only through IOutbox and IAuditoria.</summary>
public sealed class InfraDbContext(DbContextOptions<InfraDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "infra";

    public DbSet<OutboxMensaje> Outbox => Set<OutboxMensaje>();
    public DbSet<RegistroAuditoria> Auditoria => Set<RegistroAuditoria>();
    public DbSet<CabezaAuditoria> CabezaAuditoria => Set<CabezaAuditoria>();
}

public sealed class OutboxMensaje
{
    public long Secuencia { get; set; }
    public Guid Id { get; set; }
    public string Tipo { get; set; } = "";
    public Guid AgregadoId { get; set; }
    public string Payload { get; set; } = "";
    public DateTime OcurridoUtc { get; set; }
    public Guid? UsuarioId { get; set; }
    public Guid? DispositivoId { get; set; }
    public DateTime? ProcesadoUtc { get; set; }
    public DateTime? SubidoUtc { get; set; }
}

public sealed class RegistroAuditoria
{
    public long Secuencia { get; set; }
    public Guid Id { get; set; }
    public DateTime FechaUtc { get; set; }
    public Guid? UsuarioId { get; set; }
    public Guid? SupervisorId { get; set; }
    public Guid? DispositivoId { get; set; }
    public string Accion { get; set; } = "";
    public string RecursoTipo { get; set; } = "";
    public Guid RecursoId { get; set; }
    public string Datos { get; set; } = "";
    public byte[] HashPrevio { get; set; } = [];
    public byte[] Hash { get; set; } = [];
}

/// <summary>Single row with the head of the audit chain. Updating it first serializes audit writers.</summary>
public sealed class CabezaAuditoria
{
    public int Id { get; set; }
    public long Secuencia { get; set; }
    public byte[] Hash { get; set; } = [];
}

internal sealed class OutboxMensajeConfiguration : IEntityTypeConfiguration<OutboxMensaje>
{
    public void Configure(EntityTypeBuilder<OutboxMensaje> b)
    {
        b.ToTable("OutboxMensajes");
        b.HasKey(m => m.Secuencia);
        b.Property(m => m.Secuencia).ValueGeneratedOnAdd();
        b.HasIndex(m => m.Id).IsUnique();
        b.Property(m => m.Payload).HasMaxLength(ModuleDbContext.TextoLargo);
        b.HasIndex(m => m.ProcesadoUtc);
    }
}

internal sealed class RegistroAuditoriaConfiguration : IEntityTypeConfiguration<RegistroAuditoria>
{
    public void Configure(EntityTypeBuilder<RegistroAuditoria> b)
    {
        b.ToTable("AuditoriaRegistros");
        b.HasKey(r => r.Secuencia);
        b.Property(r => r.Secuencia).ValueGeneratedNever();
        b.Property(r => r.Datos).HasMaxLength(ModuleDbContext.TextoLargo);
        b.Property(r => r.HashPrevio).HasMaxLength(32);
        b.Property(r => r.Hash).HasMaxLength(32);
        b.HasIndex(r => new { r.RecursoTipo, r.RecursoId });
    }
}

internal sealed class CabezaAuditoriaConfiguration : IEntityTypeConfiguration<CabezaAuditoria>
{
    public void Configure(EntityTypeBuilder<CabezaAuditoria> b)
    {
        b.ToTable("AuditoriaCabeza");
        b.Property(c => c.Id).ValueGeneratedNever();
        b.Property(c => c.Hash).HasMaxLength(32);
        b.HasData(new CabezaAuditoria { Id = 1, Secuencia = 0, Hash = new byte[32] });
    }
}
