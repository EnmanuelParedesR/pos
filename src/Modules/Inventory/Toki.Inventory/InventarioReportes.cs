using Microsoft.EntityFrameworkCore;
using Toki.Inventory.Contracts;
using Toki.Inventory.Persistence;
using Toki.SharedKernel;

namespace Toki.Inventory;

internal sealed class InventarioReportes(InventarioDbContext db) : IInventarioReportes
{
    public async Task<IReadOnlyList<ValorInventario>> ValorAsync(CancellationToken ct)
    {
        var existencias = await db.Existencias.GroupBy(e => e.ProductoId).Select(g => new { g.Key, Cantidad = g.Sum(e => e.Cantidad) })
            .ToDictionaryAsync(x => x.Key, x => x.Cantidad, ct);
        var productos = await (from p in db.Productos
                               join c in db.Categorias on p.CategoriaId equals c.Id
                               select new { p.Id, p.Nombre, p.CategoriaId, Categoria = c.Nombre, p.Unidad, p.Activo, p.Costo, p.Precio }).AsNoTracking().ToListAsync(ct);
        return productos.Where(p => p.Activo || existencias.GetValueOrDefault(p.Id) != 0).Select(p =>
        {
            var existencia = existencias.GetValueOrDefault(p.Id);
            return new ValorInventario(p.Id, p.Nombre, p.CategoriaId, p.Categoria, p.Unidad, p.Activo, existencia, p.Costo,
                Dinero.Redondear(existencia * p.Costo), p.Precio, Dinero.Redondear(existencia * p.Precio));
        }).OrderByDescending(v => v.ValorCosto).ThenBy(v => v.Producto, StringComparer.CurrentCulture).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, string>> CategoriasAsync(CancellationToken ct) =>
        await db.Categorias.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Nombre, ct);
}
