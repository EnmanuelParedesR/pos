using System.Globalization;
using System.Reflection;
using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Toki.BuildingBlocks.Endpoints;
using Toki.SharedKernel;

namespace Toki.Reports;

/// <summary>Turns a report's rows into JSON, CSV or Excel. Columns are the row record's properties, in order.
/// In CSV and Excel, UTC times are shown in store time (UTC−4) and the "Utc" suffix leaves the column name; JSON keeps UTC with its "Z".</summary>
internal static class Exportar
{
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Excel keeps every cell as an object in memory on the store server; bigger exports go to CSV.</summary>
    public const int MaxFilasExcel = 50_000;

    public static IResult Como<T>(IReadOnlyList<T> filas, string? formato, string nombre) => formato?.ToLowerInvariant() switch
    {
        null or "" or "json" => Results.Ok(filas),
        "csv" => Results.File(Csv(filas), "text/csv; charset=utf-8", $"{nombre}.csv"),
        "xlsx" when filas.Count > MaxFilasExcel => EndpointExtensions.Problema(
            Error.Validation("Reportes.DemasiadasFilas", $"El reporte tiene {filas.Count:N0} filas; Excel acepta hasta {MaxFilasExcel:N0} aquí. Use CSV o un período más corto.")),
        "xlsx" => Results.File(Excel(filas, nombre), Xlsx, $"{nombre}.xlsx"),
        _ => EndpointExtensions.Problema(Error.Validation("Reportes.FormatoInvalido", "El formato debe ser json, csv o xlsx.")),
    };

    /// <summary>UTF-8 with BOM so Excel shows accents; dot decimals (invariant) so amounts parse the same everywhere.</summary>
    public static byte[] Csv<T>(IEnumerable<T> filas)
    {
        var columnas = Columnas<T>();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', columnas.Select(Titulo)));
        foreach (var fila in filas)
            sb.AppendLine(string.Join(',', columnas.Select(c => Celda(Valor(c, fila)))));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static PropertyInfo[] Columnas<T>() => typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    private static string Titulo(PropertyInfo p) => p.Name.EndsWith("Utc", StringComparison.Ordinal) ? p.Name[..^3] : p.Name;

    private static object? Valor<T>(PropertyInfo p, T fila) => p.GetValue(fila) is DateTime utc ? utc + DiaNegocio.Desfase : p.GetValue(fila);

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

    /// <summary>Cells are written as values (never formulas); columns are sized from the first 100 rows only.</summary>
    private static byte[] Excel<T>(IReadOnlyList<T> filas, string nombre)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add(nombre.Length > 31 ? nombre[..31] : nombre);
        var columnas = Columnas<T>();
        for (var c = 0; c < columnas.Length; c++)
        {
            hoja.Cell(1, c + 1).Value = Titulo(columnas[c]);
            for (var f = 0; f < filas.Count; f++) hoja.Cell(f + 2, c + 1).Value = XLCellValue.FromObject(Valor(columnas[c], filas[f]) switch
            {
                DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                Guid g => g.ToString(),
                Enum e => e.ToString(),
                var v => v,
            });
        }
        hoja.Row(1).Style.Font.Bold = true;
        hoja.Columns().AdjustToContents(1, Math.Min(filas.Count + 1, 101));
        using var salida = new MemoryStream();
        libro.SaveAs(salida);
        return salida.ToArray();
    }
}
