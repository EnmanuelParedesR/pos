using System.Globalization;
using System.Reflection;
using System.Text;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http;
using Toki.BuildingBlocks.Endpoints;
using Toki.SharedKernel;

namespace Toki.Reports;

/// <summary>Turns a report's rows into JSON, CSV or Excel. Columns are the row record's properties, in order.
/// In CSV and Excel, UTC times are shown in store time (UTC−4) and columns and enum values carry the Spanish names the owner and the accountant read
/// (<see cref="Spanish"/>; otherwise the property name without its "Utc" suffix). JSON keeps the English names and UTC with its "Z".</summary>
internal static class Export
{
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Excel keeps every cell as an object in memory on the store server; bigger exports go to CSV.</summary>
    public const int MaxExcelRows = 50_000;

    /// <summary>Column titles (row record properties) and enum values as the owner and the accountant know them. The 606/607/608 titles are DGII's.</summary>
    private static readonly Dictionary<string, string> Spanish = new(StringComparer.Ordinal)
    {
        // sales by day, product, category and cashier
        ["Day"] = "Dia", ["SaleCount"] = "Ventas", ["SalesWithItbis"] = "VentasConItbis", ["Discount"] = "Descuento", ["Returns"] = "Devoluciones",
        ["NetWithoutItbis"] = "NetoSinItbis", ["Cost"] = "Costo", ["Profit"] = "Utilidad",
        ["ProductId"] = "ProductoId", ["CategoryId"] = "CategoriaId", ["Product"] = "Producto", ["Category"] = "Categoria", ["Quantity"] = "Cantidad",
        ["CashierId"] = "CajeroId", ["Cashier"] = "Cajero", ["VoidedCount"] = "Anuladas", ["SoldWithItbis"] = "VendidoConItbis", ["SoldOnCredit"] = "VendidoFiado",
        // sales by payment method (Method values too) and by hour
        ["Method"] = "MedioPago", ["Paid"] = "Pagado", ["Refunded"] = "Devuelto", ["Net"] = "Neto", ["Hour"] = "Hora",
        ["Transfer"] = "Transferencia", ["OnAccount"] = "Credito", ["CreditBalance"] = "SaldoAFavor",
        // shifts
        ["ShiftId"] = "TurnoId", ["Register"] = "Caja", ["OpenedUtc"] = "Abierto", ["ClosedUtc"] = "Cerrado", ["ZNumber"] = "NumeroZ",
        ["OpeningFloat"] = "FondoInicial", ["ExpectedCash"] = "EfectivoEsperado", ["CountedCash"] = "EfectivoContado", ["Difference"] = "Diferencia",
        // inventory value (Unit is also the UnitOfMeasure value)
        ["Unit"] = "Unidad", ["Pound"] = "Libra", ["IsActive"] = "Activo", ["Stock"] = "Existencia", ["CostValue"] = "ValorCosto", ["Price"] = "Precio",
        ["SaleValue"] = "ValorVenta",
        // DGII 607 and 606
        ["TaxId"] = "RncCedula", ["IdType"] = "TipoId", ["ModifiedNcf"] = "NcfModificado", ["IncomeType"] = "TipoIngreso", ["DocumentDate"] = "FechaComprobante",
        ["WithholdingDate"] = "FechaRetencion", ["InvoicedAmount"] = "MontoFacturado", ["ItbisInvoiced"] = "ItbisFacturado",
        ["ItbisWithheldByThirdParties"] = "ItbisRetenidoTerceros", ["ItbisCollected"] = "ItbisPercibido", ["IncomeTaxWithheldByThirdParties"] = "RetencionRentaTerceros",
        ["IsrCollected"] = "IsrPercibido", ["SelectiveTax"] = "ImpuestoSelectivo", ["OtherTaxes"] = "OtrosImpuestos", ["LegalTip"] = "PropinaLegal",
        ["Cash"] = "Efectivo", ["CheckTransfer"] = "ChequeTransferencia", ["Card"] = "Tarjeta", ["CreditSale"] = "VentaCredito", ["GiftVouchers"] = "BonosRegalo",
        ["Barter"] = "Permuta", ["OtherForms"] = "OtrasFormas",
        ["GoodsServicesType"] = "TipoBienesServicios", ["PaymentDate"] = "FechaPago", ["ServicesAmount"] = "MontoServicios", ["GoodsAmount"] = "MontoBienes",
        ["TotalAmount"] = "TotalMonto", ["ItbisWithheld"] = "ItbisRetenido", ["ItbisProportionality"] = "ItbisProporcionalidad", ["ItbisToCost"] = "ItbisAlCosto",
        ["ItbisToAdvance"] = "ItbisPorAdelantar", ["IsrWithholdingType"] = "TipoRetencionIsr", ["IncomeTaxWithheld"] = "RetencionRenta", ["PaymentForm"] = "FormaPago",
        // DGII 608
        ["VoidType"] = "TipoAnulacion",
        // receivables and payables
        ["CustomerId"] = "ClienteId", ["Customer"] = "Cliente", ["Days0To30"] = "De0a30", ["Days31To60"] = "De31a60", ["Days61To90"] = "De61a90",
        ["Over90"] = "MasDe90", ["CreditLimit"] = "LimiteCredito",
        ["SupplierId"] = "SuplidorId", ["Supplier"] = "Suplidor", ["NotYetDue"] = "PorVencer", ["Overdue1To30"] = "Vencido1a30", ["Overdue31To60"] = "Vencido31a60",
        ["Overdue61To90"] = "Vencido61a90", ["OverdueOver90"] = "VencidoMas90",
        // audit log (TK-213)
        ["Sequence"] = "Registro", ["DateUtc"] = "Fecha", ["UserId"] = "UsuarioId", ["User"] = "Usuario", ["Action"] = "Accion", ["ResourceType"] = "Entidad",
        ["ResourceId"] = "EntidadId", ["Data"] = "Detalle",
    };

    public static IResult As<T>(IReadOnlyList<T> rows, string? format, string name) => format?.ToLowerInvariant() switch
    {
        null or "" or "json" => Results.Ok(rows),
        "csv" => Results.File(Csv(rows), "text/csv; charset=utf-8", $"{name}.csv"),
        "xlsx" when rows.Count > MaxExcelRows => EndpointExtensions.Problem(
            Error.Validation("Reports.TooManyRows", $"El reporte tiene {rows.Count:N0} filas; Excel acepta hasta {MaxExcelRows:N0} aquí. Use CSV o un período más corto.")),
        "xlsx" => Results.File(Excel(rows, name), Xlsx, $"{name}.xlsx"),
        _ => EndpointExtensions.Problem(Error.Validation("Reports.InvalidFormat", "El formato debe ser json, csv o xlsx.")),
    };

    /// <summary>UTF-8 with BOM so Excel shows accents; dot decimals (invariant) so amounts parse the same everywhere.</summary>
    public static byte[] Csv<T>(IEnumerable<T> rows)
    {
        var columns = Columns<T>();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', columns.Select(Header)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(',', columns.Select(c => Cell(Value(c, row)))));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static PropertyInfo[] Columns<T>() => typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    private static string Header(PropertyInfo p) =>
        Spanish.TryGetValue(p.Name, out var title) ? title : p.Name.EndsWith("Utc", StringComparison.Ordinal) ? p.Name[..^3] : p.Name;

    private static object? Value<T>(PropertyInfo p, T row) => p.GetValue(row) switch
    {
        DateTime utc => utc + BusinessDay.Offset,
        Enum e => Spanish.GetValueOrDefault(e.ToString(), e.ToString()),
        string method when p.Name == "Method" => Spanish.GetValueOrDefault(method, method), // a payment method sent as text by its module
        var v => v,
    };

    private static string Cell(object? value)
    {
        var text = value switch
        {
            null => "",
            DateTime f => f.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
        // A name typed in the store that starts with = + - @ would run as a formula when the file is opened (CSV injection).
        if (value is string && text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
    }

    /// <summary>Cells are written as values (never formulas); columns are sized from the first 100 rows only.</summary>
    private static byte[] Excel<T>(IReadOnlyList<T> rows, string name)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(name.Length > 31 ? name[..31] : name);
        var columns = Columns<T>();
        for (var c = 0; c < columns.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = Header(columns[c]);
            for (var r = 0; r < rows.Count; r++) sheet.Cell(r + 2, c + 1).Value = XLCellValue.FromObject(Value(columns[c], rows[r]) switch
            {
                DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                Guid g => g.ToString(),
                Enum e => e.ToString(),
                var v => v,
            });
        }
        sheet.Row(1).Style.Font.Bold = true;
        sheet.Columns().AdjustToContents(1, Math.Min(rows.Count + 1, 101));
        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }
}
