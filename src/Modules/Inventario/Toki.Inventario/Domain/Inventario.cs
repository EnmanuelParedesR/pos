using Toki.Inventario.Contracts;
using Toki.SharedKernel;

namespace Toki.Inventario.Domain;

internal sealed class Categoria
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = "";
    /// <summary>#RRGGBB shown on the register tiles.</summary>
    public string Color { get; set; } = "#0B5CAD";
    /// <summary>Predefined line notes for products of this category ("molida", "sin grasa").</summary>
    public List<string> Notas { get; set; } = [];
    public bool Activa { get; set; } = true;
}

internal sealed class Producto
{
    public Guid Id { get; set; }
    public string Nombre { get; set; } = "";
    public Guid CategoriaId { get; set; }
    public UnidadMedida Unidad { get; set; }
    /// <summary>Short code the scale prints in weight labels. Stored without leading zeros so "0321" and "321" are the same PLU.</summary>
    public string? Plu { get; set; }

    public static string? NormalizarPlu(string? plu) => string.IsNullOrWhiteSpace(plu) ? null : plu.Trim().TrimStart('0');
    public TasaItbis TasaItbis { get; set; }
    /// <summary>Retail price per base unit, ITBIS included.</summary>
    public decimal Precio { get; set; }
    public decimal? PrecioMayorista { get; set; }
    /// <summary>Weighted average cost per base unit, without ITBIS. Changed only by purchases.</summary>
    public decimal Costo { get; set; }
    public bool VenderSinExistencia { get; set; }
    public decimal StockMinimo { get; set; }
    public bool Activo { get; set; } = true;
    public int Version { get; set; }
}

internal sealed class Presentacion
{
    public Guid Id { get; set; }
    public Guid ProductoId { get; set; }
    public string Nombre { get; set; } = "";
    /// <summary>Base units in one presentation (a box of 12 → 12; a 5 lb pack → 5).</summary>
    public decimal Factor { get; set; }
    public decimal Precio { get; set; }
    public decimal? PrecioMayorista { get; set; }
    public bool Activa { get; set; } = true;
}

internal sealed class CodigoBarras
{
    public string Codigo { get; set; } = "";
    public Guid ProductoId { get; set; }
    public Guid? PresentacionId { get; set; }
}

internal sealed class Almacen
{
    public static readonly Guid PrincipalId = new("0199e000-0000-7000-8000-0000000000a1");

    public Guid Id { get; set; }
    public string Nombre { get; set; } = "";
    public bool Activo { get; set; } = true;
}

internal sealed class Existencia
{
    public Guid ProductoId { get; set; }
    public Guid AlmacenId { get; set; }
    public decimal Cantidad { get; set; }
    /// <summary>Set when a sale went below zero: the shelf must be counted.</summary>
    public bool PendienteContar { get; set; }
}

/// <summary>Kardex row. Never updated or deleted.</summary>
internal sealed class Movimiento
{
    public long Secuencia { get; set; }
    public Guid Id { get; set; }
    public Guid ProductoId { get; set; }
    public Guid AlmacenId { get; set; }
    public TipoMovimiento Tipo { get; set; }
    /// <summary>Signed base units: positive in, negative out.</summary>
    public decimal Cantidad { get; set; }
    public decimal CostoUnitario { get; set; }
    public decimal ExistenciaResultante { get; set; }
    public Guid? ReferenciaId { get; set; }
    public string? Motivo { get; set; }
    public Guid? UsuarioId { get; set; }
    public DateTime FechaUtc { get; set; }
}

/// <summary>How the scale encodes an EAN-13 label: "2X" prefix, PLU length, and whether the value is weight or price.</summary>
internal sealed class FormatoEtiqueta
{
    public string Prefijo { get; set; } = "";
    public int LongitudPlu { get; set; }
    public bool EsPrecio { get; set; }
    public int Decimales { get; set; }

    public static IEnumerable<FormatoEtiqueta> Predeterminados() =>
    [
        new() { Prefijo = "20", LongitudPlu = 5, EsPrecio = false, Decimales = 3 },
        new() { Prefijo = "21", LongitudPlu = 5, EsPrecio = false, Decimales = 3 },
        new() { Prefijo = "22", LongitudPlu = 4, EsPrecio = true, Decimales = 2 },
    ];
}

internal sealed record LecturaEtiqueta(string Plu, decimal? Peso, decimal? Precio);

internal static class EtiquetaBalanza
{
    /// <summary>Reads an in-store EAN-13 label (prefixes 20–29). Null when the code is not a scale label of a known format.</summary>
    public static Result<LecturaEtiqueta>? Leer(string codigo, IEnumerable<FormatoEtiqueta> formatos)
    {
        if (codigo.Length != 13 || codigo[0] != '2' || !codigo.All(char.IsAsciiDigit)) return null;
        var formato = formatos.FirstOrDefault(f => codigo.StartsWith(f.Prefijo, StringComparison.Ordinal));
        if (formato is null) return null;
        if (DigitoVerificador(codigo.AsSpan(0, 12)) != codigo[12] - '0')
            return Error.Validation("Inventario.EtiquetaMalLeida", "La etiqueta no se leyó bien. Escanéela otra vez.");

        var plu = codigo.Substring(formato.Prefijo.Length, formato.LongitudPlu);
        var inicioValor = formato.Prefijo.Length + formato.LongitudPlu;
        var valor = decimal.Parse(codigo.AsSpan(inicioValor, 12 - inicioValor), System.Globalization.CultureInfo.InvariantCulture)
                    / (decimal)Math.Pow(10, formato.Decimales);
        if (valor <= 0) return Error.Validation("Inventario.EtiquetaSinValor", "La etiqueta tiene peso o precio en cero.");
        return new LecturaEtiqueta(plu, formato.EsPrecio ? null : valor, formato.EsPrecio ? valor : null);
    }

    public static int DigitoVerificador(ReadOnlySpan<char> doce)
    {
        var suma = 0;
        for (var i = 0; i < 12; i++) suma += (doce[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - suma % 10) % 10;
    }
}

internal static class CostoPromedio
{
    /// <summary>Weighted average after receiving <paramref name="cantidad"/> at <paramref name="costoEntrada"/>.
    /// With zero or negative stock on hand, the new cost is the entry cost.</summary>
    public static decimal Calcular(decimal existencia, decimal costoActual, decimal cantidad, decimal costoEntrada)
    {
        if (existencia <= 0) return Math.Round(costoEntrada, 4);
        return Math.Round((existencia * costoActual + cantidad * costoEntrada) / (existencia + cantidad), 4);
    }

    /// <summary>Takes a voided purchase back out of the average: the inverse of <see cref="Calcular"/>.
    /// Null (keep the cost) when nothing is left in stock or the result is not positive: most of that purchase was already sold.</summary>
    public static decimal? Revertir(decimal existenciaDespues, decimal costoActual, decimal cantidad, decimal costoEntrada)
    {
        if (existenciaDespues <= 0) return null;
        var costo = Math.Round(((existenciaDespues + cantidad) * costoActual - cantidad * costoEntrada) / existenciaDespues, 4);
        return costo > 0 ? costo : null;
    }
}

internal static class InventarioErrors
{
    public static readonly Error ProductoNoEncontrado = Error.NotFound("Inventario.ProductoNoEncontrado", "El producto no existe.");
    public static readonly Error CategoriaNoEncontrada = Error.NotFound("Inventario.CategoriaNoEncontrada", "La categoría no existe.");
    public static readonly Error AlmacenNoEncontrado = Error.NotFound("Inventario.AlmacenNoEncontrado", "El almacén no existe.");
    public static readonly Error CodigoNoEncontrado = Error.NotFound("Inventario.CodigoNoEncontrado", "No hay ningún producto con ese código.");
    public static readonly Error CodigoDuplicado = Error.Conflict("Inventario.CodigoDuplicado", "Ese código de barras ya está asignado a otro producto.");
    public static readonly Error PluDuplicado = Error.Conflict("Inventario.PluDuplicado", "Ese PLU ya lo usa otro producto.");
    public static readonly Error SinPermisoPrecios = Error.Forbidden("Inventario.SinPermisoPrecios", "Cambiar precios necesita el permiso de precios.");

    public static Error SinExistencia(string producto, decimal disponible) =>
        Error.Conflict("Inventario.SinExistencia", $"No hay suficiente {producto} (quedan {disponible:0.###}).");
}
