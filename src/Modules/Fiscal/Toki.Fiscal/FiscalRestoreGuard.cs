using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Persistence;
using Toki.Fiscal.Domain;

namespace Toki.Fiscal;

/// <summary>R9 #3: a restored backup never hands out again an e-NCF or a B number already used. Each range keeps the higher of its next
/// number before and after the restore, and a range loaded after the backup comes back as it was.</summary>
internal sealed class FiscalRestoreGuard(FiscalDbContext db) : IRestoreGuard
{
    public async Task<RestoreSnapshot> CaptureAsync(CancellationToken ct)
    {
        var sequences = await db.Sequences.AsNoTracking().ToListAsync(ct);
        var paper = await db.Set<PaperSequence>().AsNoTracking().ToListAsync(ct);
        var signed = await db.Documents.Where(d => d.Environment != FiscalEnvironment.Simulated).MaxAsync(d => (DateTime?)d.SignedUtc, ct);
        var issued = await db.Set<PaperDocument>().MaxAsync(d => (DateTime?)d.IssuedUtc, ct);
        return new RestoreSnapshot(new[] { signed, issued }.Max(), async token =>
        {
            db.ChangeTracker.Clear();
            var restored = await db.Sequences.ToDictionaryAsync(s => s.Id, token);
            foreach (var s in sequences)
                if (restored.TryGetValue(s.Id, out var r)) r.Next = Math.Max(r.Next, s.Next);
                else db.Sequences.Add(s);
            var restoredPaper = await db.Set<PaperSequence>().ToDictionaryAsync(s => s.Id, token);
            foreach (var s in paper)
                if (restoredPaper.TryGetValue(s.Id, out var r)) r.Next = Math.Max(r.Next, s.Next);
                else db.Set<PaperSequence>().Add(s);
            await db.SaveChangesAsync(token);
        });
    }
}
