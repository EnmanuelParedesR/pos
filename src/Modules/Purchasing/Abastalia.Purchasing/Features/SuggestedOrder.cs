using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Inventory.Contracts;
using Abastalia.Purchasing.Domain;
using Abastalia.SharedKernel;

namespace Abastalia.Purchasing.Features;

/// <summary>TK-191: what to buy. Products at or under their minimum, with the quantity that brings them up to their maximum (or back to the minimum
/// when there is none), grouped by the supplier they were last bought from (posted purchases, active suppliers) with that purchase's cost.
/// Ordered in the presentation of that last purchase (3 boxes of 12) while it is still active; otherwise in base units.
/// The screen turns each group into a purchase draft (no NCF until the invoice arrives).</summary>
/// <remarks>shortcut: last purchase found in memory over the posted lines of these products.</remarks>
internal static class SuggestedOrder
{
    /// <param name="Suggested">Base units: <see cref="Quantity"/> × <see cref="Factor"/>.</param>
    /// <param name="LastCost">Per ordered unit (the box when there is a presentation), without ITBIS, from the last purchase; null if it was never bought.</param>
    /// <param name="Presentation">Name of the presentation to order in ("Caja 12"); null in base units (pounds or units).</param>
    /// <param name="Quantity">In the presentation (or base units without one), rounded up to whole ones.</param>
    public sealed record Line(Guid ProductId, string Product, UnitOfMeasure Unit, ItbisRate ItbisRate, decimal Stock, decimal MinimumStock, decimal MaximumStock,
        decimal Suggested, decimal? LastCost, Guid? PresentationId, string? Presentation, decimal Factor, decimal Quantity);

    /// <param name="SupplierId">Null for products never bought from an active supplier.</param>
    /// <param name="Subtotal">Null, like every <see cref="Line.LastCost"/>, without costs.view (R8 #16, TK-201).</param>
    public sealed record Group(Guid? SupplierId, string? Supplier, IReadOnlyList<Line> Lines, decimal? Subtotal);

    /// <summary>Up to the maximum (or the minimum without one), never less than one whole pound or unit.</summary>
    public static decimal Suggest(decimal stock, decimal minimum, decimal maximum) => Math.Max(1, Math.Ceiling((maximum > 0 ? maximum : minimum) - stock));

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/purchasing/suggested-order", async (ICatalogQueries catalog, PurchasingDbContext db, IUserContext user, CancellationToken ct) =>
                {
                    var costs = user.Has(Permissions.CostsView);
                    var products = await catalog.BelowMinimumAsync(ct);
                    var ids = products.Select(p => p.ProductId).ToList();
                    var history = await (from l in db.Set<PurchaseLine>()
                                         join p in db.Purchases on l.PurchaseId equals p.Id
                                         join s in db.Suppliers on p.SupplierId equals s.Id
                                         where p.Status == PurchaseStatus.Posted && s.IsActive && ids.Contains(l.ProductId)
                                         select new { l.ProductId, l.PresentationId, p.SupplierId, Supplier = s.Name, p.InvoiceDate, p.Number, l.UnitCost, l.Factor })
                        .AsNoTracking().ToListAsync(ct);
                    var last = history.GroupBy(h => h.ProductId).ToDictionary(g => g.Key, g => g.MaxBy(h => (h.InvoiceDate, h.Number))!);
                    var presentations = (await catalog.GetForSaleAsync(
                            last.Values.Where(h => h.PresentationId is not null).Select(h => (h.ProductId, h.PresentationId)).ToList(), ct))
                        .Where(p => p.IsActive && p.Factor > 0).ToDictionary(p => p.ProductId);

                    var groups = products.GroupBy(p => last.TryGetValue(p.ProductId, out var h) ? (Guid?)h.SupplierId : null).Select(g =>
                    {
                        var lines = g.Select(p =>
                        {
                            var units = Suggest(p.Stock, p.MinimumStock, p.MaximumStock);
                            last.TryGetValue(p.ProductId, out var h);
                            if (h is not null && presentations.TryGetValue(p.ProductId, out var pr))
                            {
                                var boxes = Math.Ceiling(units / pr.Factor);
                                return new Line(p.ProductId, p.Name, p.Unit, p.ItbisRate, p.Stock, p.MinimumStock, p.MaximumStock, boxes * pr.Factor,
                                    costs ? h.UnitCost : null, pr.PresentationId, pr.Presentation, pr.Factor, boxes);
                            }
                            var cost = costs && h is not null ? Math.Round(h.UnitCost / h.Factor, 4) : (decimal?)null;
                            return new Line(p.ProductId, p.Name, p.Unit, p.ItbisRate, p.Stock, p.MinimumStock, p.MaximumStock, units, cost, null, null, 1, units);
                        }).ToList();
                        return new Group(g.Key, g.Key is null ? null : last[g.First().ProductId].Supplier, lines,
                            costs ? Money.Round(lines.Sum(l => l.Quantity * (l.LastCost ?? 0))) : null);
                    });
                    return Results.Ok(groups.OrderBy(g => g.SupplierId is null).ThenBy(g => g.Supplier, StringComparer.CurrentCulture).ToList());
                })
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
    }
}
