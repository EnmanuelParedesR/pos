namespace Abastalia.BuildingBlocks.Persistence;

/// <summary>R9 #3: what restoring a backup must never rewind, because it already left the store (e-NCF the DGII has, revoked registers).
/// Each module reads it before the restore and puts it back on the restored data.</summary>
public interface IRestoreGuard
{
    Task<RestoreSnapshot> CaptureAsync(CancellationToken ct);
}

/// <param name="LastIrreversibleUtc">The newest thing a backup older than it loses for good (an e-CF signed, a B receipt handed out); null if none.</param>
/// <param name="ReapplyAsync">Runs on the restored database, after its migrations.</param>
public sealed record RestoreSnapshot(DateTime? LastIrreversibleUtc, Func<CancellationToken, Task> ReapplyAsync);
