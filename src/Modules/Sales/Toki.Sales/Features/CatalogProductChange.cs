using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.Inventory.Contracts;

namespace Toki.Sales.Features;

/// <summary>R8 #17: a product on the public catalog renamed, moved or deactivated in Productos goes to the page at once, not when the owner saves
/// the catalog again. Running twice sends the same item twice (the cloud keeps the newest).</summary>
internal sealed class FollowProductChange(SalesDbContext db, ICatalogQueries catalog, IOutbox outbox, InfraDbContext infra) : IEventHandler<ProductChangedV1>
{
    public async Task HandleAsync(ProductChangedV1 e, CancellationToken ct)
    {
        if (!await db.CatalogItems.AnyAsync(i => i.ProductId == e.ProductId, ct)) return;
        foreach (var p in await catalog.GetForSaleAsync([(e.ProductId, null)], ct)) outbox.Add(PublicCatalog.ToEvent(p, shown: true));
        await infra.SaveChangesAsync(ct);
    }
}
