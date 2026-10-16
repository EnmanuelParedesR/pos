using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.Fiscal.Contracts;

namespace Abastalia.Sales.Features;

/// <summary>TK-147: when the DGII rejects an e-CF, Fiscal issues a replacement with a new e-NCF. The sale, void or return that had the rejected
/// number takes the new one, so the reprinted receipt and the 607 show the valid e-CF.</summary>
internal sealed class FollowFiscalReplacement(SalesDbContext db) : IEventHandler<FiscalDocumentReplacedV1>
{
    public async Task HandleAsync(FiscalDocumentReplacedV1 e, CancellationToken ct)
    {
        await db.Sales.Where(s => s.ENcf == e.RejectedENcf).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.ENcf, e.ENcf).SetProperty(x => x.SecurityCode, e.SecurityCode).SetProperty(x => x.QrUrl, e.QrUrl)
            .SetProperty(x => x.SignedUtc, e.SignedUtc).SetProperty(x => x.SequenceExpiresOn, e.SequenceExpiresOn).SetProperty(x => x.Contingency, false), ct);
        await db.Sales.Where(s => s.VoidENcf == e.RejectedENcf).ExecuteUpdateAsync(s => s.SetProperty(x => x.VoidENcf, e.ENcf), ct);
        await db.Returns.Where(r => r.ENcf == e.RejectedENcf).ExecuteUpdateAsync(r => r.SetProperty(x => x.ENcf, e.ENcf), ct);
    }
}
