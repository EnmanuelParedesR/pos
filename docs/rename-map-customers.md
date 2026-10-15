# Rename map, phase B: Customers module

Spanish → English renames in `src/Modules/Customers/Toki.Customers/**`. Behavior did not change: only names, comments, routes, JSON fields, enum values, error codes, audit strings and the database names. Contracts names come from `docs/rename-map.md` (`Toki.Customers.Contracts`, `Toki.CashRegister.Contracts`, building blocks).

The API uses camelCase JSON and string enums, so every property below appears on the wire in camelCase (`CreditLimit` → `creditLimit`).

## Files

| Old | New |
|---|---|
| `ClientesModule.cs` | `CustomersModule.cs` |
| `Domain/Clientes.cs` | `Domain/Customers.cs` |
| `Features/Clientes.cs` | `Features/Customers.cs` |
| `Persistence/Migraciones/` (`InicialClientes`, `OrigenAbonoClientes`, `ClientesDbContextModelSnapshot`) | `Persistence/Migrations/` (`20261015152420_InitialCustomers`, `CustomersDbContextModelSnapshot`, regenerated) |

## Types other code references

| Old | New |
|---|---|
| `Toki.Customers.ClientesModule` | `Toki.Customers.CustomersModule` |
| `ClientesModule.AddClientesModule(services)` | `CustomersModule.AddCustomersModule(services)` |
| `ClientesDbContext` (internal) | `CustomersDbContext` |
| `DisenoClientes : DisenoDbContext<ClientesDbContext>` (internal) | `CustomersDesignTimeFactory : DesignTimeFactory<CustomersDbContext>` |
| `CuentasClientes : ICuentasClientes` (internal) | `CustomerAccounts : ICustomerAccounts` |
| `Features.ClientesReportes : IClientesReportes` (internal) | `Features.CustomerReports : ICustomerReports` |
| `ClientesConfiguration` (internal) | `CustomersConfiguration` |
| `ClientesErrors` (internal) | `CustomersErrors` |

Host: `typeof(ClientesModule).Assembly` → `typeof(CustomersModule).Assembly`, `builder.Services.AddClientesModule()` → `builder.Services.AddCustomersModule()`.

The migrations guard test finds the design-time factory by its context name: now `CustomersDbContext`.

### `CustomersDbContext` members

| Old | New |
|---|---|
| `Clientes` | `Customers` |
| `Cargos` | `Charges` |
| `Abonos` | `Payments` |
| `Aplicaciones` | `Applications` |
| `MovimientosSaldo` | `BalanceMovements` |
| `BloquearAsync(clienteId, ct)` | `LockAsync(customerId, ct)` |
| `CargosAbiertosAsync(clienteId, ct)` → `List<(CargoFiado Cargo, decimal Saldo)>` | `OpenChargesAsync(customerId, ct)` → `List<(OnAccountCharge Charge, decimal Balance)>` |
| `PagadoAsync(cargoId, ct)` | `PaidAsync(chargeId, ct)` |
| `SaldoFavorAsync(clienteId, ct)` | `CreditBalanceAsync(customerId, ct)` |

### `CustomerAccounts` private members

| Old | New |
|---|---|
| `Abonar(clienteId, origen, referenciaId, monto, abiertos)` | `ApplyPayment(customerId, source, referenceId, amount, open)` |
| `AgregarSaldo(clienteId, tipo, monto, referenciaId)` | `AddBalance(customerId, type, amount, referenceId)` |

### Domain (`Toki.Customers.Domain`, internal)

| Old | New |
|---|---|
| `Cliente` | `Customer` |
| `CargoFiado` | `OnAccountCharge` |
| `OrigenAbono` | `CustomerPaymentSource` |
| `AbonoFiado` | `CustomerPayment` |
| `AplicacionAbono` | `CustomerPaymentApplication` |
| `TipoMovimientoSaldo` | `CreditBalanceMovementType` |
| `MovimientoSaldo` | `CreditBalanceMovement` |

Entity properties are listed with their columns under **Database**.

### Feature classes (`Toki.Customers.Features`, internal)

| Old | New |
|---|---|
| `GuardarCliente` (`Command`, `Response`, `Validator`, `Handler`, `Body`, `Endpoint`) | `SaveCustomer` (same nested names) |
| `ConsultarClientes` (`Item`, `Endpoint`) | `SearchCustomers` (same nested names) |
| `RegistrarAbono` (`Command`, `Response`, `Validator`, `Handler`, `Body`, `Endpoint`) | `RecordCustomerPayment` (same nested names) |
| `RegistrarAbono.Handler.GuardadoAsync` (private) | `RecordCustomerPayment.Handler.SavedAsync` |
| `AnularAbono` (`Command`, `Validator`, `Handler`, `Body`, `Endpoint`) | `VoidCustomerPayment` (same nested names) |
| `EstadoCuentaCliente` (`Movimiento`, `Response`, `Endpoint`) | `CustomerStatement` (`StatementLine`, `Response`, `Endpoint`) |
| `ClientesReportes.PorCobrarAsync(ct)` | `CustomerReports.ReceivablesAsync(ct)` |

## Routes

All endpoints: OpenAPI tag `Clientes` → `Customers`.

| Old | New | Permission (old → new) |
|---|---|---|
| `POST /api/clientes` | `POST /api/customers` | `clientes.gestionar` → `customers.manage` |
| `PUT /api/clientes/{id}` | `PUT /api/customers/{id}` | `clientes.gestionar` → `customers.manage` |
| `GET /api/clientes?buscar=&inactivos=` | `GET /api/customers?search=&includeInactive=` | `clientes.gestionar` → `customers.manage` |
| `GET /api/clientes/{id}` | `GET /api/customers/{id}` | `clientes.gestionar` → `customers.manage` |
| `POST /api/clientes/{id}/abonos` | `POST /api/customers/{id}/payments` | `caja.operar` → `register.operate` |
| `POST /api/clientes/abonos/{id}/anular` | `POST /api/customers/payments/{id}/void` | `caja.operar` → `register.operate` (+ approval `fiado.gestionar` → `credit.manage`) |
| `GET /api/clientes/{id}/estado-cuenta` | `GET /api/customers/{id}/statement` | `clientes.gestionar` → `customers.manage` |

`POST /api/customers` returns `201` with `Location: /api/customers/{id}` (was `/api/clientes/{id}`).

Other permissions checked inside handlers: `fiado.gestionar` → `credit.manage` (changing credit limit / price level / reactivating in `SaveCustomer`; listing inactive customers with `includeInactive=true`).

## Requests and responses (wire names)

### `POST /api/customers`, `PUT /api/customers/{id}` — body (`SaveCustomer.Body`)

| Old | New |
|---|---|
| `nombre` | `name` |
| `rncCedula` | `taxId` |
| `telefono` | `phone` |
| `direccion` | `address` |
| `creditoFiscal` | `fiscalCredit` |
| `nivel` (`"Detalle"` / `"Mayorista"`) | `priceLevel` (`"Retail"` / `"Wholesale"`) |
| `limiteCredito` | `creditLimit` |
| `activo` | `isActive` |

Response (`SaveCustomer.Response`, POST only): `id` (unchanged). PUT returns 204.

`SaveCustomer.Command(Id, Nombre, RncCedula, Telefono, Direccion, CreditoFiscal, Nivel, LimiteCredito, Activo)` → `Command(Id, Name, TaxId, Phone, Address, FiscalCredit, PriceLevel, CreditLimit, IsActive)`.

### `GET /api/customers` — array of `SearchCustomers.Item`

| Old | New |
|---|---|
| `id` | `id` |
| `nombre` | `name` |
| `rncCedula` | `taxId` |
| `telefono` | `phone` |
| `nivel` | `priceLevel` |
| `limiteCredito` | `creditLimit` |
| `activo` | `isActive` |

Query: `buscar` → `search`, `inactivos` → `includeInactive`.

### `GET /api/customers/{id}` — `CustomerForSale` (Contracts)

| Old | New |
|---|---|
| `id` | `id` |
| `nombre` | `name` |
| `rncCedula` | `taxId` |
| `creditoFiscal` | `fiscalCredit` |
| `nivel` | `priceLevel` |
| `limiteCredito` | `creditLimit` |
| `deuda` | `debt` |
| `saldoFavor` | `creditBalance` |
| `activo` | `isActive` |

### `POST /api/customers/{id}/payments` — body (`RecordCustomerPayment.Body`)

| Old | New |
|---|---|
| `abonoId` | `customerPaymentId` |
| `turnoId` | `shiftId` |
| `monto` | `amount` |
| `medio` (`"Efectivo"` / `"Tarjeta"` / `"Transferencia"`) | `method` (`"Cash"` / `"Card"` / `"Transfer"`) |
| `referencia` | `reference` |

Response (`RecordCustomerPayment.Response`):

| Old | New |
|---|---|
| `abonoId` | `customerPaymentId` |
| `aplicado` | `applied` |
| `deudaRestante` | `remainingDebt` |

`RecordCustomerPayment.Command(ClienteId, AbonoId, TurnoId, Monto, Medio, Referencia)` → `Command(CustomerId, CustomerPaymentId, ShiftId, Amount, Method, Reference)`.

### `POST /api/customers/payments/{id}/void` — body (`VoidCustomerPayment.Body`)

| Old | New |
|---|---|
| `motivo` | `reason` |
| `autorizacionSupervisorId` | `supervisorApprovalId` |

Returns 204. Approval: permission `credit.manage`, resource id = the payment id (unchanged logic). `VoidCustomerPayment.Command(AbonoId, Motivo, AutorizacionSupervisorId)` → `Command(CustomerPaymentId, Reason, SupervisorApprovalId)`.

### `GET /api/customers/{id}/statement` — `CustomerStatement.Response`

| Old | New |
|---|---|
| `clienteId` | `customerId` |
| `nombre` | `name` |
| `deuda` | `debt` |
| `saldoFavor` | `creditBalance` |
| `limiteCredito` | `creditLimit` |
| `movimientos` | `lines` |

Each line (`Movimiento` → `StatementLine`):

| Old | New |
|---|---|
| `fechaUtc` | `dateUtc` |
| `tipo` | `type` |
| `documento` | `document` |
| `cargo` | `charge` |
| `abono` | `payment` |
| `deuda` | `debt` |

The `type` values stay in Spanish (owner-facing text): `"Fiado"`, `"Venta anulada"`, `"Lo pagado de la venta anulada"`, `"Abono"`, `"Devolución"`, `"Pago de venta anulada"`, `"Abono anulado"`.

## Enum values (stored as text)

| Old | New | Column (length) |
|---|---|---|
| `OrigenAbono.Caja` / `Devolucion` / `AnulacionVenta` | `CustomerPaymentSource.Register` / `Return` / `SaleVoid` | `CustomerPayments.Source` (20) |
| `TipoMovimientoSaldo.Devolucion` / `UsoEnVenta` / `AnulacionUso` / `CargoAnuladoPagado` | `CreditBalanceMovementType.Return` / `UsedInSale` / `UseVoided` / `VoidedChargePaid` | `CreditBalanceMovements.Type` (20) |
| `NivelPrecioCliente.Detalle` / `Mayorista` (Contracts) | `PriceLevel.Retail` / `Wholesale` | `Customers.PriceLevel` (10) |
| `MedioPago.*` (CashRegister Contracts) | `PaymentMethod.Cash` / `Card` / `Transfer` / `OnAccount` / `CreditBalance` | `CustomerPayments.Method` (20) |

All names fit their columns (longest: `VoidedChargePaid` = 16, `CreditBalance` = 13, `Wholesale` = 9).

## Error codes

`detail` texts unchanged (Spanish).

| Old code | New code | `CustomersErrors` member (old → new) | Type |
|---|---|---|---|
| `Clientes.ClienteNoEncontrado` | `Customers.CustomerNotFound` | `ClienteNoEncontrado` → `CustomerNotFound` | NotFound |
| `Clientes.SinSaldoFavor` | `Customers.InsufficientCreditBalance` | `SinSaldoFavor` → `InsufficientCreditBalance` | Conflict |
| `Clientes.AbonoMayorQueDeuda` | `Customers.PaymentExceedsDebt` | `AbonoMayorQueDeuda` → `PaymentExceedsDebt` | Validation |
| `Clientes.RncDuplicado` | `Customers.DuplicateTaxId` | `RncDuplicado` → `DuplicateTaxId` | Conflict |
| `Clientes.SinPermisoCredito` | `Customers.CreditPermissionRequired` | `SinPermisoCredito` → `CreditPermissionRequired` | Forbidden |
| `Clientes.AbonoNoEncontrado` | `Customers.PaymentNotFound` | `AbonoNoEncontrado` → `PaymentNotFound` | NotFound |
| `Clientes.AbonoYaAnulado` | `Customers.PaymentAlreadyVoided` | `AbonoYaAnulado` → `PaymentAlreadyVoided` | Conflict |
| `Clientes.AbonoConFacturaAnulada` | `Customers.PaymentOnVoidedSale` | `AbonoConFacturaAnulada` → `PaymentOnVoidedSale` | Conflict |
| `Clientes.LimiteExcedido` | `Customers.CreditLimitExceeded` | `LimiteExcedido(disponible)` → `CreditLimitExceeded(available)` | Forbidden |

Cross-cutting codes the module returns through the building blocks: `Approval.Required` (was `Autorizacion.Requerida`), `Validation`, `Concurrency`, `Duplicate`, `Busy`, plus CashRegister codes from `GetOpenShiftAsync` / `RecordCustomerPaymentAsync` / `VoidMovementsAsync`.

## Audit

Resource type `Cliente` → `Customer` (all three actions; resource id = the customer id).

| Old action | New action | Data (old → new) |
|---|---|---|
| `clientes.guardar` | `customers.save` | `{ antes: { LimiteCredito, Nivel, Activo }, despues: <command> }` → `{ before: { CreditLimit, PriceLevel, IsActive }, after: <command> }` |
| `clientes.abono` | `customers.payment` | `{ Abono, Monto, aplicado, Medio, TurnoId }` → `{ Payment, Amount, applied, Method, ShiftId }` |
| `clientes.anular-abono` | `customers.void-payment` | `{ Abono, Monto, Motivo }` → `{ Payment, Amount, Reason }` |

## Integration events published

From Contracts: `CargoFiadoRegistradoV1` → `OnAccountChargeRecordedV1(CustomerId, SaleId, Amount)`, `AbonoFiadoRegistradoV1` → `CustomerPaymentRecordedV1(CustomerId, CustomerPaymentId, Amount)`.

## Config keys

None in this module.

## Database (schema `clientes` → `customers`)

| Old table | New table |
|---|---|
| `clientes.Clientes` | `customers.Customers` |
| `clientes.CargosFiado` | `customers.OnAccountCharges` |
| `clientes.AbonosFiado` | `customers.CustomerPayments` |
| `clientes.AplicacionesAbono` | `customers.CustomerPaymentApplications` |
| `clientes.MovimientosSaldoFavor` | `customers.CreditBalanceMovements` |

| Table | Old column | New column |
|---|---|---|
| Customers | `Id` | `Id` |
| Customers | `Nombre` | `Name` |
| Customers | `RncCedula` | `TaxId` |
| Customers | `Telefono` | `Phone` |
| Customers | `Direccion` | `Address` |
| Customers | `CreditoFiscal` | `FiscalCredit` |
| Customers | `Nivel` | `PriceLevel` |
| Customers | `LimiteCredito` | `CreditLimit` |
| Customers | `Activo` | `IsActive` |
| Customers | `CreadoUtc` | `CreatedUtc` |
| Customers | `Version` | `Version` |
| OnAccountCharges | `Id` | `Id` |
| OnAccountCharges | `ClienteId` | `CustomerId` |
| OnAccountCharges | `VentaId` | `SaleId` |
| OnAccountCharges | `Documento` | `Document` |
| OnAccountCharges | `FechaUtc` | `DateUtc` |
| OnAccountCharges | `Monto` | `Amount` |
| OnAccountCharges | `Anulado` | `IsVoided` |
| OnAccountCharges | `AnuladoUtc` | `VoidedUtc` |
| OnAccountCharges | `PagadoAlAnular` | `PaidAtVoid` |
| CustomerPayments | `Id` | `Id` |
| CustomerPayments | `ClienteId` | `CustomerId` |
| CustomerPayments | `FechaUtc` | `DateUtc` |
| CustomerPayments | `Monto` | `Amount` |
| CustomerPayments | `Origen` | `Source` |
| CustomerPayments | `Medio` | `Method` |
| CustomerPayments | `TurnoId` | `ShiftId` |
| CustomerPayments | `ReferenciaId` | `ReferenceId` |
| CustomerPayments | `Referencia` | `Reference` |
| CustomerPayments | `UsuarioId` | `UserId` |
| CustomerPayments | `Anulado` | `IsVoided` |
| CustomerPayments | `AnuladoUtc` | `VoidedUtc` |
| CustomerPayments | `MotivoAnulacion` | `VoidReason` |
| CustomerPaymentApplications | `AbonoId` | `CustomerPaymentId` |
| CustomerPaymentApplications | `CargoId` | `ChargeId` |
| CustomerPaymentApplications | `Monto` | `Amount` |
| CreditBalanceMovements | `Secuencia` | `Sequence` |
| CreditBalanceMovements | `Id` | `Id` |
| CreditBalanceMovements | `ClienteId` | `CustomerId` |
| CreditBalanceMovements | `Tipo` | `Type` |
| CreditBalanceMovements | `Monto` | `Amount` |
| CreditBalanceMovements | `ReferenciaId` | `ReferenceId` |
| CreditBalanceMovements | `FechaUtc` | `DateUtc` |

Keys and indexes: `PK_CustomerPaymentApplications` (`CustomerPaymentId`, `ChargeId`), `FK_CustomerPaymentApplications_CustomerPayments_CustomerPaymentId`, `IX_Customers_Name`, `IX_Customers_TaxId` (unique, filter `[TaxId] IS NOT NULL`), `IX_OnAccountCharges_CustomerId`, `IX_OnAccountCharges_SaleId`, `IX_CustomerPayments_CustomerId`, `IX_CustomerPaymentApplications_ChargeId`, `IX_CreditBalanceMovements_CustomerId`, `IX_CreditBalanceMovements_ReferenceId`. Lengths unchanged.

Migration command used (from the module folder):

```
dotnet tool restore
dotnet dotnet-ef migrations add InitialCustomers --project . --startup-project . --context CustomersDbContext --output-dir Persistence/Migrations --no-build
```
