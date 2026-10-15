using Microsoft.EntityFrameworkCore;
using Toki.Inventory.Contracts;
using Toki.Inventory.Persistence;
using Toki.SharedKernel;

namespace Toki.Inventory;

internal sealed class InventoryReports(InventoryDbContext db) : IInventoryReports
{
    public async Task<IReadOnlyList<InventoryValue>> ValueAsync(CancellationToken ct)
    {
        var stock = await db.Stock.GroupBy(e => e.ProductId).Select(g => new { g.Key, Quantity = g.Sum(e => e.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Quantity, ct);
        var products = await (from p in db.Products
                              join c in db.Categories on p.CategoryId equals c.Id
                              select new { p.Id, p.Name, p.CategoryId, Category = c.Name, p.Unit, p.IsActive, p.Cost, p.Price }).AsNoTracking().ToListAsync(ct);
        return products.Where(p => p.IsActive || stock.GetValueOrDefault(p.Id) != 0).Select(p =>
        {
            var onHand = stock.GetValueOrDefault(p.Id);
            return new InventoryValue(p.Id, p.Name, p.CategoryId, p.Category, p.Unit, p.IsActive, onHand, p.Cost,
                Money.Round(onHand * p.Cost), p.Price, Money.Round(onHand * p.Price));
        }).OrderByDescending(v => v.CostValue).ThenBy(v => v.Product, StringComparer.CurrentCulture).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> CategoriesAsync(CancellationToken ct) =>
        await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
}
