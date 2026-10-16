using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Identity.Domain;
using Abastalia.Identity.Persistence;

namespace Abastalia.Identity;

/// <summary>R9 #3: a restored backup never brings back access that was taken away after it. A revoked register stays revoked and a register
/// paired after the backup stays paired (its certificate is still valid); a deactivated user stays inactive and a changed PIN stays changed;
/// a closed session stays closed; and no pairing code of before the restore can be used.</summary>
/// <remarks>R11 A3: PIN hashes are peppered with a secret of this server that is not in backups. A backup restored on another PC (a new main PC)
/// has hashes nobody can sign in with there; then the users of before the restore (whoever installed the new PC) are kept, taking over a
/// restored user with the same code, so the owner can sign in and give everyone else a new PIN.</remarks>
internal sealed class IdentityRestoreGuard(IdentityDbContext db, PinPepper pepper, TimeProvider clock) : IRestoreGuard
{
    public async Task<RestoreSnapshot> CaptureAsync(CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
        var users = await db.Users.AsNoTracking().ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var closed = await db.Sessions.Where(s => s.ClosedUtc != null && s.ExpiresUtc > now).Select(s => s.Id).ToListAsync(ct);
        return new RestoreSnapshot(null, async token =>
        {
            db.ChangeTracker.Clear();
            var restored = await db.Devices.ToDictionaryAsync(d => d.Id, token);
            foreach (var d in devices)
                if (restored.TryGetValue(d.Id, out var r)) r.IsRevoked |= d.IsRevoked;
                else db.Devices.Add(d);
            var restoredUsers = await db.Users.ToDictionaryAsync(u => u.Id, token);
            var foreign = restoredUsers.Values.Any(u => Pin.IsForeign(u.PinHash, pepper.Value));
            foreach (var u in users)
            {
                if (restoredUsers.TryGetValue(u.Id, out var r))
                {
                    r.PinHash = u.PinHash;
                    r.IsActive &= u.IsActive;
                }
                else if (!foreign) continue;
                else if (restoredUsers.Values.FirstOrDefault(x => x.Code == u.Code) is { } same)
                    (same.PinHash, same.FailedAttempts, same.LockedUntilUtc, same.IsActive) = (u.PinHash, 0, null, true);
                else db.Users.Add(u);
            }
            await db.SaveChangesAsync(token);
            var at = clock.GetUtcNow().UtcDateTime;
            await db.Sessions.Where(s => closed.Contains(s.Id) && s.ClosedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ClosedUtc, at), token);
            await db.PairingCodes.Where(p => p.UsedUtc == null).ExecuteUpdateAsync(p => p.SetProperty(x => x.UsedUtc, at), token);
        });
    }
}
