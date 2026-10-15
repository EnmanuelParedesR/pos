using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Security;
using Toki.SharedKernel;

namespace Toki.BuildingBlocks.Persistence;

public interface IOutbox
{
    /// <summary>Saved with the command's transaction; nothing is published if the command fails.</summary>
    void Agregar(IIntegrationEvent evento);
}

public interface IAuditoria
{
    /// <summary>Appends a hash-chained record inside the command's transaction.</summary>
    Task RegistrarAsync(string accion, string recursoTipo, Guid recursoId, object datos, CancellationToken ct);
}

internal sealed class Outbox(InfraDbContext db, IUserContext usuario, TimeProvider reloj) : IOutbox
{
    public void Agregar(IIntegrationEvent evento)
    {
        evento.OcurridoUtc = reloj.GetUtcNow().UtcDateTime;
        db.Outbox.Add(new OutboxMensaje
        {
            Id = evento.EventId,
            Tipo = evento.GetType().FullName!,
            AgregadoId = evento.AgregadoId,
            Payload = JsonSerializer.Serialize(evento, evento.GetType(), Json.Opciones),
            OcurridoUtc = evento.OcurridoUtc,
            UsuarioId = usuario.UsuarioId == Guid.Empty ? null : usuario.UsuarioId,
            DispositivoId = usuario.DispositivoId,
        });
    }
}

internal sealed class Auditoria(InfraDbContext db, UnitOfWork uow, IUserContext usuario, TimeProvider reloj) : IAuditoria
{
    public async Task RegistrarAsync(string accion, string recursoTipo, Guid recursoId, object datos, CancellationToken ct)
    {
        // Outside a transaction the head update would commit alone and the record would be lost, breaking the chain.
        if (uow.Transaction is null) throw new InvalidOperationException("La auditoría solo se escribe dentro de un comando.");

        // Bumping the head first takes its write lock, so concurrent commands cannot fork the chain.
        await db.CabezaAuditoria.Where(c => c.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Secuencia, c => c.Secuencia + 1), ct);
        var cabeza = await db.CabezaAuditoria.AsNoTracking().SingleAsync(c => c.Id == 1, ct);

        var registro = new RegistroAuditoria
        {
            Secuencia = cabeza.Secuencia,
            Id = Guid.CreateVersion7(),
            FechaUtc = reloj.GetUtcNow().UtcDateTime,
            UsuarioId = usuario.UsuarioId == Guid.Empty ? null : usuario.UsuarioId,
            SupervisorId = usuario.SupervisorId,
            DispositivoId = usuario.DispositivoId,
            Accion = accion,
            RecursoTipo = recursoTipo,
            RecursoId = recursoId,
            Datos = JsonSerializer.Serialize(datos, Json.Opciones),
            HashPrevio = cabeza.Hash,
        };
        registro.Hash = CadenaAuditoria.Hash(registro);
        db.Auditoria.Add(registro);

        await db.CabezaAuditoria.Where(c => c.Id == 1)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Hash, registro.Hash), ct);
    }
}

public static class CadenaAuditoria
{
    public static byte[] Hash(RegistroAuditoria r)
    {
        var contenido = string.Join('|', r.Secuencia, r.Id, r.FechaUtc.ToString("O"), r.UsuarioId, r.SupervisorId,
            r.DispositivoId, r.Accion, r.RecursoTipo, r.RecursoId, r.Datos);
        return SHA256.HashData([.. r.HashPrevio, .. Encoding.UTF8.GetBytes(contenido)]);
    }

    /// <summary>Recomputes the chain. Returns the first broken sequence number, or null when intact.
    /// The chain must also end at the head row: deleting the newest records breaks it at the first missing number.</summary>
    public static async Task<long?> VerificarAsync(InfraDbContext db, CancellationToken ct)
    {
        var cabeza = await db.CabezaAuditoria.AsNoTracking().SingleAsync(c => c.Id == 1, ct);
        var previo = new byte[32];
        long ultima = 0;
        await foreach (var r in db.Auditoria.AsNoTracking().Where(r => r.Secuencia <= cabeza.Secuencia).OrderBy(r => r.Secuencia).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (r.Secuencia != ultima + 1 || !r.HashPrevio.SequenceEqual(previo) || !Hash(r).SequenceEqual(r.Hash)) return ultima + 1;
            (previo, ultima) = (r.Hash, r.Secuencia);
        }
        return ultima == cabeza.Secuencia && previo.SequenceEqual(cabeza.Hash) ? null : ultima + 1;
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
