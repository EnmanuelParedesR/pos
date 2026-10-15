namespace Toki.Inventario.Contracts;

/// <summary>Stock of a product in all warehouses, valued at its average cost (without ITBIS) and at its shelf price (with ITBIS).</summary>
public sealed record ValorInventario(Guid ProductoId, string Producto, Guid CategoriaId, string Categoria, UnidadMedida Unidad, bool Activo,
    decimal Existencia, decimal Costo, decimal ValorCosto, decimal Precio, decimal ValorVenta);

public interface IInventarioReportes
{
    /// <summary>Active products, and inactive ones that still have stock (so the value matches the kardex), highest cost value first.
    /// Negative stock is kept as is: it shows what must be counted.</summary>
    Task<IReadOnlyList<ValorInventario>> ValorAsync(CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, string>> CategoriasAsync(CancellationToken ct);
}
