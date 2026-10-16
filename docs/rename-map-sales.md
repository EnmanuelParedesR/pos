# Rename map, phase B: Sales module (`src/Modules/Sales/Abastalia.Sales`)

Spanish → English renames in `Abastalia.Sales`. Follows `docs/glosario-codigo.md` and `docs/rename-map.md` (phase A: SharedKernel, BuildingBlocks, Contracts). Behavior did not change, except the two display-text points marked **(display)** below.

The API uses camelCase JSON and string enums: every record property below appears on the wire in camelCase (`SaleId` → `saleId`), every enum by its English name.

## Files

| Old | New |
|---|---|
| `VentasModule.cs` | `SalesModule.cs` |
| `VentasDbContext.cs` | `SalesDbContext.cs` |
| `VentasReportes.cs` | `SalesReports.cs` |
| `Domain/Venta.cs` | `Domain/Sale.cs` |
| `Domain/Calculadora.cs` | `Domain/TotalsCalculator.cs` |
| `Domain/Comprobante.cs` | `Domain/Receipt.cs` |
| `Features/RegistrarVenta.cs` | `Features/RegisterSale.cs` |
| `Features/AnularYDevolver.cs` | `Features/VoidAndReturn.cs` |
| `Features/Consultas.cs` | `Features/Queries.cs` |
| `Persistence/Migraciones/` (`20261015095648_InicialVentas`, `VentasDbContextModelSnapshot`) | `Persistence/Migrations/` (`20261015152635_InitialSales`, `SalesDbContextModelSnapshot`, regenerated) |

## Module registration and internal types (Host, tests)

| Old | New |
|---|---|
| `Abastalia.Sales.VentasModule` | `Abastalia.Sales.SalesModule` |
| `VentasModule.AddVentasModule(services)` | `SalesModule.AddSalesModule(services)` |
| `VentasDbContext` | `SalesDbContext` |
| `VentasDbContext.Ventas` / `Lineas` / `Pagos` / `Devoluciones` / `EnEspera` | `SalesDbContext.Sales` / `Lines` / `Payments` / `Returns` / `HeldSales` |
| `VentasDbContext.VentaCompletaAsync(id, ct)` | `SalesDbContext.FullSaleAsync(id, ct)` |
| `VentasDbContext.BloquearVentaAsync(id, ct)` | `SalesDbContext.LockSaleAsync(id, ct)` |
| schema `ventas` | schema `sales` |
| `VentasConfiguration` | `SalesConfiguration` |
| `DisenoVentas` (dotnet-ef factory) | `SalesDesignTimeFactory` |
| `VentasReportes : IVentasReportes` | `SalesReports : ISalesReports` (methods per phase A map: `ByDayAsync`, `ByProductAsync`, `ByCashierAsync`, `Report607Async`) |
| `VentasErrors` | `SalesErrors` (members below) |

Feature classes (all `internal static`, in `Abastalia.Sales.Features`):

| Old | New |
|---|---|
| `RegistrarVenta` (+ `.Linea`, `.Pago`, `.Command`, `.Response`, `.Validator`, `.Handler`, `.Endpoint`) | `RegisterSale` (+ `.Line`, `.Payment`, `.Command`, `.Response`, `.Validator`, `.Handler`, `.Endpoint`) |
| `AnularVenta` (+ `.Body`) | `VoidSale` (+ `.Body`) |
| `DevolverVenta` (+ `.Linea`, `.Body`) | `ReturnSale` (+ `.Line`, `.Body`) |
| `ObtenerComprobante` | `GetReceipt` |
| `ObtenerVenta` (+ `LineaDto`, `PagoDto`, `DevolucionDto`) | `GetSale` (+ `LineDto`, `PaymentDto`, `ReturnDto`) |
| `ListarVentas` (+ `Item`, `TamanoPagina = 100`) | `ListSales` (+ `Item`, `PageSize = 100`) |
| `VentasEnEspera` | `HeldSales` |
| `VentasEnEspera.Guardar` / `Quitar` / `Item` | `HeldSales.Save` / `Remove` / `Item` |
| `VentasEnEspera.GuardarHandler` / `QuitarHandler` | `HeldSales.SaveHandler` / `RemoveHandler` |
| `VentasEnEspera.PurgarAlCerrarTurno : IManejadorEvento<TurnoCerradoV1>` | `HeldSales.PurgeOnShiftClosed : IEventHandler<ShiftClosedV1>` |

Private helpers in `RegisterSale.Handler`: `AutorizarAsync` → `ApproveAsync`, `Repetida` → `Repeated`, `Respuesta` → `ToResponse`. In `ReturnSale.Handler`: `YaRegistradaAsync` → `AlreadyRecordedAsync`. In `SalesReports`: `Movida` → `MovedLine(ProductId, CategoryId, Description, WithoutPresentation, Quantity, Net, Cost)`, `Devuelto` → `Returned(DateUtc, Total, Itbis, RestockedCost, Lines)`, `DevueltoAsync` → `ReturnedAsync`, local `Fila` → `Row`, `TipoId` → `IdType`, `Medio` → `ByMethod`.

## Totals calculator and payments (`Abastalia.Sales.Domain`, used by unit tests)

| Old | New |
|---|---|
| `Descuento(bool EsPorcentaje, decimal Valor)` (public) | `Discount(bool IsPercentage, decimal Value)` |
| `LineaEntrada(PrecioUnitario, Cantidad, TasaItbis Tasa, Descuento? Descuento, decimal? MontoFijo = null)` | `LineInput(UnitPrice, Quantity, ItbisRate Rate, Discount? Discount, decimal? FixedAmount = null)` |
| `LineaCalculada(Bruto, DescuentoLinea, DescuentoGlobal, Total, Itbis)` + computed `Descuento` | `CalculatedLine(Gross, LineDiscount, GlobalDiscount, Total, Itbis)` + computed `Discount` |
| `Totales(Lineas, Bruto, Descuento, Subtotal, Itbis, Total)` + computed `PorcentajeDescuento` | `Totals(Lines, Gross, Discount, Subtotal, Itbis, Total)` + computed `DiscountPercent` |
| `Calculadora` | `TotalsCalculator` |
| `Calculadora.Calcular(IReadOnlyList<LineaEntrada> lineas, Descuento? global)` → `Result<Totales>` | `TotalsCalculator.Calculate(IReadOnlyList<LineInput> lines, Discount? global)` → `Result<Totals>` |
| private `Monto`, `Repartir` | private `DiscountAmount`, `Spread` |
| `PagoEntrada(MedioPago Medio, Monto, Referencia)` | `PaymentInput(PaymentMethod Method, Amount, Reference)` |
| `PagoAplicado(Medio, Monto, Recibido, Referencia)` | `AppliedPayment(Method, Amount, Received, Reference)` |
| `Pagos` (static class) | `Payments` |
| `Pagos.Aplicar(decimal total, IReadOnlyList<PagoEntrada> pagos)` → `Result<(Pagos, Cambio, Redondeo)>` | `Payments.Apply(decimal total, IReadOnlyList<PaymentInput> payments)` → `Result<(IReadOnlyList<AppliedPayment> Payments, decimal Change, decimal Rounding)>` |
| — | new `internal static PaymentMethodNames.Display(PaymentMethod)` → `"Efectivo"`, `"Tarjeta"`, `"Transferencia"`, `"Fiado"`, `"Saldo a favor"` (in `Domain/Receipt.cs`) |

Unit-test cheat sheet: `MedioPago.Efectivo` → `PaymentMethod.Cash`, `Tarjeta` → `Card`; `TasaItbis.General18` → `ItbisRate.General18`, `Reducida16` → `Reduced16`, `Exento` → `Exempt`; named argument `MontoFijo:` → `FixedAmount:`; `r.Pagos` / `r.Cambio` / `r.Redondeo` → `r.Payments` / `r.Change` / `r.Rounding`; `.Monto` / `.Recibido` / `.Medio` → `.Amount` / `.Received` / `.Method`; `t.Lineas`, `t.Bruto`, `t.Descuento`, `t.PorcentajeDescuento` → `t.Lines`, `t.Gross`, `t.Discount`, `t.DiscountPercent`; line `DescuentoLinea` / `DescuentoGlobal` → `LineDiscount` / `GlobalDiscount`.

## Enums

| Old | New | Where |
|---|---|---|
| `NivelPrecio { Detalle, Mayorista }` (public) | `SalePriceLevel { Retail, Wholesale }` | `RegisterSale.Command.PriceLevel` (wire), `Sale.PriceLevel` (DB). Named `SalePriceLevel` to avoid clashing with `Abastalia.Customers.Contracts.PriceLevel`. |
| `EstadoVenta { Completada, Anulada }` (internal) | `SaleStatus { Completed, Voided }` | DB column `Sales.Status`; the `status` strings on the wire (below) |

Enums from Contracts that appear on the wire (phase A): `PaymentMethod` (`Cash`, `Card`, `Transfer`, `OnAccount`, `CreditBalance`), `EcfType` (`FiscalCredit31`, `Consumer32`, `DebitNote33`, `CreditNote34`), `UnitOfMeasure` (`Unit`, `Pound`).

## Routes

All tags `"Ventas"` → `"Sales"`.

| Old | New | Permission (old → new) |
|---|---|---|
| `POST /api/ventas` → `201 Location: /api/ventas/{ventaId}` | `POST /api/sales` → `201 Location: /api/sales/{saleId}` | `ventas.registrar` → `sales.create` |
| `GET /api/ventas/{id}` | `GET /api/sales/{id}` | `sales.create` |
| `GET /api/ventas?turnoId=&desde=&hasta=&buscar=&pagina=` | `GET /api/sales?shiftId=&from=&to=&search=&page=` | `sales.create` (+ `reports.view` to see other cashiers' sales) |
| `GET /api/ventas/{id}/comprobante` | `GET /api/sales/{id}/receipt` | `sales.create` |
| `POST /api/ventas/{id}/anular` | `POST /api/sales/{id}/void` | endpoint `sales.create`; command needs `sales.void` (was `ventas.anular`) or a supervisor approval |
| `POST /api/ventas/{id}/devoluciones` | `POST /api/sales/{id}/returns` | endpoint `sales.create`; command needs `sales.return` (was `ventas.devolver`) or a supervisor approval |
| `PUT /api/ventas/en-espera` → 204 | `PUT /api/sales/held` → 204 | `sales.create` |
| `DELETE /api/ventas/en-espera/{id}?turnoId=` → 204 | `DELETE /api/sales/held/{id}?shiftId=` → 204 | `sales.create` |
| `GET /api/ventas/en-espera?turnoId=` | `GET /api/sales/held?shiftId=` | `sales.create` |

Other permissions the sale may escalate to (supervisor approval): `ventas.cambiar-precio` → `sales.price-override`, `ventas.descuento-mayor` → `sales.large-discount`, `fiado.exceder-limite` → `credit.exceed-limit`.

## Requests and responses (wire)

### `POST /api/sales` — `RegisterSale.Command`

| Old | New |
|---|---|
| `ventaId` | `saleId` |
| `turnoId` | `shiftId` |
| `lineas` | `lines` |
| `pagos` | `payments` |
| `totalEsperado` | `expectedTotal` |
| `tipoComprobante` (`"Consumo32"` default / `"CreditoFiscal31"`) | `documentType` (`"Consumer32"` default / `"FiscalCredit31"`) |
| `clienteId` | `customerId` |
| `rncCliente` | `customerRnc` |
| `nombreCliente` | `customerName` |
| `nivel` (`"Detalle"` default / `"Mayorista"`) | `priceLevel` (`"Retail"` default / `"Wholesale"`) |
| `descuentoGlobal` | `globalDiscount` |
| `autorizaciones` (list of approval ids) | `supervisorApprovals` |

`lines[]` — `RegisterSale.Line`:

| Old | New |
|---|---|
| `productoId` | `productId` |
| `presentacionId` | `presentationId` |
| `cantidad` | `quantity` |
| `precioUnitario` | `unitPrice` |
| `descuento` | `discount` |
| `nota` | `note` |
| `codigoEtiqueta` | `scaleLabel` |

`discount` / `globalDiscount` — `Discount`: `esPorcentaje` → `isPercentage`, `valor` → `value`.

`payments[]` — `RegisterSale.Payment`: `medio` → `method`, `monto` → `amount`, `referencia` → `reference`.

Response `RegisterSale.Response`:

| Old | New |
|---|---|
| `ventaId` | `saleId` |
| `numero` | `number` |
| `eNcf` | `eNcf` (unchanged) |
| `total` | `total` |
| `cambio` | `change` |
| `comprobante` | `receipt` (the `Receipt` model below) |

### Receipt print model — `ComprobanteImpresion` → `Receipt`

Returned by `POST /api/sales` (`receipt`) and `GET /api/sales/{id}/receipt`. Factory `ComprobanteImpresion.De(Venta, DatosNegocio)` → `Receipt.From(Sale, BusinessProfile)`.

| Old | New |
|---|---|
| `negocio` | `businessName` |
| `rnc` | `rnc` |
| `direccion` | `address` |
| `telefono` | `phone` |
| `tipoComprobante` (Spanish title) | `documentType` (Spanish title, unchanged text: `"Factura de Consumo Electrónica"` / `"Factura de Crédito Fiscal Electrónica"`) |
| `eNcf` | `eNcf` |
| `numero` | `number` |
| `fechaLocal` | `localDateTime` |
| `caja` | `register` |
| `cajero` | `cashier` |
| `cliente` | `customer` |
| `rncCliente` | `customerRnc` |
| `lineas` | `lines` |
| `subtotal` | `subtotal` |
| `descuento` | `discount` |
| `itbis` | `itbis` |
| `total` | `total` |
| `pagos` | `payments` |
| `cambio` | `change` |
| `codigoSeguridad` | `securityCode` |
| `urlQr` | `qrUrl` |
| `pie` | `footer` |
| `anulada` | `isVoided` |

`lines[]` — `LineaImpresion` → `ReceiptLine`: `descripcion` → `description`, `cantidad` → `quantity`, `unidad` → `unit` (`"Unidad"`/`"Libra"` → `"Unit"`/`"Pound"`), `precio` → `price`, `descuento` → `discount`, `total` → `total`, `nota` → `note`.

`itbis[]` — `ItbisImpresion` → `ReceiptItbis`: `tasa` → `rate` (Spanish label, unchanged: `"ITBIS 18%"`, `"ITBIS 16%"`, `"ITBIS 0%"`, `"Exento"`), `base` → `base`, `itbis` → `itbis`.

`payments[]` — `PagoImpresion` → `ReceiptPayment`: `medio` → `method`, `monto` → `amount`, `recibido` → `received`. **(display)** `method` is now a `string` with the Spanish name to print (`"Efectivo"`, `"Tarjeta"`, `"Transferencia"`, `"Fiado"`, `"Saldo a favor"`), not the enum. Before it was the `MedioPago` enum (`"Efectivo"`, `"Tarjeta"`, `"Transferencia"`, `"Fiado"`, `"SaldoFavor"`). The client prints it as is.

### `GET /api/sales/{id}` — `ObtenerVenta.Response` → `GetSale.Response`

| Old | New |
|---|---|
| `id` | `id` |
| `numero` | `number` |
| `eNcf` | `eNcf` |
| `tipoComprobante` | `documentType` (`EcfType`) |
| `fechaUtc` | `dateUtc` |
| `estado` (`"Completada"` / `"Anulada"`) | `status` (`"Completed"` / `"Voided"`) |
| `caja` | `register` |
| `cajero` | `cashier` |
| `clienteId` | `customerId` |
| `cliente` | `customer` |
| `descuento` | `discount` |
| `itbis` | `itbis` |
| `total` | `total` |
| `cambio` | `change` |
| `motivoAnulacion` | `voidReason` |
| `eNcfAnulacion` | `voidENcf` |
| `lineas` | `lines` |
| `pagos` | `payments` |
| `devoluciones` | `returns` |

`lines[]` — `LineaDto` → `LineDto`: `id`, `descripcion` → `description`, `cantidad` → `quantity`, `precioUnitario` → `unitPrice`, `descuento` → `discount`, `total`, `itbis`, `cantidadDevuelta` → `returnedQuantity`, `nota` → `note`.

`payments[]` — `PagoDto` → `PaymentDto`: `medio` → `method` (`PaymentMethod` enum), `monto` → `amount`, `recibido` → `received`, `referencia` → `reference`.

`returns[]` — `DevolucionDto` → `ReturnDto`: `id`, `fechaUtc` → `dateUtc`, `total`, `medio` → `method`, `eNcf`, `motivo` → `reason`.

### `GET /api/sales` — `ListarVentas.Item` → `ListSales.Item` (array, 100 per page, newest first)

`id`, `numero` → `number`, `eNcf`, `fechaUtc` → `dateUtc`, `estado` (`"Completada"`/`"Anulada"`) → `status` (`"Completed"`/`"Voided"`), `cajero` → `cashier`, `cliente` → `customer`, `total`.

Query: `turnoId` → `shiftId`, `desde` → `from`, `hasta` → `to`, `buscar` → `search` (e-NCF or receipt number), `pagina` → `page`.

### `POST /api/sales/{id}/void` — `AnularVenta` → `VoidSale`

Body (`Body`): `motivo` → `reason`, `autorizacionSupervisorId` → `supervisorApprovalId`.
Command: `VoidSale.Command(SaleId, Reason, SupervisorApprovalId)`, implements `IRequiresApproval` (`Permission` = `sales.void`, `ResourceId` = `SaleId`).
Response: `ventaId` → `saleId`, `eNcfNotaCredito` → `creditNoteENcf`, `total`.

### `POST /api/sales/{id}/returns` — `DevolverVenta` → `ReturnSale`

Body (`Body`):

| Old | New |
|---|---|
| `devolucionId` | `returnId` |
| `turnoId` | `shiftId` |
| `lineas` | `lines` |
| `medio` | `method` (`Cash`, `Card`, `Transfer` or `CreditBalance`) |
| `motivo` | `reason` |
| `autorizacionSupervisorId` | `supervisorApprovalId` |

`lines[]` — `Linea` → `Line`: `lineaVentaId` → `saleLineId`, `cantidad` → `quantity`, `reponer` → `restock` (default `true`).
Command: `ReturnSale.Command(SaleId, ReturnId, ShiftId, Lines, Method, Reason, SupervisorApprovalId)`, implements `IRequiresApproval` (`Permission` = `sales.return`, `ResourceId` = `SaleId`).
Response: `devolucionId` → `returnId`, `eNcfNotaCredito` → `creditNoteENcf`, `total`, `itbis`, `medio` → `method`.

### Held sales — `VentasEnEspera` → `HeldSales`

`PUT /api/sales/held` body (`Save`): `id`, `turnoId` → `shiftId`, `etiqueta` → `label`, `contenido` → `content` (any JSON, stored raw), `total`.
`DELETE /api/sales/held/{id}?shiftId=` (`Remove(Id, ShiftId)`).
`GET /api/sales/held?shiftId=` → array of `Item`: `id`, `etiqueta` → `label`, `total`, `creadaUtc` → `createdUtc`, `contenido` → `content`.

## Error codes

`detail` texts are unchanged Spanish unless noted.

| Old | New | HTTP type |
|---|---|---|
| `Ventas.VentaNoEncontrada` | `Sales.SaleNotFound` | NotFound |
| `Ventas.ProductoNoDisponible` | `Sales.ProductUnavailable` | Conflict |
| `Ventas.YaAnulada` | `Sales.AlreadyVoided` | Conflict |
| `Ventas.FueraDeTurno` | `Sales.OutsideShift` | Conflict |
| `Ventas.ConDevoluciones` | `Sales.HasReturns` | Conflict |
| `Ventas.CantidadDevolucion` | `Sales.ReturnQuantityExceeded` | Validation |
| `Ventas.EtiquetaRepetida` | `Sales.DuplicateScaleLabel` | Conflict |
| `Ventas.MedioNoDisponible` | `Sales.MethodUnavailable` | Validation |
| `Ventas.IdRepetido` | `Sales.DuplicateId` | Conflict |
| `Ventas.EtiquetaNoCoincide` | `Sales.ScaleLabelMismatch` | Conflict |
| `Ventas.RncRequerido` | `Sales.RncRequired` | Validation |
| `Ventas.MedioExcedido` | `Sales.MethodExceeded` | Validation. **(display)** the detail names the method in Spanish via `PaymentMethodNames.Display` ("Por Efectivo…", "Por Saldo a favor…"); before it printed the enum name ("Por SaldoFavor…"). |
| `Ventas.TotalCambio` (+ `errors.total`) | `Sales.TotalChanged` (+ `errors.total`, unchanged key, value `"0.00"` invariant) | Conflict |
| `Ventas.CantidadInvalida` | `Sales.InvalidQuantity` | Validation |
| `Ventas.DescuentoInvalido` | `Sales.InvalidDiscount` | Validation |
| `Ventas.PagoInvalido` | `Sales.InvalidPayment` | Validation |
| `Ventas.EfectivoSinCentavos` | `Sales.CashNotWholePesos` | Validation |
| `Ventas.PagoExcedido` | `Sales.PaymentExceedsTotal` | Validation |
| `Ventas.PagoIncompleto` | `Sales.PaymentIncomplete` | Validation |
| `Clientes.ClienteNoEncontrado` (emitted by Sales when the sale's customer is missing or inactive) | `Customers.CustomerNotFound` | NotFound |

`SalesErrors` members: `VentaNoEncontrada` → `SaleNotFound`, `ProductoNoDisponible` → `ProductUnavailable`, `YaAnulada` → `AlreadyVoided`, `FueraDeTurno` → `OutsideShift`, `ConDevoluciones` → `HasReturns`, `CantidadDevolucion` → `ReturnQuantityExceeded`, `EtiquetaRepetida` → `DuplicateScaleLabel`, `MedioNoDisponible` → `MethodUnavailable`, `IdRepetido` → `DuplicateId`, `EtiquetaNoCoincide` → `ScaleLabelMismatch`, `RncRequerido` → `RncRequired`, `MedioExcedido(medio, maximo)` → `MethodExceeded(method, maximum)`, `TotalCambio(total)` → `TotalChanged(total)`.

Codes from other modules that Sales relies on:
- `VoidSale` maps the shift-closed error of `GetOpenShiftAsync` to `Sales.OutsideShift` by comparing its code with **`"CashRegister.ShiftClosed"`** (was `"Caja.TurnoCerrado"`). The CashRegister module must use exactly that code.
- Pass-through codes from Contracts implementations (`Approval.Required`, `Customers.CreditLimitExceeded`, `Inventory.OutOfStock`, cash and fiscal errors) are not renamed here.

FluentValidation `errors.*` keys follow the renamed property names (e.g. `Lineas[0].Cantidad` → `Lines[0].Quantity`, `Motivo` → `Reason`, `Medio` → `Method`, `Etiqueta` → `Label`, `RncCliente` → `CustomerRnc`, `NombreCliente` → `CustomerName`, `ClienteId` → `CustomerId`, `TipoComprobante` → `DocumentType`, `Nivel` → `PriceLevel`, `DevolucionId` → `ReturnId`). Messages stay Spanish.

## Audit actions

| Old | New | Resource type | Data (old → new) |
|---|---|---|---|
| `ventas.registrar` | `sales.create` | `Venta` → `Sale` | `{ Numero, ENcf, Total, Descuento, PorcentajeDescuento, Permisos }` → `{ Number, ENcf, Total, Discount, DiscountPercent, Permissions }` |
| `ventas.anular` | `sales.void` | `Venta` → `Sale` | `{ Numero, ENcf, Total, Motivo, NotaCredito }` → `{ Number, ENcf, Total, Reason, CreditNote }` |
| `ventas.devolver` | `sales.return` | `Venta` → `Sale` | `{ Devolucion, Total, Medio, Motivo, NotaCredito }` → `{ Return, Total, Method, Reason, CreditNote }` |

## Integration events

Published (phase A names): `SaleRecordedV1`, `SaleVoidedV1`, `ReturnRecordedV1`; `SalePaymentV1.Method` and `ReturnRecordedV1.Method` carry `PaymentMethod` names (`Cash`, …). Consumed: `ShiftClosedV1` (purges that shift's held sales).

## Config keys

None in this module.

## Database (schema `ventas` → `sales`)

| Old table | New table |
|---|---|
| `ventas.Ventas` | `sales.Sales` |
| `ventas.LineasVenta` | `sales.SaleLines` |
| `ventas.PagosVenta` | `sales.SalePayments` |
| `ventas.Devoluciones` | `sales.Returns` |
| `ventas.LineasDevolucion` | `sales.ReturnLines` |
| `ventas.VentasEnEspera` | `sales.HeldSales` |

Entity types: `Venta` → `Sale`, `LineaVenta` → `SaleLine`, `PagoVenta` → `SalePayment`, `Devolucion` → `SaleReturn`, `LineaDevolucion` → `ReturnLine`, `VentaEnEspera` → `HeldSale`.

Columns (entity property = column):

| Table | Old | New |
|---|---|---|
| Sales | `Numero` (PK, identity) | `Number` |
| Sales | `Id` (alternate key) | `Id` |
| Sales | `TurnoId` | `ShiftId` |
| Sales | `CajaId` | `RegisterId` |
| Sales | `Caja` | `Register` |
| Sales | `CajeroId` | `CashierId` |
| Sales | `Cajero` | `Cashier` |
| Sales | `ClienteId` | `CustomerId` |
| Sales | `ClienteNombre` | `CustomerName` |
| Sales | `RncCliente` | `CustomerRnc` |
| Sales | `TipoComprobante` (int) | `DocumentType` (int) |
| Sales | `ENcf` | `ENcf` |
| Sales | `CodigoSeguridad` | `SecurityCode` |
| Sales | `UrlQr` | `QrUrl` |
| Sales | `Nivel` (`Detalle`/`Mayorista`) | `PriceLevel` (`Retail`/`Wholesale`) |
| Sales | `Bruto` | `Gross` |
| Sales | `Descuento` | `Discount` |
| Sales | `Subtotal`, `Itbis`, `Total` | (unchanged) |
| Sales | `Cambio` | `Change` |
| Sales | `Redondeo` | `Rounding` |
| Sales | `Estado` (`Completada`/`Anulada`) | `Status` (`Completed`/`Voided`) |
| Sales | `Version` (concurrency token) | `Version` |
| Sales | `FechaUtc` | `DateUtc` |
| Sales | `AnuladaUtc` | `VoidedUtc` |
| Sales | `MotivoAnulacion` | `VoidReason` |
| Sales | `AnuladaPorId` | `VoidedById` |
| Sales | `ENcfAnulacion` | `VoidENcf` |
| Sales | `SupervisorId` | `SupervisorId` |
| SaleLines | `Id` | `Id` |
| SaleLines | `VentaId` | `SaleId` |
| SaleLines | `Orden` | `Position` |
| SaleLines | `ProductoId` | `ProductId` |
| SaleLines | `PresentacionId` | `PresentationId` |
| SaleLines | `CategoriaId` | `CategoryId` |
| SaleLines | `Descripcion` | `Description` |
| SaleLines | `Unidad` | `Unit` (int) |
| SaleLines | `Cantidad` | `Quantity` |
| SaleLines | `Factor` | `Factor` |
| SaleLines | `PrecioLista` | `ListPrice` |
| SaleLines | `PrecioUnitario` | `UnitPrice` |
| SaleLines | `Bruto` | `Gross` |
| SaleLines | `Descuento` | `Discount` |
| SaleLines | `TasaItbis` (string) | `ItbisRate` (string: `General18`, `Reduced16`, `Zero`, `Exempt`) |
| SaleLines | `Itbis`, `Total` | (unchanged) |
| SaleLines | `CostoUnitario` | `UnitCost` |
| SaleLines | `Nota` | `Note` |
| SaleLines | `CodigoEtiqueta` | `ScaleLabel` |
| SaleLines | `CantidadDevuelta` | `ReturnedQuantity` |
| SalePayments | `Id` | `Id` |
| SalePayments | `VentaId` | `SaleId` |
| SalePayments | `Medio` (string) | `Method` (string, `PaymentMethod` name) |
| SalePayments | `Monto` | `Amount` |
| SalePayments | `Recibido` | `Received` |
| SalePayments | `Referencia` | `Reference` |
| Returns | `Id` | `Id` |
| Returns | `VentaId` | `SaleId` |
| Returns | `TurnoId` | `ShiftId` |
| Returns | `Medio` | `Method` |
| Returns | `Total`, `Itbis`, `ENcf` | (unchanged) |
| Returns | `Motivo` | `Reason` |
| Returns | `FechaUtc` | `DateUtc` |
| Returns | `UsuarioId` | `UserId` |
| Returns | `SupervisorId` | `SupervisorId` |
| ReturnLines | `DevolucionId` (PK part) | `ReturnId` |
| ReturnLines | `LineaVentaId` (PK part) | `SaleLineId` |
| ReturnLines | `Cantidad` | `Quantity` |
| ReturnLines | `Total`, `Itbis` | (unchanged) |
| ReturnLines | `Repuesto` | `Restocked` |
| HeldSales | `Id` | `Id` |
| HeldSales | `TurnoId` | `ShiftId` |
| HeldSales | `UsuarioId` | `UserId` |
| HeldSales | `Etiqueta` | `Label` |
| HeldSales | `Contenido` | `Content` |
| HeldSales | `Total` | `Total` |
| HeldSales | `CreadaUtc` | `CreatedUtc` |

Indexes: `IX_Sales_ShiftId`, `IX_Sales_DateUtc`, `IX_Sales_CustomerId`, `AK_Sales_Id`, `IX_SaleLines_ProductId`, `IX_SaleLines_SaleId`, `IX_SalePayments_SaleId`, `IX_Returns_SaleId`, `IX_HeldSales_ShiftId`. No seed data.

Migration command used (from the repo root):

```
dotnet tool restore
dotnet dotnet-ef migrations add InitialSales --project src/Modules/Sales/Abastalia.Sales --startup-project src/Modules/Sales/Abastalia.Sales --context SalesDbContext --output-dir Persistence/Migrations --no-build
```
