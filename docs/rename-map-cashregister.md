# Rename map, phase B: CashRegister module

Spanish → English renames done in `src/Modules/CashRegister/Toki.CashRegister/**`. Tests, the Host and the client must update their references with this map. Behavior did not change: same rules, transactions, audit, ordering and rounding. The Contracts names are in `docs/rename-map.md` (`Toki.CashRegister.Contracts`).

JSON is camelCase and enums go over the wire as strings, so every wire name below is the camelCase of the C# name.

## Files

| Old | New |
|---|---|
| `CajaModule.cs` | `CashRegisterModule.cs` |
| `Domain/Caja.cs` | `Domain/Shift.cs` |
| `Domain/ReporteZ.cs` | `Domain/ZReport.cs` |
| `Features/Turnos.cs` | `Features/Shifts.cs` |
| `Persistence/Migraciones/` (`20261015095643_InicialCaja`, `20261015101222_CadenaAuditoriaCaja`, `CajaDbContextModelSnapshot`) | `Persistence/Migrations/` (`20261015152410_InitialCashRegister`, `CashRegisterDbContextModelSnapshot`), regenerated |

## Routes

All OpenAPI tags `"Caja"` → `"CashRegister"`. Permissions are the new strings from `docs/rename-map.md`.

| Old | New | Permission |
|---|---|---|
| `POST /api/caja/cajas` (→ `201 Location: /api/caja/cajas/{id}`) | `POST /api/cash/registers` (→ `201 Location: /api/cash/registers/{id}`) | `settings.manage` |
| `GET /api/caja/cajas` | `GET /api/cash/registers` | authenticated |
| — (nuevo, TK-175) | `PUT /api/cash/registers/{id}` (body `{ name }` → `204`; envía `RegisterChangedV1`) | `settings.manage` |
| `PUT /api/caja/cajas/{id}/dispositivo` | `PUT /api/cash/registers/{id}/device` | `settings.manage` |
| `POST /api/caja/turnos` (→ `201 Location: /api/caja/turnos/{turnoId}`) | `POST /api/cash/shifts` (→ `201 Location: /api/cash/shifts/{shiftId}`) | `register.operate` |
| `GET /api/caja/turnos/actual` | `GET /api/cash/shifts/current` | `register.operate` |
| `POST /api/caja/turnos/{id}/movimientos` | `POST /api/cash/shifts/{id}/movements` | `register.operate` (+ approval rules below) |
| `POST /api/caja/turnos/{id}/cerrar` | `POST /api/cash/shifts/{id}/close` | `register.operate` (+ approval `register.approve-close`) |
| `GET /api/caja/turnos/{id}/z` | `GET /api/cash/shifts/{id}/z` | authenticated (cashier of a closed shift, or `register.approve-close` / `reports.view`) |
| `GET /api/caja/turnos?desde=&hasta=` | `GET /api/cash/shifts?from=&to=` (both `DateOnly`, required) | `reports.view` |
| `GET /api/caja/denominaciones` | `GET /api/cash/denominations` (unchanged body: `[2000,1000,500,200,100,50,25,10,5,1]`) | authenticated |

## Requests and responses (wire names)

### `POST /api/cash/registers`

| | Old | New |
|---|---|---|
| request record | `Cajas.Crear` | `Registers.Create` |
| body | `{ nombre }` | `{ name }` |
| response record | `Cajas.Response` | `Registers.Response` |
| response | `{ id }` | `{ id }` (unchanged) |

### `GET /api/cash/registers`

Item record `Cajas.Item` → `Registers.Item`:

| Old | New |
|---|---|
| `id` | `id` |
| `nombre` | `name` |
| `activa` | `isActive` |
| `turnoAbiertoId` | `openShiftId` |
| `cajero` | `cashier` |

### `PUT /api/cash/registers/{id}/device`

| | Old | New |
|---|---|---|
| request record | `Cajas.Asignar(CajaId, DispositivoId)` | `Registers.AssignDevice(RegisterId, DeviceId)` |
| body | `{ dispositivoId }` (`cajaId` is overwritten by the route) | `{ deviceId }` (`registerId` is overwritten by the route) |
| handler | `Cajas.AsignarHandler` | `Registers.AssignDeviceHandler` |
| response | `{ id }` | `{ id }` |

### `POST /api/cash/shifts`

| | Old | New |
|---|---|---|
| slice | `AbrirTurno` | `OpenShift` (feature class `Toki.CashRegister.Features.OpenShift`; not the Contracts record `OpenShift`) |
| body `AbrirTurno.Command` → `OpenShift.Command` | `{ cajaId, fondoInicial }` | `{ registerId, openingFloat }` |
| response | `{ turnoId }` | `{ shiftId }` |

### `GET /api/cash/shifts/current`

Slice `TurnoActual` → `CurrentShift`. 404 code `Caja.SinTurno` → `CashRegister.NoShift`.

| Old | New |
|---|---|
| `turnoId` | `shiftId` |
| `cajaId` | `registerId` |
| `caja` | `register` |
| `fondoInicial` | `openingFloat` |
| `abiertoUtc` | `openedUtc` |

### `POST /api/cash/shifts/{id}/movements`

Slice `RegistrarMovimiento` → `RecordMovement`. Command `RegistrarMovimiento.Command(TurnoId, TipoMovimiento, Monto, Motivo, Nota, AutorizacionSupervisorId)` → `RecordMovement.Command(ShiftId, MovementType, Amount, Reason, Note, SupervisorApprovalId)`; route body record `Body`.

| Old | New |
|---|---|
| `tipoMovimiento` | `movementType` |
| `monto` | `amount` |
| `motivo` | `reason` |
| `nota` | `note` |
| `autorizacionSupervisorId` | `supervisorApprovalId` |
| response `{ movimientoId }` | `{ movementId }` |

`movementType` enum `RegistrarMovimiento.Tipo` → `RecordMovement.Kind`:

| Old | New |
|---|---|
| `Entrada` | `CashIn` |
| `Retiro` | `Withdrawal` |
| `Gasto` | `Expense` |

Approval permission: `CashIn` → `register.operate`; `Withdrawal`/`Expense` → `register.withdraw`. Validation messages unchanged (Spanish), e.g. `"Explique el motivo."` when `reason` is `Other`.

`reason` enum `MotivoMovimiento` → `MovementReason`:

| Old | New |
|---|---|
| `CambioAdicional` | `ExtraChange` |
| `Recogida` | `CashPickup` |
| `DepositoBanco` | `BankDeposit` |
| `PagoSuplidor` | `SupplierPayment` |
| `GastoMenor` | `PettyExpense` |
| `Otro` | `Other` |

### `POST /api/cash/shifts/{id}/close`

Slice `CerrarTurno` → `CloseShift`. Command `CerrarTurno.Command(TurnoId, Conteo, AutorizacionSupervisorId)` → `CloseShift.Command(ShiftId, CashCount, SupervisorApprovalId)`; body record `Body`.

| Old | New |
|---|---|
| `conteo` | `cashCount` |
| `conteo[].denominacion` | `cashCount[].denomination` |
| `conteo[].cantidad` | `cashCount[].quantity` |
| `autorizacionSupervisorId` | `supervisorApprovalId` |

Response: the Z report below. Validation messages unchanged: `"Esa denominación no existe."`, `"Hay denominaciones repetidas."`.

### Z / X report (`POST …/close` and `GET /api/cash/shifts/{id}/z`)

Slice `ObtenerReporteZ` → `GetZReport`. Record `ReporteZ` → `ZReport`, in order:

| Old | New |
|---|---|
| `turnoId` | `shiftId` |
| `numeroZ` | `zNumber` |
| `caja` | `register` |
| `cajero` | `cashier` |
| `abiertoUtc` | `openedUtc` |
| `cerradoUtc` | `closedUtc` |
| `fondoInicial` | `openingFloat` |
| `porMedio` | `byMethod` |
| `ventas` | `saleCount` |
| `ventasAnuladas` | `voidedCount` |
| `abonosClientes` | `customerPayments` |
| `redondeo` | `rounding` |
| `entradas` | `cashIn` |
| `retiros` | `withdrawals` |
| `gastos` | `expenses` |
| `efectivoEsperado` | `expectedCash` |
| `efectivoContado` | `countedCash` |
| `diferencia` | `difference` |
| `conteo` (`[{ denominacion, cantidad }]`) | `cashCount` (`[{ denomination, quantity }]`) |
| `supervisorCierreId` | `closingSupervisorId` |
| `cadenaAuditoria` | `auditChainHead` |

`byMethod[]` item, record `TotalMedio` → `MethodTotal`:

| Old | New |
|---|---|
| `medio` (`Efectivo`, `Tarjeta`, …) | `method` (`Cash`, `Card`, `Transfer`, `OnAccount`, `CreditBalance`) |
| `cobros` | `salePayments` |
| `anulaciones` | `voids` |
| `reembolsos` | `refunds` |
| `abonos` | `customerPayments` |
| `neto` | `net` |

### `GET /api/cash/shifts?from=&to=`

Slice `ListarTurnos` → `ListShifts`. Item:

| Old | New |
|---|---|
| `id` | `id` |
| `caja` | `register` |
| `cajero` | `cashier` |
| `abiertoUtc` | `openedUtc` |
| `cerradoUtc` | `closedUtc` |
| `numeroZ` | `zNumber` |
| `fondoInicial` | `openingFloat` |
| `efectivoEsperado` | `expectedCash` |
| `efectivoContado` | `countedCash` |
| `diferencia` | `difference` |

Slice `ListarDenominaciones` → `ListDenominations`.

## Error codes

`detail` texts unchanged (Spanish).

| Old | New | HTTP |
|---|---|---|
| `Caja.CajaNoEncontrada` | `CashRegister.RegisterNotFound` | 404 |
| `Caja.TurnoNoEncontrado` | `CashRegister.ShiftNotFound` | 404 |
| `Caja.CajaOcupada` | `CashRegister.RegisterInUse` | 409 |
| `Caja.CajeroConTurno` | `CashRegister.CashierHasShift` | 409 |
| `Caja.TurnoCerrado` | `CashRegister.ShiftClosed` | 409 |
| `Caja.TurnoAjeno` | `CashRegister.NotYourShift` | 403 |
| `Caja.OtroDispositivo` | `CashRegister.OtherDevice` | 403 |
| `Caja.DenominacionInvalida` | `CashRegister.InvalidDenomination` | 400 (declared, not used by a handler) |
| `Caja.SinTurno` | `CashRegister.NoShift` | 404 |

C# members: `CajaErrors` → `CashRegisterErrors` (`CajaNoEncontrada` → `RegisterNotFound`, `TurnoNoEncontrado` → `ShiftNotFound`, `CajaOcupada` → `RegisterInUse`, `CajeroConTurno` → `CashierHasShift`, `TurnoCerrado` → `ShiftClosed`, `TurnoAjeno` → `NotYourShift`, `OtroDispositivo` → `OtherDevice`, `DenominacionInvalida` → `InvalidDenomination`).

## Audit actions

| Old action | New action | Old resource type | New resource type | Data |
|---|---|---|---|---|
| `caja.crear-caja` | `cash.create-register` | `Caja` | `Register` | the command `{ Name }` |
| `caja.asignar-dispositivo` | `cash.assign-device` | `Caja` | `Register` | the command `{ RegisterId, DeviceId }` |
| `caja.abrir-turno` | `cash.open-shift` | `Turno` | `Shift` | the command `{ RegisterId, OpeningFloat }` |
| `caja.entrada` | `cash.cash-in` | `Turno` | `Shift` | `{ Amount, Reason, Note }` |
| `caja.retiro` | `cash.withdrawal` | `Turno` | `Shift` | `{ Amount, Reason, Note }` |
| `caja.gasto` | `cash.expense` | `Turno` | `Shift` | `{ Amount, Reason, Note }` |
| `caja.cerrar-turno` | `cash.close-shift` | `Turno` | `Shift` | `{ ZNumber, ExpectedCash, CountedCash, Difference }` |

The movement actions were built as `"caja." + tipo.ToLowerInvariant()`; they are now explicit strings in the `RecordMovement` handler so `CashIn` gives `cash.cash-in` (kebab-case).

## Integration events and outbox payloads

Published with the Contracts names: `ShiftOpenedV1`, `CashMovementRecordedV1`, `ShiftClosedV1`. `CashMovementRecordedV1.Type` is the `CashMovementType` name, so it now carries the English values below.

## Domain (internal; `InternalsVisibleTo` Toki.UnitTests and Toki.IntegrationTests)

Namespace `Toki.CashRegister.Domain` (unchanged).

| Old | New |
|---|---|
| `CajaFisica` (`Id`, `Nombre`, `Activa`, `DispositivoId`) | `Register` (`Id`, `Name`, `IsActive`, `DeviceId`) |
| `EstadoTurno` (`Abierto`, `Cerrado`) | `ShiftStatus` (`Open`, `Closed`) |
| `Turno` | `Shift` |
| `Turno.CajaId` / `CajeroId` / `CajeroNombre` | `Shift.RegisterId` / `CashierId` / `CashierName` |
| `Turno.AbiertoUtc` / `FondoInicial` / `Estado` / `CerradoUtc` | `Shift.OpenedUtc` / `OpeningFloat` / `Status` / `ClosedUtc` |
| `Turno.EfectivoEsperado` / `EfectivoContado` / `Diferencia` | `Shift.ExpectedCash` / `CountedCash` / `Difference` |
| `Turno.SupervisorCierreId` / `Conteo` / `NumeroZ` / `CadenaAuditoria` / `Version` | `Shift.ClosingSupervisorId` / `CashCount` / `ZNumber` / `AuditChainHead` / `Version` |
| `TipoMovimientoCaja` | `CashMovementType` |
| `MotivoMovimiento` | `MovementReason` (values above) |
| `MovimientoCaja` | `CashMovement` |
| `MovimientoCaja.Secuencia` / `Id` / `TurnoId` / `Tipo` / `Medio` / `Monto` | `CashMovement.Sequence` / `Id` / `ShiftId` / `Type` / `Method` / `Amount` |
| `MovimientoCaja.Motivo` / `Nota` / `ReferenciaId` / `Referencia` | `CashMovement.Reason` / `Note` / `ReferenceId` / `Reference` |
| `MovimientoCaja.UsuarioId` / `SupervisorId` / `FechaUtc` / `Entra` | `CashMovement.UserId` / `SupervisorId` / `DateUtc` / `IsInflow` |
| `Denominaciones.Dop` | `Denominations.Dop` |
| `ConteoDenominacion(Denominacion, Cantidad)` | `DenominationCount(Denomination, Quantity)` |
| `CuadreTurno` | `ShiftBalance` |
| `CuadreTurno.EfectivoEsperado(decimal fondo, IEnumerable<MovimientoCaja> movimientos)` | `ShiftBalance.ExpectedCash(decimal openingFloat, IEnumerable<CashMovement> movements)` |
| `CuadreTurno.Contado(IEnumerable<ConteoDenominacion> conteo)` | `ShiftBalance.Counted(IEnumerable<DenominationCount> count)` |
| `TotalMedio(Medio, Cobros, Anulaciones, Reembolsos, Abonos, Neto)` | `MethodTotal(Method, SalePayments, Voids, Refunds, CustomerPayments, Net)` |
| `ReporteZ` (members in the Z table above, PascalCase: `TurnoId` → `ShiftId`, `PorMedio` → `ByMethod`, `Ventas` → `SaleCount`, `VentasAnuladas` → `VoidedCount`, …) | `ZReport` |
| `ReporteZ.ReferenciaRedondeo = "redondeo"` | `ZReport.RoundingReference = "rounding"` |
| `ReporteZ.Construir(Turno t, string caja, IReadOnlyList<MovimientoCaja> movimientos, IReadOnlyList<ConteoDenominacion>? conteo, string? cadenaAuditoria)` | `ZReport.Build(Shift s, string register, IReadOnlyList<CashMovement> movements, IReadOnlyList<DenominationCount>? cashCount, string? auditChainHead)` |

`CashMovementType` (`TipoMovimientoCaja`) values, stored as strings in `CashMovements.Type`:

| Old | New |
|---|---|
| `Cobro` | `SalePayment` |
| `AnulacionCobro` | `SalePaymentVoid` |
| `Reembolso` | `Refund` |
| `Entrada` | `CashIn` |
| `Retiro` | `Withdrawal` |
| `Gasto` | `Expense` |
| `AbonoCliente` | `CustomerPayment` |
| `Redondeo` | `Rounding` |
| `AnulacionAbono` | `CustomerPaymentVoid` |

Stored data kept in Spanish: the note `"Anulación"` on the `CashIn` that reverses a voided supplier payment (owner-visible). The rounding-reversal marker in `CashMovements.Reference` is now `"rounding"` (was `"redondeo"`).

## Module types (namespace `Toki.CashRegister`)

| Old | New |
|---|---|
| `CajaModule` | `CashRegisterModule` |
| `CajaModule.AddCajaModule(IServiceCollection)` | `CashRegisterModule.AddCashRegisterModule(IServiceCollection)` |
| `CajaDbContext` | `CashRegisterDbContext` |
| `CajaDbContext.Cajas` / `Turnos` / `Movimientos` | `CashRegisterDbContext.Registers` / `Shifts` / `Movements` |
| `CajaDbContext.TurnoAsync(id, ct)` | `CashRegisterDbContext.ShiftAsync(id, ct)` |
| `CajaConfiguration` | `CashRegisterConfiguration` |
| `CajaReportes` (implements `ICashRegisterReports`) | `CashRegisterReports` |
| `CajaOperaciones` (implements `ICashRegisterOperations`) | `CashRegisterOperations` |
| `CajaOperaciones.EfectivoEsperadoAsync(turnoId, ct)` | `CashRegisterOperations.ExpectedCashAsync(shiftId, ct)` |
| `CajaOperaciones.Agregar(turnoId, tipo, medio, monto, referenciaId, referencia, motivo, nota)` | `CashRegisterOperations.Add(shiftId, type, method, amount, referenceId, reference, reason, note)` |
| interface methods | as in `docs/rename-map.md` (`GetOpenShiftAsync`, `RecordSalePaymentAsync`, `RecordRefundAsync`, `RecordCustomerPaymentAsync`, `RecordSupplierPaymentAsync`, `VoidMovementsAsync`) |
| `DisenoCaja` | `CashRegisterDesignTimeFactory` |

Host: `builder.Services.AddCajaModule()` → `builder.Services.AddCashRegisterModule()`; `typeof(CajaModule).Assembly` → `typeof(CashRegisterModule).Assembly`.

## Database (schema `caja` → `cash`)

| Old table | New table |
|---|---|
| `caja.CajasFisicas` | `cash.Registers` |
| `caja.Turnos` | `cash.Shifts` |
| `caja.MovimientosCaja` | `cash.CashMovements` |

| Table | Old column | New column |
|---|---|---|
| Registers | `Id`, `Nombre`, `Activa`, `DispositivoId` | `Id`, `Name`, `IsActive`, `DeviceId` |
| Shifts | `Id` | `Id` |
| Shifts | `CajaId` | `RegisterId` |
| Shifts | `CajeroId` | `CashierId` |
| Shifts | `CajeroNombre` | `CashierName` |
| Shifts | `AbiertoUtc` | `OpenedUtc` |
| Shifts | `FondoInicial` | `OpeningFloat` |
| Shifts | `Estado` (`Abierto`/`Cerrado`) | `Status` (`Open`/`Closed`) |
| Shifts | `CerradoUtc` | `ClosedUtc` |
| Shifts | `EfectivoEsperado` | `ExpectedCash` |
| Shifts | `EfectivoContado` | `CountedCash` |
| Shifts | `Diferencia` | `Difference` |
| Shifts | `SupervisorCierreId` | `ClosingSupervisorId` |
| Shifts | `Conteo` (JSON `[{denominacion,cantidad}]`) | `CashCount` (JSON `[{denomination,quantity}]`) |
| Shifts | `NumeroZ` | `ZNumber` |
| Shifts | `CadenaAuditoria` | `AuditChainHead` |
| Shifts | `Version` | `Version` |
| CashMovements | `Secuencia` | `Sequence` |
| CashMovements | `Id` | `Id` |
| CashMovements | `TurnoId` | `ShiftId` |
| CashMovements | `Tipo` | `Type` |
| CashMovements | `Medio` | `Method` |
| CashMovements | `Monto` | `Amount` |
| CashMovements | `Motivo` | `Reason` |
| CashMovements | `Nota` | `Note` |
| CashMovements | `ReferenciaId` | `ReferenceId` |
| CashMovements | `Referencia` | `Reference` |
| CashMovements | `UsuarioId` | `UserId` |
| CashMovements | `SupervisorId` | `SupervisorId` |
| CashMovements | `FechaUtc` | `DateUtc` |

Indexes:

| Old | New |
|---|---|
| `IX_Turnos_CajaAbierta` (unique, `[CerradoUtc] IS NULL`) | `IX_Shifts_OpenRegister` (unique, `[ClosedUtc] IS NULL`) |
| `IX_Turnos_CajeroAbierto` (unique, `[CerradoUtc] IS NULL`) | `IX_Shifts_OpenCashier` (unique, `[ClosedUtc] IS NULL`) |
| `IX_Turnos_AbiertoUtc` | `IX_Shifts_OpenedUtc` |
| `IX_MovimientosCaja_Id` (unique) / `_TurnoId` / `_ReferenciaId` | `IX_CashMovements_Id` (unique) / `_ShiftId` / `_ReferenceId` |

No seed data (`HasData`) in this module. Config keys: none.

Migration command used (from the repo root):

```
dotnet tool restore
dotnet dotnet-ef migrations add InitialCashRegister --project src/Modules/CashRegister/Toki.CashRegister --startup-project src/Modules/CashRegister/Toki.CashRegister --context CashRegisterDbContext --output-dir Persistence/Migrations --no-build
```
