using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Persistence;
using Toki.Identity.Domain;
using Toki.Identity.Persistence;

namespace Toki.Identity;

/// <summary>R9 #3: a restored backup never brings back access that was taken away after it. A revoked register stays revoked and a register
/// paired after the backup stays paired (its certificate is still valid); a deactivated user stays inactive and a changed PIN stays changed;
/// a closed session stays closed; and no pairing code of before the restore can be used.</summary>
internal sealed class IdentityRestoreGuard(IdentityDbContext db, TimeProvider clock) : IRestoreGuard
{
    public async Task<RestoreSnapshot> CaptureAsync(CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
        var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.PinHash, u.IsActive }).ToListAsync(ct);
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
            foreach (var u in users)
            {
                if (!restoredUsers.TryGetValue(u.Id, out var r)) continue;
                r.PinHash = u.PinHash;
                r.IsActive &= u.IsActive;
            }
            await db.SaveChangesAsync(token);
            var at = clock.GetUtcNow().UtcDateTime;
            await db.Sessions.Where(s => closed.Contains(s.Id) && s.ClosedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.ClosedUtc, at), token);
            await db.PairingCodes.Where(p => p.UsedUtc == null).ExecuteUpdateAsync(p => p.SetProperty(x => x.UsedUtc, at), token);
        });
    }
}
