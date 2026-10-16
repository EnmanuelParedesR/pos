# Rename map, phase A: building blocks and Contracts

Spanish → English renames done in `src/BuildingBlocks/**` and `src/Modules/*/Abastalia.*.Contracts/**`. Module, test, host and client code must update its references with this map. Behavior did not change: only names, comments and log text.

Read `docs/glosario-codigo.md` first; this map follows it.

## Conventions followed

- **Types**: English noun phrases from the glossary. Consumer interfaces in Contracts are named after what they offer: `I<Module><Noun>` or `I<Noun>` (`ICashRegisterOperations`, `ICustomerAccounts`, `IStockMovements`, `ICatalogQueries`, `IFiscalDocuments`, `ISettingsQueries`). Report interfaces are `I<Module>Reports` (`ISalesReports`, `IPurchasingReports`, `IInventoryReports`, `ICustomerReports`, `ICashRegisterReports`).
- **Integration events**: `<Subject><PastParticiple>V1` (`SaleRecordedV1`, `ShiftOpenedV1`, `PurchasePostedV1`). The `V1` suffix stays. Nested payload records keep `V1` too (`SaleLineV1`).
- **"Registrar" (to record)** → `Record…` for methods (`RecordRefundAsync`) and `…Recorded` for events. For a purchase, "registrada" means applied, so it maps to `Posted` (glossary: `Purchase (Draft, Posted, Voided)`).
- **Verbs**: obtener → `Get`, anular → `Void`, devolver/devolución → `Return`, emitir → `Issue`, consumir → `Consume`, verificar → `Verify`, procesar → `Process`, agregar → `Add`, leer → `Read`, acreditar → `Credit`, cargar (fiado) → `Charge`, usar → `Use`.
- **Ids**: `<Entity>Id` in English (`TurnoId` → `ShiftId`, `VentaId` → `SaleId`, `ReferenciaId` → `ReferenceId`, `AlmacenId` → `WarehouseId`, `AbonoId` → `CustomerPaymentId`, `PagoId` (supplier) → `SupplierPaymentId`, `DevolucionId` → `ReturnId`).
- **Common fields**: `Monto` → `Amount`, `Cantidad` → `Quantity`, `Precio` → `Price`, `Costo` → `Cost`, `CostoUnitario` → `UnitCost`, `Descuento` → `Discount`, `Motivo` → `Reason`, `Medio` → `Method`, `Tipo` → `Type`, `Nombre` → `Name`, `Descripcion` → `Description`, `Numero` → `Number`, `Lineas` → `Lines`, `Pagos` → `Payments`, `Utilidad` → `Profit`, `Existencia` → `Stock`, `Diferencia` → `Difference`, `RncCedula` → `TaxId`, `Rnc` stays `Rnc`, `ENcf` stays `ENcf`.
- **Booleans**: Spanish adjectives become `Is…` when they describe state (`Activo` → `IsActive`). Others keep a plain name (`Credito` → `OnCredit`, `CreditoFiscal` → `FiscalCredit`).
- **Dates**: `…Utc` suffix kept (`AbiertoUtc` → `OpenedUtc`, `FechaFirmaUtc` → `SignedUtc`). Range parameters `desde, hasta` → `from, to`.
- **Parameters/locals**: plain English; the event parameter of `IEventHandler<T>.HandleAsync` is `@event`.
- **Error codes**: `Module.Reason`, PascalCase English. Module prefix = the English project name (`Identity`, `Settings`, `Inventory`, `CashRegister`, `Sales`, `Purchasing`, `Customers`, `Fiscal`, `Reports`). Cross-cutting codes have no module (`Validation`, `Concurrency`, `Duplicate`, `Busy`, `Approval.Required`). The `detail` text stays in Spanish.
- **Audit actions** (first argument of `IAuditLog.RecordAsync`): `<area>.<kebab-verb-or-noun>` in English, lowercase. The area is the module's route prefix (`identity`, `settings`, `inventory`, `cash`, `sales`, `purchasing`, `customers`, `fiscal`). Example: `caja.abrir-turno` → `cash.open-shift`, `ventas.anular` → `sales.void`. The building blocks use `approval.used`.
- **Kept in Spanish**: `detail`/`Description` texts of `Error`, FluentValidation messages, the `"sistema"` display name in `HttpUserContext`, and the store data the owner sees.
- **Name clash to watch**: `Abastalia.BuildingBlocks.Security.Permissions` (static class) has the same name as a `Permissions` property you may give to a role entity. Inside such a class, refer to the static class as `Security.Permissions.X` or add `using Perms = Abastalia.BuildingBlocks.Security.Permissions;`.
- **Enum strings in the database**: modules that store these enums with `HasConversion<string>()` now store the English names. All new names fit the current column lengths. Each module regenerates its initial migration anyway.

## Abastalia.SharedKernel

### Files

| Old | New |
|---|---|
| `Dinero.cs` | `Money.cs` |
| `IntegrationEvent.cs` | (unchanged) |
| `Itbis.cs` | (unchanged) |
| `Result.cs` | (unchanged, comment only) |

### Types and members

| Old | New |
|---|---|
| `Dinero` | `Money` |
| `Dinero.Redondear(decimal monto)` | `Money.Round(decimal amount)` |
| `Asignacion` | `Allocation` |
| `Asignacion.EnOrden<T>(decimal monto, IEnumerable<(T Item, decimal Saldo)> abiertas)` | `Allocation.InOrder<T>(decimal amount, IEnumerable<(T Item, decimal Balance)> open)` |
| return tuple element `(T Item, decimal Monto)` | `(T Item, decimal Amount)` |
| `DiaNegocio` | `BusinessDay` |
| `DiaNegocio.Desfase` | `BusinessDay.Offset` |
| `DiaNegocio.De(DateTime utc)` | `BusinessDay.Of(DateTime utc)` |
| `DiaNegocio.RangoUtc(DateOnly desde, DateOnly hasta)` → `(Desde, Hasta)` | `BusinessDay.UtcRange(DateOnly from, DateOnly to)` → `(From, To)` |
| `IIntegrationEvent.OcurridoUtc` | `IIntegrationEvent.OccurredUtc` |
| `IIntegrationEvent.AgregadoId` | `IIntegrationEvent.AggregateId` |
| `IntegrationEvent(Guid AgregadoId)` | `IntegrationEvent(Guid AggregateId)` |
| `IntegrationEvent.OcurridoUtc` | `IntegrationEvent.OccurredUtc` |
| `TasaItbis` | `ItbisRate` |
| `TasaItbis.General18` | `ItbisRate.General18` |
| `TasaItbis.Reducida16` | `ItbisRate.Reduced16` |
| `TasaItbis.Cero` | `ItbisRate.Zero` |
| `TasaItbis.Exento` | `ItbisRate.Exempt` |
| `Itbis.Porcentaje(TasaItbis tasa)` | `Itbis.Percentage(ItbisRate rate)` |
| `Itbis.Incluido(decimal montoConItbis, TasaItbis tasa)` | `Itbis.Included(decimal amountWithItbis, ItbisRate rate)` |
| `Rnc.EsValido(string? valor)` | `Rnc.IsValid(string? value)` |
| `Rnc.Normalizar(string? valor)` | `Rnc.Normalize(string? value)` |
| `ErrorType`, `Error`, `Result`, `Result<T>` | (unchanged) |

## Abastalia.BuildingBlocks

### Files and folders

| Old | New |
|---|---|
| `Messaging/ProcesadorOutbox.cs` | `Messaging/OutboxProcessor.cs` |
| `Persistence/CrearBaseDeDatosAlArrancar.cs` | `Persistence/CreateDatabaseOnStartup.cs` |
| `Persistence/Diseno.cs` | `Persistence/DesignTimeFactory.cs` |
| `Persistence/OutboxYAuditoria.cs` | `Persistence/OutboxAndAudit.cs` |
| `Persistence/RelojHealthCheck.cs` | `Persistence/ClockHealthCheck.cs` |
| `Persistence/Migraciones/` (`20261015095632_InicialInfra`) | `Persistence/Migrations/` (`20261015151529_InitialInfra`, regenerated) |
| `Security/Autorizacion.cs` | `Security/Approval.cs` |
| `Security/Licencia.cs` | `Security/License.cs` |
| `Security/Permisos.cs` | `Security/Permissions.cs` |

`.editorconfig`: the generated-code glob is now `[**/Persistence/Migrations/*.cs]`, it was `[**/Persistence/Migraciones/*.cs]`. Module migration folders must move to `Persistence/Migrations` too, or analyzers will start checking them.

### AbastaliaServices

| Old | New |
|---|---|
| `AddAbastalia(services, config, modulos)` | `AddAbastalia(services, config, modules)` |
| `AbastaliaServices.CrearBaseDeDatosAsync(IServiceProvider, CancellationToken)` | `AbastaliaServices.CreateDatabaseAsync(IServiceProvider, CancellationToken)` |
| health check name `"reloj"` | `"clock"` |
| health check name `"outbox"` | (unchanged) |
| exception `"Falta Database:ConnectionString."` | `"Missing Database:ConnectionString."` |
| SQL probe `OBJECT_ID('infra.AuditoriaCabeza')` | `OBJECT_ID('infra.AuditHead')` |

### Endpoints (`Abastalia.BuildingBlocks.Endpoints`)

| Old | New |
|---|---|
| `EndpointExtensions.RequirePermiso(string permiso)` | `EndpointExtensions.RequirePermission(string permission)` |
| `EndpointExtensions.Problema(Error error)` | `EndpointExtensions.Problem(Error error)` |
| `IEndpoint`, `MapEndpoints`, `ToHttp` | (unchanged) |

### Messaging (`Abastalia.BuildingBlocks.Messaging`)

| Old | New |
|---|---|
| `Ok.Valor` | `Ok.Value` |
| `IManejadorEvento<TEvento>` | `IEventHandler<TEvent>` |
| `IManejadorEvento.ManejarAsync(TEvento evento, CancellationToken ct)` | `IEventHandler.HandleAsync(TEvent @event, CancellationToken ct)` |
| `ProcesadorOutbox` | `OutboxProcessor` |
| `ProcesadorOutbox.Lote` | `OutboxProcessor.BatchSize` |
| `ProcesadorOutbox.ProcesarAsync(ct)` | `OutboxProcessor.ProcessAsync(ct)` |
| `ProcesadorOutbox.EntregarAsync` (private) | `OutboxProcessor.DeliverAsync` |
| `TiposEvento(PorNombre)` | `EventTypes(ByName)` |
| `ProcesadorOutboxWorker` (internal) | `OutboxProcessorWorker` |
| `OutboxHealthCheck` (internal) | (unchanged) |
| `ValidacionDecorator<,>` (internal) | `ValidationDecorator<,>` |
| `TransaccionDecorator<,>` (internal) | `TransactionDecorator<,>` |
| `TransaccionDecorator.EsDuplicado` (private) | `TransactionDecorator.IsDuplicate` |
| `AutorizacionDecorator<,>` (internal) | `ApprovalDecorator<,>` |
| `ICommand<T>`, `ICommandHandler<,>`, `Handle` | (unchanged) |

### Persistence (`Abastalia.BuildingBlocks.Persistence`)

| Old | New |
|---|---|
| `CrearBaseDeDatosAlArrancar` (internal) | `CreateDatabaseOnStartup` |
| `DisenoDbContext<T>` | `DesignTimeFactory<T>` |
| `DisenoInfra` (internal) | `InfraDesignTimeFactory` |
| design-time database name `abastalia_diseno` | `abastalia_design` |
| `ModuleDbContext.TextoLargo` | `ModuleDbContext.LongText` |
| `DatabaseOptions.Seccion` | `DatabaseOptions.Section` |
| `DatabaseOptions.CrearAlIniciar` | `DatabaseOptions.CreateOnStartup` |
| `IOutbox.Agregar(IIntegrationEvent evento)` | `IOutbox.Add(IIntegrationEvent @event)` |
| `IAuditoria` | `IAuditLog` |
| `IAuditoria.RegistrarAsync(accion, recursoTipo, recursoId, datos, ct)` | `IAuditLog.RecordAsync(action, resourceType, resourceId, data, ct)` |
| `Auditoria` (internal implementation) | `AuditLog` |
| `CadenaAuditoria` | `AuditChain` |
| `CadenaAuditoria.Hash(RegistroAuditoria)` | `AuditChain.Hash(AuditRecord)` |
| `CadenaAuditoria.VerificarAsync(InfraDbContext, ct)` | `AuditChain.VerifyAsync(InfraDbContext, ct)` |
| `Json.Opciones` | `Json.Options` |
| `RelojHealthCheck` (internal) | `ClockHealthCheck` |
| `RelojHealthCheck.Tolerancia` | `ClockHealthCheck.Tolerance` |
| `InfraDbContext.Outbox` | (unchanged) |
| `InfraDbContext.Auditoria` | `InfraDbContext.AuditRecords` |
| `InfraDbContext.CabezaAuditoria` | `InfraDbContext.AuditHead` |
| `OutboxMensaje` | `OutboxMessage` |
| `RegistroAuditoria` | `AuditRecord` |
| `CabezaAuditoria` | `AuditHead` |
| `CabezaAuditoria.TextoAsync(db, ct)` | `AuditHead.TextAsync(db, ct)` |
| `OutboxMensajeConfiguration` / `RegistroAuditoriaConfiguration` / `CabezaAuditoriaConfiguration` | `OutboxMessageConfiguration` / `AuditRecordConfiguration` / `AuditHeadConfiguration` |
| `UnitOfWork`, `DbProvider`, `DatabaseOptions.Provider`, `DatabaseOptions.ConnectionString` | (unchanged) |

The audit hash covers the same fields in the same order. Only the property names changed.

### Infra database (schema `infra`, unchanged)

| Old table | New table |
|---|---|
| `infra.OutboxMensajes` | `infra.OutboxMessages` |
| `infra.AuditoriaRegistros` | `infra.AuditRecords` |
| `infra.AuditoriaCabeza` | `infra.AuditHead` |

| Table | Old column | New column |
|---|---|---|
| OutboxMessages | `Secuencia` | `Sequence` |
| OutboxMessages | `Id` | `Id` |
| OutboxMessages | `Tipo` | `Type` |
| OutboxMessages | `AgregadoId` | `AggregateId` |
| OutboxMessages | `Payload` | `Payload` |
| OutboxMessages | `OcurridoUtc` | `OccurredUtc` |
| OutboxMessages | `UsuarioId` | `UserId` |
| OutboxMessages | `DispositivoId` | `DeviceId` |
| OutboxMessages | `ProcesadoUtc` | `ProcessedUtc` |
| OutboxMessages | `SubidoUtc` | `UploadedUtc` |
| AuditRecords | `Secuencia` | `Sequence` |
| AuditRecords | `Id` | `Id` |
| AuditRecords | `FechaUtc` | `DateUtc` |
| AuditRecords | `UsuarioId` | `UserId` |
| AuditRecords | `SupervisorId` | `SupervisorId` |
| AuditRecords | `DispositivoId` | `DeviceId` |
| AuditRecords | `Accion` | `Action` |
| AuditRecords | `RecursoTipo` | `ResourceType` |
| AuditRecords | `RecursoId` | `ResourceId` |
| AuditRecords | `Datos` | `Data` |
| AuditRecords | `HashPrevio` | `PreviousHash` |
| AuditRecords | `Hash` | `Hash` |
| AuditHead | `Id`, `Secuencia`, `Hash` | `Id`, `Sequence`, `Hash` |

Indexes: `IX_OutboxMessages_Id` (unique), `IX_OutboxMessages_ProcessedUtc`, `IX_AuditRecords_ResourceType_ResourceId`.

Migration command used (from the repo root):

```
dotnet tool restore
dotnet dotnet-ef migrations add InitialInfra --project src/BuildingBlocks/Abastalia.BuildingBlocks --startup-project src/BuildingBlocks/Abastalia.BuildingBlocks --context InfraDbContext --output-dir Persistence/Migrations --no-build
```

### Security (`Abastalia.BuildingBlocks.Security`)

| Old | New |
|---|---|
| `IRequiereAutorizacion` | `IRequiresApproval` |
| `IRequiereAutorizacion.Permiso` | `IRequiresApproval.Permission` |
| `IRequiereAutorizacion.AutorizacionSupervisorId` | `IRequiresApproval.SupervisorApprovalId` |
| `IRequiereAutorizacion.RecursoId` | `IRequiresApproval.ResourceId` |
| `IAutorizacionesSupervisor` | `ISupervisorApprovals` |
| `IAutorizacionesSupervisor.ConsumirAsync(autorizacionId, permiso, recursoId, ct)` | `ISupervisorApprovals.ConsumeAsync(approvalId, permission, resourceId, ct)` |
| `AutorizacionErrors` | `ApprovalErrors` |
| `AutorizacionErrors.Requerida(permiso)` | `ApprovalErrors.Required(permission)` |
| `Funcion` | `Feature` |
| `Funcion.ModuloCarniceria` / `ModuloMayorista` / `CopiaNube` / `AsistenteIA` | `Feature.ButcherModule` / `WholesaleModule` / `CloudBackup` / `AiAssistant` |
| `Limite` | `Limit` |
| `Limite.Cajas` | `Limit.Registers` |
| `EstadoLicencia` | `LicenseStatus` |
| `EstadoLicencia.Prueba` / `Activa` / `Gracia` / `Restringida` | `LicenseStatus.Trial` / `Active` / `Grace` / `Restricted` |
| `IFeatureService.Estado` | `IFeatureService.Status` |
| `IFeatureService.EstaActiva(Funcion funcion)` | `IFeatureService.IsEnabled(Feature feature)` |
| `IFeatureService.LimiteDe(Limite limite)` | `IFeatureService.LimitOf(Limit limit)` |
| `Permisos` | `Permissions` |
| `Permisos.Todos` | `Permissions.All` |
| `AbastaliaClaims.Usuario` (`"sub"`) | `AbastaliaClaims.User` (`"sub"`) |
| `AbastaliaClaims.Nombre` (`"name"`) | `AbastaliaClaims.Name` (`"name"`) |
| `AbastaliaClaims.Dispositivo` (`"dispositivo"`) | `AbastaliaClaims.Device` (`"device"`) |
| `AbastaliaClaims.Sesion` (`"sesion"`) | `AbastaliaClaims.Session` (`"session"`) |
| `AbastaliaClaims.Permiso` (`"permiso"`) | `AbastaliaClaims.Permission` (`"permission"`) |
| `IUserContext.UsuarioId` | `IUserContext.UserId` |
| `IUserContext.Nombre` | `IUserContext.Name` |
| `IUserContext.DispositivoId` | `IUserContext.DeviceId` |
| `IUserContext.SesionId` | `IUserContext.SessionId` |
| `IUserContext.Tiene(string permiso)` | `IUserContext.Has(string permission)` |
| `IUserContext.SupervisorId` | (unchanged) |

License: a signed license lists features and limits by enum name (`DatosLicencia.Funciones` / `Limites` in Settings). Licenses must now carry the English names (`ButcherModule`, …, limit key `Registers`). Licenses signed with the Spanish names no longer turn those features on.

### Permission strings

| Old constant = value | New constant = value |
|---|---|
| `UsuariosGestionar = "usuarios.gestionar"` | `UsersManage = "users.manage"` |
| `DispositivosGestionar = "dispositivos.gestionar"` | `DevicesManage = "devices.manage"` |
| `ConfiguracionGestionar = "configuracion.gestionar"` | `SettingsManage = "settings.manage"` |
| `ProductosGestionar = "productos.gestionar"` | `ProductsManage = "products.manage"` |
| `PreciosCambiar = "precios.cambiar"` | `PricesChange = "prices.change"` |
| `InventarioVer = "inventario.ver"` | `InventoryView = "inventory.view"` |
| `InventarioAjustar = "inventario.ajustar"` | `InventoryAdjust = "inventory.adjust"` |
| `InventarioContar = "inventario.contar"` | `InventoryCount = "inventory.count"` |
| `InventarioTransferir = "inventario.transferir"` | `InventoryTransfer = "inventory.transfer"` |
| `CajaOperar = "caja.operar"` | `RegisterOperate = "register.operate"` |
| `CajaRetirar = "caja.retirar"` | `RegisterWithdraw = "register.withdraw"` |
| `CajaAprobarCierre = "caja.aprobar-cierre"` | `RegisterApproveClose = "register.approve-close"` |
| `CajaAbrirGaveta = "caja.abrir-gaveta"` | `RegisterOpenDrawer = "register.open-drawer"` |
| `VentasRegistrar = "ventas.registrar"` | `SalesCreate = "sales.create"` |
| `VentasAnular = "ventas.anular"` | `SalesVoid = "sales.void"` |
| `VentasDevolver = "ventas.devolver"` | `SalesReturn = "sales.return"` |
| `VentasDescuentoMayor = "ventas.descuento-mayor"` | `SalesLargeDiscount = "sales.large-discount"` |
| `VentasCambiarPrecio = "ventas.cambiar-precio"` | `SalesPriceOverride = "sales.price-override"` |
| `ComprasGestionar = "compras.gestionar"` | `PurchasingManage = "purchasing.manage"` |
| `ClientesGestionar = "clientes.gestionar"` | `CustomersManage = "customers.manage"` |
| `FiadoGestionar = "fiado.gestionar"` | `CreditManage = "credit.manage"` |
| `FiadoExcederLimite = "fiado.exceder-limite"` | `CreditExceedLimit = "credit.exceed-limit"` |
| `FiscalGestionar = "fiscal.gestionar"` | `FiscalManage = "fiscal.manage"` |
| `ReportesVer = "reportes.ver"` | `ReportsView = "reports.view"` |

### Error codes

| Old | New | Where |
|---|---|---|
| `Validacion` | `Validation` | `ValidationDecorator` |
| `Concurrencia` | `Concurrency` | `TransactionDecorator` (also used by `Inventory` products and by `Productos.razor` in the client) |
| `Duplicado` | `Duplicate` | `TransactionDecorator` |
| `Ocupado` | `Busy` | `TransactionDecorator` (SQL deadlock) |
| `Autorizacion.Requerida` | `Approval.Required` | `ApprovalErrors.Required` |

Codes named in Contracts doc comments, for the module agents to use:

| Old | New |
|---|---|
| `Clientes.LimiteExcedido` | `Customers.CreditLimitExceeded` |
| `Inventario.SinExistencia` | `Inventory.OutOfStock` |
| `Caja.TurnoYaAbierto` (example in `Result.cs`) | `CashRegister.ShiftAlreadyOpen` |
| `Identidad.AutorizacionInvalida` (suggested) | `Identity.InvalidApproval` |

### Audit actions

| Old | New |
|---|---|
| `autorizacion.usada` (data `{ Permiso, Autorizacion }`) | `approval.used` (data `{ Permission, Approval }`) |

### Config keys

| Old | New | Read by |
|---|---|---|
| `Database:CrearAlIniciar` | `Database:CreateOnStartup` | `DatabaseOptions.CreateOnStartup` (also set in `src/Hosts/Abastalia.Host.Local/appsettings.json`) |
| `Database:Provider`, `Database:ConnectionString` | (unchanged) | `DatabaseOptions` |
| `Outbox:Procesar` | `Outbox:Process` | `OutboxProcessorWorker` (tests set it to false) |
| `Outbox:IntervaloSegundos` | `Outbox:IntervalSeconds` | `OutboxProcessorWorker` |

Not in this scope, but asked for by the plan (Settings module / tests): `Auditoria:VerificacionNocturna` → `Audit:NightlyCheck`, `Abastalia:CarpetaClaves` → `Abastalia:KeysFolder` (environment variable `Abastalia__CarpetaClaves` → `Abastalia__KeysFolder` in `tests/Abastalia.E2ETests`).

### Log and exception texts (now English)

- `"No se pudo procesar el evento {Tipo} {Id}; se reintenta en la próxima pasada"` → `"Could not process event {Type} {Id}; it is retried on the next run"`
- `"Falló el procesador del outbox"` → `"The outbox processor failed"`
- Outbox health: `"Hay eventos sin procesar desde hace más de 10 minutos."` → `"Some events have been waiting more than 10 minutes to be processed."`
- Clock health: `"La hora del servidor está atrasada…"` → `"The server clock is behind the newest audit record (…). Fix the Windows time."`
- `"La auditoría solo se escribe dentro de un comando."` → `"The audit log is only written inside a command."`
- `"Esta base se creó antes de las migraciones (TK-090)…"` → `"This database was created before migrations existed (TK-090): back it up and create it again (ADR-0005)."`

## Abastalia.CashRegister.Contracts

Files: `Reportes.cs` → `Reports.cs`; `Contracts.cs` unchanged.

| Old | New |
|---|---|
| `MedioPago` | `PaymentMethod` |
| `MedioPago.Efectivo` / `Tarjeta` / `Transferencia` / `Fiado` / `SaldoFavor` | `PaymentMethod.Cash` / `Card` / `Transfer` / `OnAccount` / `CreditBalance` |
| `TurnoAbierto(TurnoId, CajaId, Caja, CajeroId)` | `OpenShift(ShiftId, RegisterId, Register, CashierId)` |
| `CobroMedio(Medio, Monto, Referencia)` | `PaymentByMethod(Method, Amount, Reference)` |
| `ICajaOperaciones` | `ICashRegisterOperations` |
| `ObtenerTurnoAbiertoAsync(turnoId, ct)` | `GetOpenShiftAsync(shiftId, ct)` |
| `RegistrarCobroAsync(turnoId, ventaId, pagos, redondeo, ct)` | `RecordSalePaymentAsync(shiftId, saleId, payments, rounding, ct)` |
| `RegistrarReembolsoAsync(turnoId, referenciaId, medio, monto, ct)` | `RecordRefundAsync(shiftId, referenceId, method, amount, ct)` |
| `RegistrarAbonoClienteAsync(turnoId, abonoId, medio, monto, redondeo, ct)` | `RecordCustomerPaymentAsync(shiftId, customerPaymentId, method, amount, rounding, ct)` |
| `RegistrarPagoSuplidorAsync(turnoId, pagoId, monto, ct)` | `RecordSupplierPaymentAsync(shiftId, supplierPaymentId, amount, ct)` |
| `AnularMovimientosAsync(referenciaId, ct)` | `VoidMovementsAsync(referenceId, ct)` |
| `TurnoAbiertoV1(TurnoId, CajaId, CajeroId, FondoInicial)` | `ShiftOpenedV1(ShiftId, RegisterId, CashierId, OpeningFloat)` |
| `MovimientoCajaRegistradoV1(TurnoId, Tipo, Medio, Monto, ReferenciaId)` | `CashMovementRecordedV1(ShiftId, Type, Method, Amount, ReferenceId)` |
| `TurnoCerradoV1(TurnoId, CajaId, CajeroId, NumeroZ, EfectivoEsperado, EfectivoContado, Diferencia)` | `ShiftClosedV1(ShiftId, RegisterId, CashierId, ZNumber, ExpectedCash, CountedCash, Difference)` |
| `ResumenTurno(TurnoId, Caja, Cajero, AbiertoUtc, CerradoUtc, NumeroZ, FondoInicial, EfectivoEsperado, EfectivoContado, Diferencia)` | `ShiftSummary(ShiftId, Register, Cashier, OpenedUtc, ClosedUtc, ZNumber, OpeningFloat, ExpectedCash, CountedCash, Difference)` |
| `ICajaReportes` | `ICashRegisterReports` |
| `ICajaReportes.TurnosAsync(desde, hasta, ct)` | `ICashRegisterReports.ShiftsAsync(from, to, ct)` |

The doc comment on `RecordSupplierPaymentAsync` names the withdrawal reason `SupplierPayment` (was `PagoSuplidor`). The CashRegister agent should use that name for the cash-movement reason enum value.

## Abastalia.Customers.Contracts

Files: `Reportes.cs` → `Reports.cs`; `Contracts.cs` unchanged.

| Old | New |
|---|---|
| `NivelPrecioCliente` | `PriceLevel` |
| `NivelPrecioCliente.Detalle` / `Mayorista` | `PriceLevel.Retail` / `Wholesale` |
| `ClienteParaVenta(Id, Nombre, RncCedula, CreditoFiscal, Nivel, LimiteCredito, Deuda, SaldoFavor, Activo)` | `CustomerForSale(Id, Name, TaxId, FiscalCredit, PriceLevel, CreditLimit, Debt, CreditBalance, IsActive)` |
| `ICuentasClientes` | `ICustomerAccounts` |
| `ObtenerAsync(clienteId, ct)` | `GetAsync(customerId, ct)` |
| `CargarFiadoAsync(clienteId, ventaId, documento, monto, permitirExceso, ct)` | `ChargeOnAccountAsync(customerId, saleId, document, amount, allowOverLimit, ct)` |
| `UsarSaldoFavorAsync(clienteId, ventaId, monto, ct)` | `UseCreditBalanceAsync(customerId, saleId, amount, ct)` |
| `AnularVentaAsync(ventaId, ct)` | `VoidSaleAsync(saleId, ct)` |
| `AcreditarDevolucionAsync(clienteId, ventaId, devolucionId, monto, ct)` | `CreditReturnAsync(customerId, saleId, returnId, amount, ct)` |
| `CargoFiadoRegistradoV1(ClienteId, VentaId, Monto)` | `OnAccountChargeRecordedV1(CustomerId, SaleId, Amount)` |
| `AbonoFiadoRegistradoV1(ClienteId, AbonoId, Monto)` | `CustomerPaymentRecordedV1(CustomerId, CustomerPaymentId, Amount)` |
| `CuentaPorCobrarCliente(ClienteId, Cliente, De0a30, De31a60, De61a90, MasDe90, Total, LimiteCredito)` | `CustomerReceivable(CustomerId, Customer, Days0To30, Days31To60, Days61To90, Over90, Total, CreditLimit)` |
| `IClientesReportes` | `ICustomerReports` |
| `IClientesReportes.PorCobrarAsync(ct)` | `ICustomerReports.ReceivablesAsync(ct)` |

## Abastalia.Fiscal.Contracts

Files: `Contracts.cs` unchanged.

| Old | New |
|---|---|
| `TipoEcf` | `EcfType` |
| `TipoEcf.CreditoFiscal31 = 31` / `Consumo32 = 32` / `NotaDebito33 = 33` / `NotaCredito34 = 34` | `EcfType.FiscalCredit31 = 31` / `Consumer32 = 32` / `DebitNote33 = 33` / `CreditNote34 = 34` |
| `LineaFiscal(Descripcion, Cantidad, PrecioUnitario, Descuento, TasaItbis Tasa, Itbis, Total)` | `FiscalLine(Description, Quantity, UnitPrice, Discount, ItbisRate Rate, Itbis, Total)` |
| `CodigoModificacion` | `ModificationCode` |
| `CodigoModificacion.Anulacion = 1` / `CorreccionMontos = 3` | `ModificationCode.Void = 1` / `AmountCorrection = 3` |
| `TipoPagoEcf` | `EcfPaymentType` |
| `TipoPagoEcf.Contado = 1` / `Credito = 2` | `EcfPaymentType.Cash = 1` / `Credit = 2` |
| `LimitesEcf` | `EcfLimits` |
| `LimitesEcf.ConsumoIdentificado` | `EcfLimits.IdentifiedConsumer` |
| `DocumentoAEmitir(Tipo, ReferenciaId, RncComprador, NombreComprador, Lineas, Itbis, Total, ENcfModificado, CodigoModificacion, TipoPago)` | `DocumentToIssue(Type, ReferenceId, BuyerRnc, BuyerName, Lines, Itbis, Total, ModifiedENcf, ModificationCode, PaymentType)` |
| `ComprobanteEmitido(DocumentoId, ENcf, CodigoSeguridad, FechaFirmaUtc, UrlQr)` | `IssuedDocument(DocumentId, ENcf, SecurityCode, SignedUtc, QrUrl)` |
| `IComprobantesFiscales` | `IFiscalDocuments` |
| `IComprobantesFiscales.EmitirAsync(documento, ct)` | `IFiscalDocuments.IssueAsync(document, ct)` |
| `ComprobanteEmitidoV1(DocumentoId, ENcf, Tipo, ReferenciaId, Total, Itbis)` | `FiscalDocumentIssuedV1(DocumentId, ENcf, Type, ReferenceId, Total, Itbis)` |

## Abastalia.Inventory.Contracts

Files: `Reportes.cs` → `Reports.cs`; `Contracts.cs` unchanged.

| Old | New |
|---|---|
| `UnidadMedida` | `UnitOfMeasure` |
| `UnidadMedida.Unidad` / `Libra` | `UnitOfMeasure.Unit` / `Pound` |
| `TipoMovimiento` | `StockMovementType` |
| `TipoMovimiento.Venta` / `AnulacionVenta` / `Devolucion` | `StockMovementType.Sale` / `SaleVoid` / `Return` |
| `TipoMovimiento.Compra` / `AnulacionCompra` | `StockMovementType.Purchase` / `PurchaseVoid` |
| `TipoMovimiento.Ajuste` / `Conteo` | `StockMovementType.Adjustment` / `Count` |
| `TipoMovimiento.TransferenciaSalida` / `TransferenciaEntrada` | `StockMovementType.TransferOut` / `TransferIn` |
| `ProductoParaVenta(ProductoId, PresentacionId, Nombre, CategoriaId, Categoria, Unidad, TasaItbis, Precio, PrecioMayorista, Factor, CostoBase, VenderSinExistencia, Activo)` | `ProductForSale(ProductId, PresentationId, Name, CategoryId, Category, Unit, ItbisRate, Price, WholesalePrice, Factor, BaseCost, SellWithoutStock, IsActive)` |
| `ICatalogoConsultas` | `ICatalogQueries` |
| `ObtenerParaVentaAsync(items: (ProductoId, PresentacionId), ct)` | `GetForSaleAsync(items: (ProductId, PresentationId), ct)` |
| `LeerEtiquetaAsync(codigo, ct)` | `ReadScaleLabelAsync(code, ct)` |
| `EtiquetaLeida(ProductoId, Peso, Monto)` | `ScaleLabelReading(ProductId, Weight, Amount)` |
| `LineaMovimiento(ProductoId, Cantidad, CostoUnitario)` | `StockMovementLine(ProductId, Quantity, UnitCost)` |
| `IMovimientosInventario` | `IStockMovements` |
| `RegistrarSalidaAsync(tipo, referenciaId, lineas, ct, almacenId, soloConExistencia, revertirCosto)` | `RecordOutboundAsync(type, referenceId, lines, ct, warehouseId, onlyWithStock, reverseCost)` |
| `RegistrarEntradaAsync(tipo, referenciaId, lineas, actualizarCosto, ct, almacenId)` | `RecordInboundAsync(type, referenceId, lines, updateCost, ct, warehouseId)` |
| `PrecioCambiadoV1(ProductoId, PrecioAnterior, PrecioNuevo, MayoristaAnterior, MayoristaNuevo)` | `PriceChangedV1(ProductId, OldPrice, NewPrice, OldWholesalePrice, NewWholesalePrice)` |
| `MovimientoInventarioRegistradoV1(MovimientoId, ProductoId, AlmacenId, Tipo, Cantidad, CostoUnitario, ReferenciaId)` | `StockMovementRecordedV1(MovementId, ProductId, WarehouseId, Type, Quantity, UnitCost, ReferenceId)` |
| `ValorInventario(ProductoId, Producto, CategoriaId, Categoria, Unidad, Activo, Existencia, Costo, ValorCosto, Precio, ValorVenta)` | `InventoryValue(ProductId, Product, CategoryId, Category, Unit, IsActive, Stock, Cost, CostValue, Price, SaleValue)` |
| `IInventarioReportes` | `IInventoryReports` |
| `IInventarioReportes.ValorAsync(ct)` | `IInventoryReports.ValueAsync(ct)` |
| `IInventarioReportes.CategoriasAsync(ct)` | `IInventoryReports.CategoriesAsync(ct)` |

## Abastalia.Purchasing.Contracts

Files: `Reportes.cs` → `Reports.cs`; `Contracts.cs` unchanged.

| Old | New |
|---|---|
| `CompraRegistradaV1(CompraId, SuplidorId, Ncf, Subtotal, Itbis, Total, Credito)` | `PurchasePostedV1(PurchaseId, SupplierId, Ncf, Subtotal, Itbis, Total, OnCredit)` |
| `CompraAnuladaV1(CompraId, Motivo)` | `PurchaseVoidedV1(PurchaseId, Reason)` |
| `PagoSuplidorRegistradoV1(PagoId, SuplidorId, Monto)` | `SupplierPaymentRecordedV1(SupplierPaymentId, SupplierId, Amount)` |
| `Documento606` | `Report606Row` |
| `CuentaPorPagarSuplidor(SuplidorId, Suplidor, PorVencer, Vencido1a30, Vencido31a60, Vencido61a90, VencidoMas90, Total)` | `SupplierPayable(SupplierId, Supplier, NotYetDue, Overdue1To30, Overdue31To60, Overdue61To90, OverdueOver90, Total)` |
| `IComprasReportes` | `IPurchasingReports` |
| `IComprasReportes.Documentos606Async(desde, hasta, ct)` | `IPurchasingReports.Report606Async(from, to, ct)` |
| `IComprasReportes.PorPagarAsync(ct)` | `IPurchasingReports.PayablesAsync(ct)` |

`Documento606` → `Report606Row` members, in order:

| Old | New |
|---|---|
| `RncCedula` | `TaxId` |
| `TipoId` | `IdType` |
| `TipoBienesServicios` | `GoodsServicesType` |
| `Ncf` | `Ncf` |
| `NcfModificado` | `ModifiedNcf` |
| `FechaComprobante` | `DocumentDate` |
| `FechaPago` | `PaymentDate` |
| `MontoServicios` | `ServicesAmount` |
| `MontoBienes` | `GoodsAmount` |
| `TotalMonto` | `TotalAmount` |
| `ItbisFacturado` | `ItbisInvoiced` |
| `ItbisRetenido` | `ItbisWithheld` |
| `ItbisProporcionalidad` | `ItbisProportionality` |
| `ItbisAlCosto` | `ItbisToCost` |
| `ItbisPorAdelantar` | `ItbisToAdvance` |
| `ItbisPercibido` | `ItbisCollected` |
| `TipoRetencionIsr` | `IsrWithholdingType` |
| `RetencionRenta` | `IncomeTaxWithheld` |
| `IsrPercibido` | `IsrCollected` |
| `ImpuestoSelectivo` | `SelectiveTax` |
| `OtrosImpuestos` | `OtherTaxes` |
| `PropinaLegal` | `LegalTip` |
| `FormaPago` | `PaymentForm` |

## Abastalia.Sales.Contracts

Files: `Reportes.cs` → `Reports.cs`; `Contracts.cs` unchanged.

| Old | New |
|---|---|
| `LineaVentaV1(ProductoId, PresentacionId, Descripcion, Cantidad, Total, Itbis, Costo)` | `SaleLineV1(ProductId, PresentationId, Description, Quantity, Total, Itbis, Cost)` |
| `PagoVentaV1(Medio, Monto)` | `SalePaymentV1(Method, Amount)` |
| `VentaRegistradaV1(VentaId, Numero, TurnoId, CajeroId, ClienteId, ENcf, Total, Itbis, Descuento, Lineas, Pagos)` | `SaleRecordedV1(SaleId, Number, ShiftId, CashierId, CustomerId, ENcf, Total, Itbis, Discount, Lines, Payments)` |
| `VentaAnuladaV1(VentaId, ENcfNotaCredito, Motivo, SupervisorId)` | `SaleVoidedV1(SaleId, CreditNoteENcf, Reason, SupervisorId)` |
| `DevolucionRegistradaV1(DevolucionId, VentaId, ENcfNotaCredito, Medio, Total, Itbis)` | `ReturnRecordedV1(ReturnId, SaleId, CreditNoteENcf, Method, Total, Itbis)` |
| `VentaDia(Dia, Ventas, VentasConItbis, Descuento, Devoluciones, Itbis, NetoSinItbis, Costo, Utilidad)` | `DailySales(Day, SaleCount, SalesWithItbis, Discount, Returns, Itbis, NetWithoutItbis, Cost, Profit)` |
| `VentaProducto(ProductoId, CategoriaId, Producto, Cantidad, NetoSinItbis, Costo, Utilidad)` | `ProductSales(ProductId, CategoryId, Product, Quantity, NetWithoutItbis, Cost, Profit)` |
| `VentaCajero(CajeroId, Cajero, Ventas, Anuladas, VendidoConItbis, Descuento)` | `CashierSales(CashierId, Cashier, SaleCount, VoidedCount, SoldWithItbis, Discount)` |
| `Documento607` | `Report607Row` |
| `IVentasReportes` | `ISalesReports` |
| `IVentasReportes.PorDiaAsync(desde, hasta, ct)` | `ISalesReports.ByDayAsync(from, to, ct)` |
| `IVentasReportes.PorProductoAsync(desde, hasta, ct)` | `ISalesReports.ByProductAsync(from, to, ct)` |
| `IVentasReportes.PorCajeroAsync(desde, hasta, ct)` | `ISalesReports.ByCashierAsync(from, to, ct)` |
| `IVentasReportes.Documentos607Async(desde, hasta, ct)` | `ISalesReports.Report607Async(from, to, ct)` |

`SalePaymentV1.Method` and `ReturnRecordedV1.Method` are strings holding a `PaymentMethod` name, so they now carry `Cash`, `Card`, … instead of `Efectivo`, `Tarjeta`, …

`Documento607` → `Report607Row` members, in order:

| Old | New |
|---|---|
| `RncCedula` | `TaxId` |
| `TipoId` | `IdType` |
| `Ncf` | `Ncf` |
| `NcfModificado` | `ModifiedNcf` |
| `TipoIngreso` | `IncomeType` |
| `FechaComprobante` | `DocumentDate` |
| `FechaRetencion` | `WithholdingDate` |
| `MontoFacturado` | `InvoicedAmount` |
| `ItbisFacturado` | `ItbisInvoiced` |
| `ItbisRetenidoTerceros` | `ItbisWithheldByThirdParties` |
| `ItbisPercibido` | `ItbisCollected` |
| `RetencionRentaTerceros` | `IncomeTaxWithheldByThirdParties` |
| `IsrPercibido` | `IsrCollected` |
| `ImpuestoSelectivo` | `SelectiveTax` |
| `OtrosImpuestos` | `OtherTaxes` |
| `PropinaLegal` | `LegalTip` |
| `Efectivo` | `Cash` |
| `ChequeTransferencia` | `CheckTransfer` |
| `Tarjeta` | `Card` |
| `VentaCredito` | `CreditSale` |
| `BonosRegalo` | `GiftVouchers` |
| `Permuta` | `Barter` |
| `OtrasFormas` | `OtherForms` |

## Abastalia.Settings.Contracts

Files: `Contracts.cs` unchanged.

| Old | New |
|---|---|
| `DatosNegocio(Nombre, Rnc, Direccion, Telefono, PieRecibo, TopeDescuentoPct)` | `BusinessProfile(Name, Rnc, Address, Phone, ReceiptFooter, DiscountCapPercent)` |
| `IConfiguracionConsultas` | `ISettingsQueries` |
| `IConfiguracionConsultas.ObtenerNegocioAsync(ct)` | `ISettingsQueries.GetBusinessProfileAsync(ct)` |

## Effects outside the code (for the client, host and test agents)

- **JSON field names change** wherever these types go over the wire (the API uses camelCase and string enums): for example `autorizacionSupervisorId` → `supervisorApprovalId` on every command that implements `IRequiresApproval`, and every enum value above (`"Efectivo"` → `"Cash"`, `"General18"` stays, `"Reducida16"` → `"Reduced16"`, …).
- **JWT claims**: `dispositivo` / `sesion` / `permiso` → `device` / `session` / `permission`. Old tokens stop working after the upgrade, so users sign in again.
- **Outbox payloads** now hold `occurredUtc` / `aggregateId` and English event type names. Existing databases start fresh (the initial migrations are regenerated).
- **Health check** names: `reloj` → `clock`.
- `src/Hosts/Abastalia.Host.Local/appsettings.json`: `Database:CrearAlIniciar` → `Database:CreateOnStartup`.
- `tests/Abastalia.IntegrationTests/Infra/AbastaliaApp.cs`: `Database:CrearAlIniciar` → `Database:CreateOnStartup`, `Outbox:Procesar` → `Outbox:Process`.
- `tests/Abastalia.E2ETests/ServidorAbastalia.cs`: `Database__CrearAlIniciar` → `Database__CreateOnStartup`.
