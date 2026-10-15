using System.Net.Http.Json;

namespace Toki.IntegrationTests.Infra;

/// <summary>Builds catalog data through the public API.</summary>
public static class Catalogo
{
    public static async Task<Guid> CategoriaAsync(this HttpClient c, string nombre = "Res", string[]? notas = null)
    {
        var r = await c.PostAsJsonAsync("/api/inventario/categorias", new { Nombre = nombre, Color = "#B42318", Notas = notas ?? ["molida", "sin grasa"] });
        r.EnsureSuccessStatusCode();
        return (await r.Json()).GetProperty("id").GetGuid();
    }

    public static async Task<Guid> ProductoAsync(this HttpClient c, Guid categoriaId, string nombre, decimal precio,
        string unidad = "Unidad", string tasa = "General18", decimal costo = 0, decimal existencia = 0, string? codigo = null,
        string? plu = null, bool venderSinExistencia = false, decimal? mayorista = null)
    {
        var r = await c.PostAsJsonAsync("/api/inventario/productos", new
        {
            Nombre = nombre, CategoriaId = categoriaId, Unidad = unidad, Plu = plu, TasaItbis = tasa, Precio = precio,
            PrecioMayorista = mayorista, Costo = costo, VenderSinExistencia = venderSinExistencia, StockMinimo = 0,
            Codigos = codigo is null ? Array.Empty<string>() : [codigo], ExistenciaInicial = existencia,
        });
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(await r.Content.ReadAsStringAsync());
        return (await r.Json()).GetProperty("id").GetGuid();
    }

    public static async Task<decimal> ExistenciaAsync(this HttpClient c, Guid productoId, Guid? almacenId = null)
    {
        var p = await c.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/inventario/productos/{productoId}");
        foreach (var e in p.GetProperty("existencias").EnumerateArray())
            if (almacenId is null ? e.GetProperty("almacen").GetString() == "Tienda" : e.GetProperty("almacenId").GetGuid() == almacenId)
                return e.GetProperty("cantidad").GetDecimal();
        return 0;
    }
}
