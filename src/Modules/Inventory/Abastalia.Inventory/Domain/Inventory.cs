using Abastalia.Inventory.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Inventory.Domain;

internal sealed class Category
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>#RRGGBB shown on the register tiles.</summary>
    public string Color { get; set; } = "#6B1F5E";
    /// <summary>Predefined line notes for products of this category ("molida", "sin grasa").</summary>
    public List<string> Notes { get; set; } = [];
    public bool IsActive { get; set; } = true;
    /// <summary>TK-196: loaded as "datos de ejemplo"; removing them takes it away.</summary>
    public bool IsSample { get; set; }
}

internal sealed class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid CategoryId { get; set; }
    public UnitOfMeasure Unit { get; set; }
    /// <summary>Short code the scale prints in weight labels. Stored without leading zeros so "0321" and "321" are the same PLU.</summary>
    public string? Plu { get; set; }

    public static string? NormalizePlu(string? plu) => string.IsNullOrWhiteSpace(plu) ? null : plu.Trim().TrimStart('0');
    public ItbisRate ItbisRate { get; set; }
    /// <summary>Retail price per base unit, ITBIS included.</summary>
    public decimal Price { get; set; }
    public decimal? WholesalePrice { get; set; }
    /// <summary>Weighted average cost per base unit, without ITBIS. Changed only by purchases.</summary>
    public decimal Cost { get; set; }
    public bool SellWithoutStock { get; set; }
    public decimal MinimumStock { get; set; }
    /// <summary>TK-191: the suggested order fills up to it; 0 = not set (it fills up to the minimum).</summary>
    public decimal MaximumStock { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>TK-196: loaded as "datos de ejemplo" (see Features/SampleData.cs).</summary>
    public bool IsSample { get; set; }
    public int Version { get; set; }
}

internal sealed class Presentation
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Base units in one presentation (a box of 12 → 12; a 5 lb pack → 5).</summary>
    public decimal Factor { get; set; }
    public decimal Price { get; set; }
    public decimal? WholesalePrice { get; set; }
    public bool IsActive { get; set; } = true;
}

internal sealed class Barcode
{
    public string Code { get; set; } = "";
    public Guid ProductId { get; set; }
    public Guid? PresentationId { get; set; }
}

internal sealed class Warehouse
{
    public static readonly Guid MainId = new("0199e000-0000-7000-8000-0000000000a1");

    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

internal sealed class Stock
{
    public Guid ProductId { get; set; }
    public Guid WarehouseId { get; set; }
    public decimal Quantity { get; set; }
    /// <summary>Set when a sale went below zero: the shelf must be counted.</summary>
    public bool NeedsCount { get; set; }
}

/// <summary>Kardex row. Never updated or deleted.</summary>
internal sealed class StockMovement
{
    public long Sequence { get; set; }
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid WarehouseId { get; set; }
    public StockMovementType Type { get; set; }
    /// <summary>Signed base units: positive in, negative out.</summary>
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal ResultingStock { get; set; }
    public Guid? ReferenceId { get; set; }
    public string? Reason { get; set; }
    public Guid? UserId { get; set; }
    public DateTime DateUtc { get; set; }
}

/// <summary>How the scale encodes an EAN-13 label: "2X" prefix, PLU length, and whether the value is weight or price.</summary>
internal sealed class LabelFormat
{
    public string Prefix { get; set; } = "";
    public int PluLength { get; set; }
    public bool IsPrice { get; set; }
    public int Decimals { get; set; }

    public static IEnumerable<LabelFormat> Defaults() =>
    [
        new() { Prefix = "20", PluLength = 5, IsPrice = false, Decimals = 3 },
        new() { Prefix = "21", PluLength = 5, IsPrice = false, Decimals = 3 },
        new() { Prefix = "22", PluLength = 4, IsPrice = true, Decimals = 2 },
    ];
}

internal sealed record ParsedScaleLabel(string Plu, decimal? Weight, decimal? Price);

internal static class ScaleLabel
{
    /// <summary>Reads an in-store EAN-13 label (prefixes 20–29). Null when the code is not a scale label of a known format.</summary>
    public static Result<ParsedScaleLabel>? Read(string code, IEnumerable<LabelFormat> formats)
    {
        if (code.Length != 13 || code[0] != '2' || !code.All(char.IsAsciiDigit)) return null;
        var format = formats.FirstOrDefault(f => code.StartsWith(f.Prefix, StringComparison.Ordinal));
        if (format is null) return null;
        if (CheckDigit(code.AsSpan(0, 12)) != code[12] - '0')
            return Error.Validation("Inventory.LabelMisread", "La etiqueta no se leyó bien. Escanéela otra vez.");

        var plu = code.Substring(format.Prefix.Length, format.PluLength);
        var valueStart = format.Prefix.Length + format.PluLength;
        var value = decimal.Parse(code.AsSpan(valueStart, 12 - valueStart), System.Globalization.CultureInfo.InvariantCulture)
                    / (decimal)Math.Pow(10, format.Decimals);
        if (value <= 0) return Error.Validation("Inventory.LabelZeroValue", "La etiqueta tiene peso o precio en cero.");
        return new ParsedScaleLabel(plu, format.IsPrice ? null : value, format.IsPrice ? value : null);
    }

    public static int CheckDigit(ReadOnlySpan<char> twelve)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (twelve[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - sum % 10) % 10;
    }
}

internal static class AverageCost
{
    /// <summary>Weighted average after receiving <paramref name="quantity"/> at <paramref name="inboundCost"/>.
    /// With zero or negative stock on hand, the new cost is the entry cost.</summary>
    public static decimal Calculate(decimal stock, decimal currentCost, decimal quantity, decimal inboundCost)
    {
        if (stock <= 0) return Math.Round(inboundCost, 4);
        return Math.Round((stock * currentCost + quantity * inboundCost) / (stock + quantity), 4);
    }

    /// <summary>Takes a voided purchase back out of the average: the inverse of <see cref="Calculate"/>.
    /// Null (keep the cost) when nothing is left in stock or the result is not positive: most of that purchase was already sold.</summary>
    public static decimal? Reverse(decimal stockAfter, decimal currentCost, decimal quantity, decimal inboundCost)
    {
        if (stockAfter <= 0) return null;
        var cost = Math.Round(((stockAfter + quantity) * currentCost - quantity * inboundCost) / stockAfter, 4);
        return cost > 0 ? cost : null;
    }
}

internal static class InventoryErrors
{
    public static readonly Error ProductNotFound = Error.NotFound("Inventory.ProductNotFound", "El producto no existe.");
    public static readonly Error CategoryNotFound = Error.NotFound("Inventory.CategoryNotFound", "La categoría no existe.");
    public static readonly Error WarehouseNotFound = Error.NotFound("Inventory.WarehouseNotFound", "El almacén no existe.");
    public static readonly Error CodeNotFound = Error.NotFound("Inventory.CodeNotFound", "No hay ningún producto con ese código.");
    public static readonly Error DuplicateBarcode = Error.Conflict("Inventory.DuplicateBarcode", "Ese código de barras ya está asignado a otro producto.");
    public static readonly Error DuplicatePlu = Error.Conflict("Inventory.DuplicatePlu", "Ese PLU ya lo usa otro producto.");
    public static readonly Error NoPricePermission = Error.Forbidden("Inventory.NoPricePermission", "Cambiar precios necesita el permiso de precios.");

    public static Error OutOfStock(string product, decimal available) =>
        Error.Conflict("Inventory.OutOfStock", $"No hay suficiente {product} (quedan {available:0.###}).");
}
