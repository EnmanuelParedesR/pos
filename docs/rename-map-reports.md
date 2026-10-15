# Rename map, phase B: Reports module

Spanish → English renames in `src/Modules/Reports/Toki.Reports/**`. Tests, the Host and the client update their references with this map. Behavior did not change: same period check (366 days max), same formats, same 50,000-row Excel limit, CSV with BOM and formula-injection protection, same downloaded file names and sheet names, same Spanish CSV/XLSX column titles.

The module has no DbContext, tables or migrations (ADR-0004): nothing to regenerate.

## Project and files

| Old | New |
|---|---|
| project `Toki.Reports` (`src/Modules/Reports/Toki.Reports/Toki.Reports.csproj`) | (unchanged) |
| namespace `Toki.Reports` | (unchanged) |
| `ReportesModule.cs` | `ReportsModule.cs` |
| `Exportar.cs` | `Export.cs` |

## Types and members

| Old | New | Notes |
|---|---|---|
| `ReportesModule` (public static, empty) | `ReportsModule` | Host: `typeof(ReportsModule).Assembly` in `src/Hosts/Toki.Host.Local/Program.cs` |
| `ReportesEndpoints` (internal, `IEndpoint`) | `ReportsEndpoints` | |
| `Periodo(DateOnly Desde, DateOnly Hasta, string? Formato)` (internal record struct) | `Period(DateOnly From, DateOnly To, string? Format)` | bound with `[AsParameters]`, so the query params are now `from`, `to`, `format` |
| `Periodo.MaxDias` (= 366) | `Period.MaxDays` | |
| `Periodo.Invalido()` | `Period.Invalid()` | |
| `Periodo.Nombre(string reporte)` | `Period.Name(string report)` | returns `{report}-{From:yyyyMMdd}-{To:yyyyMMdd}` |
| `VentaCategoria(CategoriaId, Categoria, NetoSinItbis, Costo, Utilidad)` (internal) | `CategorySales(CategoryId, Category, NetWithoutItbis, Cost, Profit)` | |
| `Exportar` (internal static) | `Export` | |
| `Exportar.Xlsx` | `Export.Xlsx` | value unchanged |
| `Exportar.MaxFilasExcel` (= 50_000) | `Export.MaxExcelRows` | |
| `Exportar.Como<T>(IReadOnlyList<T> filas, string? formato, string nombre)` | `Export.As<T>(IReadOnlyList<T> rows, string? format, string name)` | |
| `Exportar.Csv<T>(IEnumerable<T> filas)` | `Export.Csv<T>(IEnumerable<T> rows)` | |
| `Exportar.Columnas<T>()` (private) | `Export.Columns<T>()` | |
| `Exportar.Titulo(PropertyInfo)` (private) | `Export.Header(PropertyInfo)` | |
| `Exportar.Valor<T>(PropertyInfo, T)` (private) | `Export.Value<T>(PropertyInfo, T)` | |
| `Exportar.Celda(object?)` (private) | `Export.Cell(object?)` | |
| `Exportar.Excel<T>(IReadOnlyList<T>, string)` (private) | `Export.Excel<T>(IReadOnlyList<T>, string)` | |
| — | `Export.Spanish` (private dictionary) | new, see "CSV and Excel column titles" |

OpenAPI tag: `Reportes` → `Reports`.

## Permission

| Old | New |
|---|---|
| `Permisos.ReportesVer` = `"reportes.ver"` | `Permissions.ReportsView` = `"reports.view"` (whole `/api/reports` group) |

## Routes

All `GET`. Group prefix `/api/reportes` → `/api/reports`.

Query params: `desde` → `from`, `hasta` → `to` (both `yyyy-MM-dd`, business days, both included), `formato` → `format`. Format values unchanged: `json` (default, also empty or missing), `csv`, `xlsx`, case-insensitive.

| Old | New | Query params (new) |
|---|---|---|
| `GET /api/reportes/ventas/por-dia?desde&hasta&formato` | `GET /api/reports/sales/by-day` | `from`, `to`, `format` |
| `GET /api/reportes/ventas/por-producto?desde&hasta&formato` | `GET /api/reports/sales/by-product` | `from`, `to`, `format` |
| `GET /api/reportes/ventas/por-categoria?desde&hasta&formato` | `GET /api/reports/sales/by-category` | `from`, `to`, `format` |
| `GET /api/reportes/ventas/por-cajero?desde&hasta&formato` | `GET /api/reports/sales/by-cashier` | `from`, `to`, `format` |
| `GET /api/reportes/turnos?desde&hasta&formato` | `GET /api/reports/shifts` | `from`, `to`, `format` |
| `GET /api/reportes/inventario/valor?formato` | `GET /api/reports/inventory/value` | `format` |
| `GET /api/reportes/dgii/607?desde&hasta&formato` | `GET /api/reports/dgii/607` | `from`, `to`, `format` |
| `GET /api/reportes/dgii/606?desde&hasta&formato` | `GET /api/reports/dgii/606` | `from`, `to`, `format` |
| `GET /api/reportes/por-cobrar?formato` | `GET /api/reports/receivables` | `format` |
| `GET /api/reportes/por-pagar?formato` | `GET /api/reports/payables` | `format` |

Example: `/api/reportes/ventas/por-dia?desde=2026-10-01&hasta=2026-10-07&formato=csv` → `/api/reports/sales/by-day?from=2026-10-01&to=2026-10-07&format=csv`.

### JSON rows per route

JSON is camelCase of the row records. The names come from the Contracts (`docs/rename-map.md`), except `CategorySales`, which is this module's.

| Route | Row type (old → new) | JSON fields (new, in order) |
|---|---|---|
| `sales/by-day` | `VentaDia` → `DailySales` | `day, saleCount, salesWithItbis, discount, returns, itbis, netWithoutItbis, cost, profit` |
| `sales/by-product` | `VentaProducto` → `ProductSales` | `productId, categoryId, product, quantity, netWithoutItbis, cost, profit` |
| `sales/by-category` | `VentaCategoria` → `CategorySales` | `categoryId, category, netWithoutItbis, cost, profit` (was `categoriaId, categoria, netoSinItbis, costo, utilidad`) |
| `sales/by-cashier` | `VentaCajero` → `CashierSales` | `cashierId, cashier, saleCount, voidedCount, soldWithItbis, discount` |
| `shifts` | `ResumenTurno` → `ShiftSummary` | `shiftId, register, cashier, openedUtc, closedUtc, zNumber, openingFloat, expectedCash, countedCash, difference` |
| `inventory/value` | `ValorInventario` → `InventoryValue` | `productId, product, categoryId, category, unit, isActive, stock, cost, costValue, price, saleValue` (`unit`: `"Unit"` / `"Pound"`) |
| `dgii/607` | `Documento607` → `Report607Row` | `taxId, idType, ncf, modifiedNcf, incomeType, documentDate, withholdingDate, invoicedAmount, itbisInvoiced, itbisWithheldByThirdParties, itbisCollected, incomeTaxWithheldByThirdParties, isrCollected, selectiveTax, otherTaxes, legalTip, cash, checkTransfer, card, creditSale, giftVouchers, barter, otherForms` |
| `dgii/606` | `Documento606` → `Report606Row` | `taxId, idType, goodsServicesType, ncf, modifiedNcf, documentDate, paymentDate, servicesAmount, goodsAmount, totalAmount, itbisInvoiced, itbisWithheld, itbisProportionality, itbisToCost, itbisToAdvance, itbisCollected, isrWithholdingType, incomeTaxWithheld, isrCollected, selectiveTax, otherTaxes, legalTip, paymentForm` |
| `receivables` | `CuentaPorCobrarCliente` → `CustomerReceivable` | `customerId, customer, days0To30, days31To60, days61To90, over90, total, creditLimit` |
| `payables` | `CuentaPorPagarSuplidor` → `SupplierPayable` | `supplierId, supplier, notYetDue, overdue1To30, overdue31To60, overdue61To90, overdueOver90, total` |

Contract methods used: `ISalesReports.ByDayAsync / ByProductAsync / ByCashierAsync / Report607Async`, `IInventoryReports.ValueAsync / CategoriesAsync`, `ICashRegisterReports.ShiftsAsync`, `IPurchasingReports.Report606Async / PayablesAsync`, `ICustomerReports.ReceivablesAsync`.

## Error codes

All `Validation` (HTTP 400). The `detail` text stays in Spanish.

| Old | New | Detail |
|---|---|---|
| `Reportes.PeriodoInvalido` | `Reports.InvalidPeriod` | `El período va de 'from' a 'to' y tiene como máximo 366 días.` (it named `'desde'` / `'hasta'`; now names the new query params) |
| `Reportes.FormatoInvalido` | `Reports.InvalidFormat` | `El formato debe ser json, csv o xlsx.` (unchanged) |
| `Reportes.DemasiadasFilas` | `Reports.TooManyRows` | `El reporte tiene {n} filas; Excel acepta hasta 50.000 aquí. Use CSV o un período más corto.` (unchanged) |

## Downloaded file names and sheet names (unchanged, Spanish)

The `.xlsx` sheet name is the file name without extension, cut to 31 characters.

| Route | File name (`.csv` / `.xlsx`) |
|---|---|
| `sales/by-day` | `ventas-por-dia-{from:yyyyMMdd}-{to:yyyyMMdd}` |
| `sales/by-product` | `ventas-por-producto-{from:yyyyMMdd}-{to:yyyyMMdd}` |
| `sales/by-category` | `ventas-por-categoria-{from:yyyyMMdd}-{to:yyyyMMdd}` |
| `sales/by-cashier` | `ventas-por-cajero-{from:yyyyMMdd}-{to:yyyyMMdd}` |
| `shifts` | `turnos-{from:yyyyMMdd}-{to:yyyyMMdd}` |
| `inventory/value` | `inventario-{today:yyyyMMdd}` (today = business day, UTC−4) |
| `dgii/607` | `607-{from:yyyyMMdd}-{to:yyyyMMdd}` |
| `dgii/606` | `606-{from:yyyyMMdd}-{to:yyyyMMdd}` |
| `receivables` | `por-cobrar-{today:yyyyMMdd}` |
| `payables` | `por-pagar-{today:yyyyMMdd}` |

Content types unchanged: `text/csv; charset=utf-8` and `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`.

## CSV and Excel column titles (unchanged, Spanish)

CSV/XLSX titles used to be the Spanish property names (minus `Utc`). The Contracts now have English property names, so `Export.Spanish` maps each English property back to its old Spanish title. The output is the same as before, in the same column order (606/607 included). It also maps the `UnitOfMeasure` values in cells: `Unit` → `Unidad`, `Pound` → `Libra`, as before. A property missing from the map falls back to its English name without `Utc`.

| Report | CSV/XLSX titles, in order |
|---|---|
| `sales/by-day` | `Dia,Ventas,VentasConItbis,Descuento,Devoluciones,Itbis,NetoSinItbis,Costo,Utilidad` |
| `sales/by-product` | `ProductoId,CategoriaId,Producto,Cantidad,NetoSinItbis,Costo,Utilidad` |
| `sales/by-category` | `CategoriaId,Categoria,NetoSinItbis,Costo,Utilidad` |
| `sales/by-cashier` | `CajeroId,Cajero,Ventas,Anuladas,VendidoConItbis,Descuento` |
| `shifts` | `TurnoId,Caja,Cajero,Abierto,Cerrado,NumeroZ,FondoInicial,EfectivoEsperado,EfectivoContado,Diferencia` |
| `inventory/value` | `ProductoId,Producto,CategoriaId,Categoria,Unidad,Activo,Existencia,Costo,ValorCosto,Precio,ValorVenta` |
| `dgii/607` | `RncCedula,TipoId,Ncf,NcfModificado,TipoIngreso,FechaComprobante,FechaRetencion,MontoFacturado,ItbisFacturado,ItbisRetenidoTerceros,ItbisPercibido,RetencionRentaTerceros,IsrPercibido,ImpuestoSelectivo,OtrosImpuestos,PropinaLegal,Efectivo,ChequeTransferencia,Tarjeta,VentaCredito,BonosRegalo,Permuta,OtrasFormas` |
| `dgii/606` | `RncCedula,TipoId,TipoBienesServicios,Ncf,NcfModificado,FechaComprobante,FechaPago,MontoServicios,MontoBienes,TotalMonto,ItbisFacturado,ItbisRetenido,ItbisProporcionalidad,ItbisAlCosto,ItbisPorAdelantar,ItbisPercibido,TipoRetencionIsr,RetencionRenta,IsrPercibido,ImpuestoSelectivo,OtrosImpuestos,PropinaLegal,FormaPago` |
| `receivables` | `ClienteId,Cliente,De0a30,De31a60,De61a90,MasDe90,Total,LimiteCredito` |
| `payables` | `SuplidorId,Suplidor,PorVencer,Vencido1a30,Vencido31a60,Vencido61a90,VencidoMas90,Total` |

If a Contracts row record gains or renames a property, add it to `Export.Spanish`.

## References to update outside the module

- `src/Hosts/Toki.Host.Local/Program.cs:78`: `typeof(ReportesModule).Assembly` → `typeof(ReportsModule).Assembly`.
- `src/Client/Toki.Client.UI/Paginas/Admin/Turnos.razor:61`: `/api/reportes/turnos?desde=…&hasta=…` → `/api/reports/shifts?from=…&to=…`.
- `src/Client/Toki.Client.UI/Paginas/Admin/Resumen.razor:61,66`: `/api/reportes/ventas/por-dia` and `/ventas/por-producto` with `desde/hasta` → `/api/reports/sales/by-day` and `/sales/by-product` with `from/to`.
- `tests/Toki.IntegrationTests/Reportes/ReportesTests.cs`: routes, `formato=` → `format=`, codes `Reports.InvalidPeriod` / `Reports.InvalidFormat`, JSON fields (e.g. `abiertoUtc` → `openedUtc`, `montoFacturado` → `invoicedAmount`, `efectivo` → `cash`, `tarjeta` → `card`, `chequeTransferencia` → `checkTransfer`, `tipoIngreso` → `incomeType`). The CSV/XLSX title asserts (`,Abierto,`, `ProductoId,CategoriaId,Producto,`, cell `Producto`) still hold.
- `tests/Toki.IntegrationTests/Reportes/DevolucionOtroDiaTests.cs:20`: `/api/reportes/{ruta}?desde=…&hasta=…` → `/api/reports/{route}?from=…&to=…` (and the route strings it passes).
- `tests/Toki.IntegrationTests/Compras/ComprasTests.cs:96,270,295,306`: `por-pagar` → `payables`, `dgii/606?desde&hasta` → `dgii/606?from&to`, `formato=csv` → `format=csv`, `suplidorId` → `supplierId`.
- `tests/Toki.IntegrationTests/Clientes/ClientesTests.cs:85,87,197`: `por-cobrar` → `receivables`.
- `tests/Toki.E2ETests/DiaDeTiendaTests.cs:219-247`: `por-cobrar` → `receivables` (`clienteId` → `customerId`), `por-pagar` → `payables` (`suplidorId` → `supplierId`), `ventas/por-dia` → `sales/by-day`, `dgii/607`, `inventario/valor` → `inventory/value`, and the `periodo` query string `desde/hasta` → `from/to`.
