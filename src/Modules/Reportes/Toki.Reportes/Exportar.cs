using System.Globalization;
using System.Reflection;
using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Toki.BuildingBlocks.Endpoints;
using Toki.SharedKernel;

namespace Toki.Reportes;

/// <summary>Turns a report's rows into JSON, CSV or Excel. Columns are the row record's properties, in order.</summary>
internal static class Exportar
{
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IResult Como<T>(IReadOnlyList<T> filas, string? formato, string nombre) => formato?.ToLowerInvariant() switch
    {
        null or "" or "json" => Results.Ok(filas),
        "csv" => Results.File(Csv(filas), "text/csv; charset=utf-8", $"{nombre}.csv"),
        "xlsx" => Results.File(Excel(filas, nombre), Xlsx, $"{nombre}.xlsx"),
        _ => EndpointExtensions.Problema(Error.Validation("Reportes.FormatoInvalido", "El formato debe ser json, csv o xlsx.")),
    };

    /// <summary>UTF-8 with BOM so Excel shows accents; dot decimals (invariant) so amounts parse the same everywhere.</summary>
    public static byte[] Csv<T>(IEnumerable<T> filas)
    {
        var columnas = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', columnas.Select(c => c.Name)));
        foreach (var fila in filas)
            sb.AppendLine(string.Join(',', columnas.Select(c => Celda(c.GetValue(fila)))));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Celda(object? valor)
    {
        var texto = valor switch
        {
            null => "",
            DateTime f => f.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => valor.ToString() ?? "",
        };
        // A name typed in the store that starts with = + - @ would run as a formula when the file is opened (CSV injection).
        if (valor is string && texto.Length > 0 && "=+-@\t\r".Contains(texto[0])) texto = "'" + texto;
        return texto.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{texto.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : texto;
    }

    private static byte[] Excel<T>(IEnumerable<T> filas, string nombre)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add(nombre.Length > 31 ? nombre[..31] : nombre);
        var lista = filas.ToList();
        if (lista.Count > 0) hoja.Cell(1, 1).InsertTable(lista);
        else
        {
            var columnas = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            for (var i = 0; i < columnas.Length; i++) hoja.Cell(1, i + 1).Value = columnas[i].Name;
        }
        hoja.Columns().AdjustToContents();
        using var salida = new MemoryStream();
        libro.SaveAs(salida);
        return salida.ToArray();
    }
}
