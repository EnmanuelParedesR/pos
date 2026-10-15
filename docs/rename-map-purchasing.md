# Rename map, phase B: Purchasing module

Spanish → English renames in `src/Modules/Purchasing/Toki.Purchasing/**`. Behavior did not change: only names, comments, routes, JSON fields, enum values, codes, audit strings and the database schema. Follows `docs/glosario-codigo.md` and `docs/rename-map.md` (the Contracts names, `Toki.Purchasing.Contracts`, were already renamed in phase A).

The API uses camelCase JSON and string enums, so every property below appears on the wire as its camelCase form (`SupplierId` → `supplierId`) and every enum value as its English name (`"Contado"` → `"Cash"`).

## Files

| Old | New |
|---|---|
| `ComprasModule.cs` | `PurchasingModule.cs` |
| `Domain/Compras.cs` | `Domain/Purchasing.cs` |
| `Features/Suplidores.cs` | `Features/Suppliers.cs` |
| `Features/ComprasMercancia.cs` | `Features/Purchases.cs` |
| `Features/GastosYPagos.cs` | `Features/ExpensesAndPayments.cs` |
| `Persistence/Migraciones/` (`InicialCompras`, `MedioPagoCompras`, `ComprasDbContextModelSnapshot`) | `Persistence/Migrations/` (`20261015152534_InitialPurchasing`, `PurchasingDbContextModelSnapshot`, regenerated) |

## Module registration and internal types (for the Host and tests)

| Old | New |
|---|---|
| `ComprasModule` | `PurchasingModule` |
| `ComprasModule.AddComprasModule(services)` | `PurchasingModule.AddPurchasingModule(services)` |
| `ComprasDbContext` | `PurchasingDbContext` |
| `ComprasDbContext.Suplidores` | `PurchasingDbContext.Suppliers` |
| `ComprasDbContext.Compras` | `PurchasingDbContext.Purchases` |
| `ComprasDbContext.Gastos` | `PurchasingDbContext.Expenses` |
| `ComprasDbContext.CuentasPorPagar` | `PurchasingDbContext.Payables` |
| `ComprasDbContext.Pagos` | `PurchasingDbContext.Payments` |
| `ComprasDbContext.Aplicaciones` | `PurchasingDbContext.Allocations` |
| `ComprasDbContext.BloquearSuplidorAsync(id, ct)` | `PurchasingDbContext.LockSupplierAsync(id, ct)` |
| `ComprasDbContext.BloquearCompraAsync(id, ct)` | `PurchasingDbContext.LockPurchaseAsync(id, ct)` |
| `ComprasDbContext.TienePagosAsync(cuentaId, ct)` | `PurchasingDbContext.HasPaymentsAsync(payableId, ct)` |
| `ComprasDbContext.AbiertasAsync(suplidorId, ct)` → `(Cuenta, Saldo)` | `PurchasingDbContext.OpenPayablesAsync(supplierId, ct)` → `(Payable, Balance)` |
| `ComprasConfiguration` | `PurchasingConfiguration` |
| `DisenoCompras` | `PurchasingDesignTimeFactory` |
| `Features.ComprasReportes` (implements `IPurchasingReports`) | `Features.PurchasingReports` |
| `ComprasReportes.PorPagarAsync` | `PurchasingReports.PayablesAsync` |
| `ComprasReportes.Documentos606Async(desde, hasta, ct)` | `PurchasingReports.Report606Async(from, to, ct)` |
| `ComprasErrors` | `PurchasingErrors` |
| `FechaDocumento.EsValida(fecha, reloj)` / `.Mensaje` | `DocumentDate.IsValid(date, clock)` / `.Message` |
| `Ncf.EsValido(ncf)` (`Domain`) | `Ncf.IsValid(ncf)` |
| `Compra.ToleranciaFactura` | `Purchase.InvoiceTolerance` |
| `Compra.Recalcular(itbisFactura, totalFactura)` | `Purchase.Recalculate(invoiceItbis, invoiceTotal)` |

### Feature slices (static classes; each has `Command`, `Handler`, `Validator`, `Endpoint`, `Body` as before)

| Old | New |
|---|---|
| `GuardarSuplidor` | `SaveSupplier` |
| `GuardarCompra` | `SavePurchase` |
| `GuardarCompra.Linea` | `SavePurchase.Line` |
| `GuardarCompra.Endpoint.Comando` (local fn) | `SavePurchase.Endpoint.ToCommand` |
| `AplicarCompra` | `PostPurchase` |
| `AnularCompra` | `VoidPurchase` |
| `ConsultarCompras` | `QueryPurchases` |
| `ConsultarCompras.Item` | `QueryPurchases.Item` |
| `RegistrarGasto` | `RecordExpense` |
| `AnularGasto` | `VoidExpense` |
| `PagarSuplidor` | `PaySupplier` |
| `PagarSuplidor.Aplicado` | `PaySupplier.Allocated` |
| `AnularPagoSuplidor` | `VoidSupplierPayment` |
| `EstadoCuentaSuplidor` | `SupplierStatement` |
| `EstadoCuentaSuplidor.Movimiento` | `SupplierStatement.Line` |

## Routes

All under `/api/purchasing`, permission `purchasing.manage` (was `compras.gestionar`), OpenAPI tag `Purchasing` (was `Compras`).

| Old | New |
|---|---|
| `POST /api/compras/suplidores` | `POST /api/purchasing/suppliers` (201, `Location: /api/purchasing/suppliers/{id}`) |
| `PUT /api/compras/suplidores/{id}` | `PUT /api/purchasing/suppliers/{id}` |
| `GET /api/compras/suplidores?buscar=` | `GET /api/purchasing/suppliers?search=` |
| `GET /api/compras/suplidores/{id}/estado-cuenta` | `GET /api/purchasing/suppliers/{id}/statement` |
| `POST /api/compras` | `POST /api/purchasing/purchases` (201, `Location: /api/purchasing/purchases/{id}`) |
| `PUT /api/compras/{id}` | `PUT /api/purchasing/purchases/{id}` |
| `GET /api/compras/{id}` | `GET /api/purchasing/purchases/{id}` |
| `GET /api/compras?desde=&hasta=&suplidorId=` | `GET /api/purchasing/purchases?from=&to=&supplierId=` |
| `POST /api/compras/{id}/aplicar` | `POST /api/purchasing/purchases/{id}/post` |
| `POST /api/compras/{id}/anular` | `POST /api/purchasing/purchases/{id}/void` |
| `POST /api/compras/gastos` | `POST /api/purchasing/expenses` (201, `Location: /api/purchasing/expenses/{id}`) |
| `GET /api/compras/gastos?desde=&hasta=` (both required) | `GET /api/purchasing/expenses?from=&to=` (both required) |
| `POST /api/compras/gastos/{id}/anular` | `POST /api/purchasing/expenses/{id}/void` |
| `POST /api/compras/pagos` | `POST /api/purchasing/payments` |
| `POST /api/compras/pagos/{id}/anular` | `POST /api/purchasing/payments/{id}/void` |

`PaySupplier` with a `shiftId` requires `register.withdraw` (was `caja.retirar`) or a supervisor approval (`supervisorApprovalId`, was `autorizacionSupervisorId`).

## Request and response bodies (wire names)

### `POST/PUT /api/purchasing/suppliers` — `SaveSupplier.Body`

| Old | New |
|---|---|
| `nombre` | `name` |
| `rnc` | `rnc` |
| `telefono` | `phone` |
| `contacto` | `contact` |
| `diasCredito` | `creditDays` |
| `activo` (default true) | `isActive` (default true) |

Response (POST): `SaveSupplier.Response` `{ id }` (unchanged).

### `GET /api/purchasing/suppliers` — array of `Supplier` entities

| Old | New |
|---|---|
| `id` | `id` |
| `nombre` | `name` |
| `rnc` | `rnc` |
| `telefono` | `phone` |
| `contacto` | `contact` |
| `diasCredito` | `creditDays` |
| `activo` | `isActive` |

### `GET /api/purchasing/suppliers/{id}/statement` — `SupplierStatement.Response`

| Old | New |
|---|---|
| `suplidorId` | `supplierId` |
| `suplidor` | `supplier` |
| `saldo` | `balance` |
| `movimientos` | `lines` |

Each line (`SupplierStatement.Line`, was `Movimiento`):

| Old | New |
|---|---|
| `fecha` | `date` |
| `tipo` | `type` |
| `documento` | `document` |
| `cargo` | `charge` |
| `abono` | `payment` |
| `saldo` | `balance` |
| `vence` | `dueDate` |

The `type` and `document` values stay Spanish (owner-facing text, same as before): `type` is `"Compra"`, `"Gasto"` or `"Pago"`; `document` is the NCF, the payment reference, or, without a reference, the method in Spanish (`"Efectivo"`, `"Transferencia"`, `"Cheque"`, `"Tarjeta"`). The code now maps those texts explicitly instead of using the enum name.

### `POST /api/purchasing/purchases`, `PUT /api/purchasing/purchases/{id}` — `SavePurchase.Body`

| Old | New |
|---|---|
| `suplidorId` | `supplierId` |
| `ncf` | `ncf` |
| `fechaFactura` | `invoiceDate` |
| `condicion` (`Contado`/`Credito`) | `terms` (`Cash`/`Credit`) |
| `lineas` | `lines` |
| `almacenId` | `warehouseId` |
| `diasCredito` | `creditDays` |
| `notas` | `notes` |
| `itbisFactura` | `invoiceItbis` |
| `totalFactura` | `invoiceTotal` |
| `id` (POST only, client GUID v7) | `id` |
| `medio` | `method` |

Each line (`SavePurchase.Line`, was `Linea`):

| Old | New |
|---|---|
| `productoId` | `productId` |
| `presentacionId` | `presentationId` |
| `cantidad` | `quantity` |
| `costoUnitario` | `unitCost` |
| `tasaItbis` (optional) | `itbisRate` (optional; values per `ItbisRate`: `General18`, `Reduced16`, `Zero`, `Exempt`) |

Internal `SavePurchase.Command`: `Nueva` → `IsNew`, the rest as above (`Id, IsNew, SupplierId, Ncf, InvoiceDate, Terms, Lines, WarehouseId, CreditDays, Notes, InvoiceItbis, InvoiceTotal, Method`).

Response `SavePurchase.Response`: `{ id, subtotal, itbis, total }` (unchanged).

### `POST /api/purchasing/purchases/{id}/void`, `/expenses/{id}/void`, `/payments/{id}/void` — `Body`

| Old | New |
|---|---|
| `motivo` | `reason` |

`POST /api/purchasing/purchases/{id}/post` has no body (204).

### `GET /api/purchasing/purchases` — array of `QueryPurchases.Item`

| Old | New |
|---|---|
| `id` | `id` |
| `numero` | `number` |
| `suplidor` | `supplier` |
| `ncf` | `ncf` |
| `fechaFactura` | `invoiceDate` |
| `estado` (string: `Borrador`/`Aplicada`/`Anulada`) | `status` (string: `Draft`/`Posted`/`Voided`) |
| `condicion` (string: `Contado`/`Credito`) | `terms` (string: `Cash`/`Credit`) |
| `total` | `total` |

### `GET /api/purchasing/purchases/{id}` — `Purchase` entity with its lines

| Old | New |
|---|---|
| `numero` | `number` |
| `id` | `id` |
| `suplidorId` | `supplierId` |
| `suplidorNombre` | `supplierName` |
| `rncSuplidor` | `supplierRnc` |
| `ncf` | `ncf` |
| `fechaFactura` | `invoiceDate` |
| `condicion` | `terms` |
| `medio` | `method` |
| `vence` | `dueDate` |
| `almacenId` | `warehouseId` |
| `estado` | `status` |
| `subtotal`, `itbis`, `total` | (unchanged) |
| `notas` | `notes` |
| `creadaUtc` | `createdUtc` |
| `aplicadaUtc` | `postedUtc` |
| `anuladaUtc` | `voidedUtc` |
| `motivoAnulacion` | `voidReason` |
| `usuarioId` | `userId` |
| `version` | `version` |
| `lineas` | `lines` |

Each line (`PurchaseLine`, was `LineaCompra`):

| Old | New |
|---|---|
| `id` | `id` |
| `compraId` | `purchaseId` |
| `orden` | `lineNumber` |
| `productoId` | `productId` |
| `descripcion` | `description` |
| `cantidad` | `quantity` |
| `factor` | `factor` |
| `costoUnitario` | `unitCost` |
| `tasaItbis` | `itbisRate` |
| `subtotal`, `itbis` | (unchanged) |

### `POST /api/purchasing/expenses` — `RecordExpense.Command` (bound directly)

| Old | New |
|---|---|
| `suplidorId` | `supplierId` |
| `ncf` | `ncf` |
| `fecha` | `date` |
| `concepto` | `description` |
| `tipo` | `type` (`ExpenseType`, see enums) |
| `subtotal` | `subtotal` |
| `itbis` | `itbis` |
| `condicion` | `terms` |
| `diasCredito` | `creditDays` |
| `id` | `id` |
| `medio` | `method` |

Response `RecordExpense.Response`: `{ id, total }` (unchanged).

### `GET /api/purchasing/expenses` — array of `Expense` entities

| Old | New |
|---|---|
| `id` | `id` |
| `suplidorId` | `supplierId` |
| `suplidorNombre` | `supplierName` |
| `rncSuplidor` | `supplierRnc` |
| `ncf` | `ncf` |
| `fecha` | `date` |
| `concepto` | `description` |
| `tipo` | `type` |
| `condicion` | `terms` |
| `medio` | `method` |
| `vence` | `dueDate` |
| `subtotal`, `itbis`, `total` | (unchanged) |
| `anulado` | `isVoided` |
| `motivoAnulacion` | `voidReason` |
| `creadoUtc` | `createdUtc` |
| `usuarioId` | `userId` |

### `POST /api/purchasing/payments` — `PaySupplier.Command` (bound directly)

| Old | New |
|---|---|
| `suplidorId` | `supplierId` |
| `monto` | `amount` |
| `medio` | `method` |
| `fecha` | `date` |
| `referencia` | `reference` |
| `cuentas` (payable ids) | `payableIds` |
| `pagoId` (client GUID v7) | `supplierPaymentId` |
| `turnoId` | `shiftId` |
| `autorizacionSupervisorId` | `supervisorApprovalId` |

Computed `IRequiresApproval` members (not bound from JSON): `Permiso` → `Permission`, `RecursoId` → `ResourceId`.

Response `PaySupplier.Response`:

| Old | New |
|---|---|
| `pagoId` | `supplierPaymentId` |
| `aplicaciones` | `allocations` |

Each allocation (`PaySupplier.Allocated`, was `Aplicado`):

| Old | New |
|---|---|
| `cuentaId` | `payableId` |
| `ncf` | `ncf` |
| `monto` | `amount` |
| `saldoRestante` | `remainingBalance` |

## Enums

| Old | New | Values old → new |
|---|---|---|
| `EstadoCompra` (internal) | `PurchaseStatus` | `Borrador` → `Draft`, `Aplicada` → `Posted`, `Anulada` → `Voided` |
| `Condicion` (public) | `PaymentTerms` | `Contado` → `Cash`, `Credito` → `Credit` |
| `MedioPagoSuplidor` (public) | `SupplierPaymentMethod` | `Efectivo` → `Cash`, `Transferencia` → `Transfer`, `Cheque` → `Check`, `Tarjeta` → `Card` |
| `TipoDocumento` (internal) | `PayableDocumentType` | `Compra` → `Purchase`, `Gasto` → `Expense` |
| `TipoGasto` (public, DGII 606 codes; stored as int) | `ExpenseType` | `GastosPersonal = 1` → `PersonnelExpenses`, `TrabajosSuministrosServicios = 2` → `WorkSuppliesAndServices`, `Arrendamientos = 3` → `Leases`, `GastosActivoFijo = 4` → `FixedAssetExpenses`, `GastosRepresentacion = 5` → `RepresentationExpenses`, `OtrasDeduccionesAdmitidas = 6` → `OtherAllowedDeductions`, `GastosFinancieros = 7` → `FinancialExpenses`, `GastosExtraordinarios = 8` → `ExtraordinaryExpenses`, `CostoDeVenta = 9` → `CostOfSales`, `AdquisicionesActivos = 10` → `AssetAcquisitions`, `GastosSeguros = 11` → `InsuranceExpenses` |

Uses from other projects: `TasaItbis` → `ItbisRate`, `TipoMovimiento.Compra` / `AnulacionCompra` → `StockMovementType.Purchase` / `PurchaseVoid`.

## Domain types

| Old | New | Members old → new |
|---|---|---|
| `Suplidor` | `Supplier` | `Id`, `Nombre` → `Name`, `Rnc`, `Telefono` → `Phone`, `Contacto` → `Contact`, `DiasCredito` → `CreditDays`, `Activo` → `IsActive` |
| `Compra` | `Purchase` | `Numero` → `Number`, `Id`, `SuplidorId` → `SupplierId`, `SuplidorNombre` → `SupplierName`, `RncSuplidor` → `SupplierRnc`, `Ncf`, `FechaFactura` → `InvoiceDate`, `Condicion` → `Terms`, `Medio` → `Method`, `Vence` → `DueDate`, `AlmacenId` → `WarehouseId`, `Estado` → `Status`, `Subtotal`, `Itbis`, `Total`, `Notas` → `Notes`, `CreadaUtc` → `CreatedUtc`, `AplicadaUtc` → `PostedUtc`, `AnuladaUtc` → `VoidedUtc`, `MotivoAnulacion` → `VoidReason`, `UsuarioId` → `UserId`, `Version`, `Lineas` → `Lines` |
| `LineaCompra` | `PurchaseLine` | `Id`, `CompraId` → `PurchaseId`, `Orden` → `LineNumber`, `ProductoId` → `ProductId`, `Descripcion` → `Description`, `Cantidad` → `Quantity`, `Factor`, `CostoUnitario` → `UnitCost`, `TasaItbis` → `ItbisRate`, `Subtotal`, `Itbis` |
| `Gasto` | `Expense` | `Id`, `SuplidorId` → `SupplierId`, `SuplidorNombre` → `SupplierName`, `RncSuplidor` → `SupplierRnc`, `Ncf`, `Fecha` → `Date`, `Concepto` → `Description`, `Tipo` → `Type`, `Condicion` → `Terms`, `Medio` → `Method`, `Vence` → `DueDate`, `Subtotal`, `Itbis`, `Total`, `Anulado` → `IsVoided`, `MotivoAnulacion` → `VoidReason`, `CreadoUtc` → `CreatedUtc`, `UsuarioId` → `UserId` |
| `CuentaPorPagar` | `Payable` | `Id`, `SuplidorId` → `SupplierId`, `Tipo` → `Type`, `DocumentoId` → `DocumentId`, `Ncf`, `Fecha` → `Date`, `Vence` → `DueDate`, `Total`, `Anulada` → `IsVoided` |
| `PagoSuplidor` | `SupplierPayment` | `Id`, `SuplidorId` → `SupplierId`, `Fecha` → `Date`, `Monto` → `Amount`, `Medio` → `Method`, `Referencia` → `Reference`, `TurnoId` → `ShiftId`, `CreadoUtc` → `CreatedUtc`, `UsuarioId` → `UserId`, `Anulado` → `IsVoided`, `MotivoAnulacion` → `VoidReason`, `Aplicaciones` → `Allocations` |
| `AplicacionPago` | `PaymentAllocation` | `PagoId` → `SupplierPaymentId`, `CuentaId` → `PayableId`, `Monto` → `Amount` |

## Error codes (`detail` texts unchanged, Spanish)

| `PurchasingErrors` member (was `ComprasErrors.…`) | Old code | New code |
|---|---|---|
| `SupplierNotFound` (`SuplidorNoEncontrado`) | `Compras.SuplidorNoEncontrado` | `Purchasing.SupplierNotFound` |
| `PurchaseNotFound` (`CompraNoEncontrada`) | `Compras.CompraNoEncontrada` | `Purchasing.PurchaseNotFound` |
| `NotDraft` (`NoEsBorrador`) | `Compras.NoEsBorrador` | `Purchasing.NotDraft` |
| `DuplicateNcf` (`NcfRepetido`) | `Compras.NcfRepetido` | `Purchasing.DuplicateNcf` |
| `HasPayments` (`ConPagos`) | `Compras.ConPagos` | `Purchasing.HasPayments` |
| `PaymentExceedsDebt` (`PagoMayorQueDeuda`) | `Compras.PagoMayorQueDeuda` | `Purchasing.PaymentExceedsDebt` |
| `ProductNotFound` (`ProductoNoEncontrado`) | `Compras.ProductoNoEncontrado` | `Purchasing.ProductNotFound` |
| `AlreadyVoided` (`YaAnulado`) | `Compras.YaAnulada` | `Purchasing.AlreadyVoided` |
| `SupplierWithoutRnc` (`SuplidorSinRnc`) | `Compras.SuplidorSinRnc` | `Purchasing.SupplierWithoutRnc` |
| `InvoiceTotalMismatch` (`TotalFacturaLejos`) | `Compras.TotalFacturaLejos` | `Purchasing.InvoiceTotalMismatch` |
| `PaymentNotFound` (`PagoNoEncontrado`) | `Compras.PagoNoEncontrado` | `Purchasing.PaymentNotFound` |
| `ExpenseNotFound` (`GastoNoEncontrado`) | `Compras.GastoNoEncontrado` | `Purchasing.ExpenseNotFound` |

Validation messages (FluentValidation) are unchanged and Spanish; their property names in the `errors` dictionary follow the new wire names (`Nombre` → `Name`, `Lineas[0].Cantidad` → `Lines[0].Quantity`, `Monto` → `Amount`, `FechaFactura` → `InvoiceDate`, `Fecha` → `Date`, `Concepto` → `Description`, `Motivo` → `Reason`, …).

## Audit actions

| Old action, resource type, data | New action, resource type, data |
|---|---|
| `compras.guardar-suplidor`, `Suplidor`, the command | `purchasing.save-supplier`, `Supplier`, the command |
| `compras.aplicar`, `Compra`, `{ Numero, Ncf, Total, Condicion }` | `purchasing.post`, `Purchase`, `{ Number, Ncf, Total, Terms }` |
| `compras.anular`, `Compra`, `{ Numero, Ncf, Total, Motivo }` | `purchasing.void`, `Purchase`, `{ Number, Ncf, Total, Reason }` |
| `compras.gasto`, `Gasto`, the command | `purchasing.record-expense`, `Expense`, the command |
| `compras.anular-gasto`, `Gasto`, `{ Ncf, Total, Motivo }` | `purchasing.void-expense`, `Expense`, `{ Ncf, Total, Reason }` |
| `compras.pago-suplidor`, `Suplidor`, `{ Pago, Monto, Medio, Referencia, TurnoId }` | `purchasing.pay-supplier`, `Supplier`, `{ Payment, Amount, Method, Reference, ShiftId }` |
| `compras.anular-pago`, `Suplidor`, `{ Pago, Monto, Motivo }` | `purchasing.void-payment`, `Supplier`, `{ Payment, Amount, Reason }` |

## Integration events published (names from phase A)

| Old | New |
|---|---|
| `CompraRegistradaV1` (on apply) | `PurchasePostedV1` (on post) |
| `CompraAnuladaV1` (void of an applied purchase) | `PurchaseVoidedV1` (void of a posted purchase) |
| `PagoSuplidorRegistradoV1` | `SupplierPaymentRecordedV1` |

## Config keys

None in this module.

## Database (schema `compras` → `purchasing`)

| Old table | New table |
|---|---|
| `compras.Suplidores` | `purchasing.Suppliers` |
| `compras.Compras` | `purchasing.Purchases` |
| `compras.LineasCompra` | `purchasing.PurchaseLines` |
| `compras.Gastos` | `purchasing.Expenses` |
| `compras.CuentasPorPagar` | `purchasing.Payables` |
| `compras.PagosSuplidor` | `purchasing.SupplierPayments` |
| `compras.AplicacionesPago` | `purchasing.PaymentAllocations` |

Columns are the domain property names above (old → new):

| Table | Columns old → new |
|---|---|
| Suppliers | `Id`, `Nombre` → `Name`, `Rnc`, `Telefono` → `Phone`, `Contacto` → `Contact`, `DiasCredito` → `CreditDays`, `Activo` → `IsActive` |
| Purchases | `Numero` → `Number` (PK, identity), `Id` (alternate key), `SuplidorId` → `SupplierId`, `SuplidorNombre` → `SupplierName`, `RncSuplidor` → `SupplierRnc`, `Ncf`, `FechaFactura` → `InvoiceDate`, `Condicion` → `Terms`, `Medio` → `Method`, `Vence` → `DueDate`, `AlmacenId` → `WarehouseId`, `Estado` → `Status`, `Subtotal`, `Itbis`, `Total`, `Notas` → `Notes`, `CreadaUtc` → `CreatedUtc`, `AplicadaUtc` → `PostedUtc`, `AnuladaUtc` → `VoidedUtc`, `MotivoAnulacion` → `VoidReason`, `UsuarioId` → `UserId`, `Version` |
| PurchaseLines | `Id`, `CompraId` → `PurchaseId`, `Orden` → `LineNumber`, `ProductoId` → `ProductId`, `Descripcion` → `Description`, `Cantidad` → `Quantity`, `Factor`, `CostoUnitario` → `UnitCost`, `TasaItbis` → `ItbisRate`, `Subtotal`, `Itbis` |
| Expenses | `Id`, `SuplidorId` → `SupplierId`, `SuplidorNombre` → `SupplierName`, `RncSuplidor` → `SupplierRnc`, `Ncf`, `Fecha` → `Date`, `Concepto` → `Description`, `Tipo` → `Type` (int), `Condicion` → `Terms`, `Medio` → `Method`, `Vence` → `DueDate`, `Subtotal`, `Itbis`, `Total`, `Anulado` → `IsVoided`, `MotivoAnulacion` → `VoidReason`, `CreadoUtc` → `CreatedUtc`, `UsuarioId` → `UserId` |
| Payables | `Id`, `SuplidorId` → `SupplierId`, `Tipo` → `Type`, `DocumentoId` → `DocumentId`, `Ncf`, `Fecha` → `Date`, `Vence` → `DueDate`, `Total`, `Anulada` → `IsVoided` |
| SupplierPayments | `Id`, `SuplidorId` → `SupplierId`, `Fecha` → `Date`, `Monto` → `Amount`, `Medio` → `Method`, `Referencia` → `Reference`, `TurnoId` → `ShiftId`, `CreadoUtc` → `CreatedUtc`, `UsuarioId` → `UserId`, `Anulado` → `IsVoided`, `MotivoAnulacion` → `VoidReason` |
| PaymentAllocations | `PagoId` → `SupplierPaymentId`, `CuentaId` → `PayableId`, `Monto` → `Amount` (PK `SupplierPaymentId, PayableId`) |

Stored enum strings are now English: `Status` (`Draft`/`Posted`/`Voided`), `Terms` (`Cash`/`Credit`), `Method` (`Cash`/`Transfer`/`Check`/`Card`), `Payables.Type` (`Purchase`/`Expense`), `PurchaseLines.ItbisRate` (`General18`/`Reduced16`/`Zero`/`Exempt`). `Expenses.Type` stays an int (DGII code). Column types and lengths are unchanged.

Indexes: `IX_Suppliers_Name`; `IX_Purchases_InvoiceDate`; `IX_Purchases_SupplierId_Ncf` (unique, filter `[Status] <> 'Voided'`, was `[Estado] <> 'Anulada'`); `IX_PurchaseLines_PurchaseId`; `IX_Expenses_Date`; `IX_Expenses_SupplierId_Ncf` (unique, filter `[IsVoided] = 0`, was `[Anulado] = 0`); `IX_Payables_SupplierId`; `IX_Payables_DocumentId`; `IX_SupplierPayments_SupplierId`; `IX_PaymentAllocations_PayableId`. FKs: `FK_PurchaseLines_Purchases_PurchaseId`, `FK_PaymentAllocations_SupplierPayments_SupplierPaymentId`.

No seed data in this module.

Migration command used (from `src/Modules/Purchasing/Toki.Purchasing`):

```
dotnet tool restore
dotnet dotnet-ef migrations add InitialPurchasing --project . --startup-project . --context PurchasingDbContext --output-dir Persistence/Migrations --no-build
```

## Reports (consumers: `Toki.Reports`)

`IPurchasingReports` names are from phase A. The 606 rows (`Report606Row`) keep the DGII column order and values; only the C# member names changed (see `docs/rename-map.md`).
