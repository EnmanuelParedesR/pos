# Rename map, phase B: Fiscal module

Spanish → English renames done in `src/Modules/Fiscal/Abastalia.Fiscal/**`. Follows `docs/rename-map.md` (phase A) and `docs/glosario-codigo.md`. Behavior did not change: only names, comments, log text and the regenerated migration.

The API uses camelCase JSON and string enums, so every property below appears on the wire in camelCase (`ENcf` → `eNcf`, `SignedUtc` → `signedUtc`, …).

## Files and folders

| Old | New |
|---|---|
| `ColaEcfHealthCheck.cs` | `EcfQueueHealthCheck.cs` |
| `EnvioEcf.cs` | `EcfSubmissionQueue.cs` |
| `Features/Padron.cs` | `Features/TaxpayerRegistry.cs` |
| `Features/Secuencias.cs` | `Features/Sequences.cs` |
| `Domain/Fiscal.cs` | (unchanged) |
| `FiscalModule.cs` | (unchanged) |
| `Persistence/Migraciones/` (`20261015095646_InicialFiscal`) | `Persistence/Migrations/` (`20261015152435_InitialFiscal`, regenerated; namespace `Abastalia.Fiscal.Persistence.Migrations`) |

## Routes

| Old | New | Permission |
|---|---|---|
| `POST /api/fiscal/secuencias` (201, `Location: /api/fiscal/secuencias/{id}`) | `POST /api/fiscal/sequences` (201, `Location: /api/fiscal/sequences/{id}`) | `fiscal.manage` |
| `GET /api/fiscal/secuencias` | `GET /api/fiscal/sequences` | `fiscal.manage` |
| `GET /api/fiscal/alertas` | `GET /api/fiscal/alerts` | any signed-in user |
| `GET /api/fiscal/cola` | `GET /api/fiscal/queue` | `fiscal.manage` |
| `GET /api/fiscal/documentos?soloPendientes=&ultimos=` | `GET /api/fiscal/documents?pendingOnly=&last=` | `fiscal.manage` |
| `POST /api/fiscal/padron` (raw body: the DGII_RNC.TXT file) | `POST /api/fiscal/taxpayer-registry` (same body) | `fiscal.manage` |
| `GET /api/fiscal/contribuyentes/{rnc}` | `GET /api/fiscal/taxpayers/{rnc}` | any signed-in user |

Query params of `GET /api/fiscal/documents`: `soloPendientes` (bool?) → `pendingOnly`; `ultimos` (int?, default 100, clamped 1–1000) → `last`.

Swagger tag `Fiscal` unchanged. Permission constant `Permisos.FiscalGestionar` → `Permissions.FiscalManage` (`"fiscal.manage"`), from phase A.

## Request and response records

### `CargarSecuencia` → `LoadSequence` (`POST /api/fiscal/sequences`)

`CargarSecuencia.Command` → `LoadSequence.Command` (request body):

| Old | New |
|---|---|
| `Tipo` (`TipoEcf`) | `Type` (`EcfType`) |
| `Desde` | `From` |
| `Hasta` | `To` |
| `VenceEl` | `ExpiresOn` |

`CargarSecuencia.Response(Id)` → `LoadSequence.Response(Id)` (unchanged shape).
`CargarSecuencia.Validator` / `Handler` / `Endpoint` → `LoadSequence.Validator` / `Handler` / `Endpoint`.

### `ListarSecuencias` → `ListSequences` (`GET /api/fiscal/sequences`)

`ListarSecuencias.Item` → `ListSequences.Item`:

| Old | New |
|---|---|
| `Id` | `Id` |
| `Tipo` | `Type` |
| `Desde` (formatted e-NCF) | `From` |
| `Hasta` (formatted e-NCF) | `To` |
| `Siguiente` (formatted e-NCF) | `Next` |
| `Disponibles` | `Available` |
| `PorcentajeUsado` | `PercentUsed` |
| `VenceEl` | `ExpiresOn` |

### `AlertasSecuencias` → `SequenceAlerts` (`GET /api/fiscal/alerts`)

`AlertasSecuencias.Item` → `SequenceAlerts.Item`:

| Old | New |
|---|---|
| `Tipo` | `Type` |
| `Disponibles` | `Available` |
| `VenceEl` (DateOnly?) | `ExpiresOn` |
| `Alerta` (bool) | `Alert` |

### `EstadoCola` → `QueueStatus` (`GET /api/fiscal/queue`)

`EstadoCola.Response` → `QueueStatus.Response`:

| Old | New |
|---|---|
| `Pendientes` | `Pending` |
| `Vencidos` | `Overdue` |
| `ProximoLimiteUtc` | `NextDeadlineUtc` |
| `Rechazados` | `Rejected` |
| `Simulados` | `Simulated` |

### `ListarDocumentos` → `ListDocuments` (`GET /api/fiscal/documents`)

`ListarDocumentos.Item` → `ListDocuments.Item`:

| Old | New |
|---|---|
| `Id` | `Id` |
| `ENcf` | `ENcf` |
| `Tipo` | `Type` |
| `ReferenciaId` | `ReferenceId` |
| `Total` | `Total` |
| `Itbis` | `Itbis` |
| `FechaFirmaUtc` | `SignedUtc` |
| `Estado` (`EstadoEnvio`) | `Status` (`SubmissionStatus`) |
| `FechaLimiteEnvioUtc` | `SubmitDeadlineUtc` |
| `UltimoError` | `LastError` |

### `Padron` → `TaxpayerRegistry` (`POST /api/fiscal/taxpayer-registry`, `GET /api/fiscal/taxpayers/{rnc}`)

| Old | New |
|---|---|
| `Padron.Importado(Contribuyentes)` | `TaxpayerRegistry.Imported(Taxpayers)` (POST response: `{ "taxpayers": n }`) |
| `Padron.Response(Rnc, Nombre, NombreComercial, Activo)` | `TaxpayerRegistry.Response(Rnc, Name, TradeName, IsActive)` (GET response) |
| `Padron.Endpoint` | `TaxpayerRegistry.Endpoint` |

## Enum values

`EstadoEnvio` → `SubmissionStatus` (internal, `Abastalia.Fiscal.Domain`; serialized as string on the wire and stored as string in `EcfDocuments.Status`, max 20):

| Old | New |
|---|---|
| `Pendiente` | `Pending` |
| `Aceptado` | `Accepted` |
| `AceptadoCondicional` | `AcceptedConditional` |
| `Rechazado` | `Rejected` |
| `Simulado` | `Simulated` |

`AcceptedConditional` (19 chars) was chosen over `ConditionallyAccepted` (21) so it fits the unchanged column length of 20.

`EcfType` values on the wire come from phase A: `FiscalCredit31`, `Consumer32`, `DebitNote33`, `CreditNote34` (were `CreditoFiscal31`, `Consumo32`, `NotaDebito33`, `NotaCredito34`).

## Error codes (`detail` texts unchanged, in Spanish)

| Old | New | Type |
|---|---|---|
| `Fiscal.SinSecuencia` | `Fiscal.NoSequence` | Conflict (409) |
| `Fiscal.RangoSolapado` | `Fiscal.OverlappingRange` | Conflict (409) |
| `Fiscal.SinRncEmisor` | `Fiscal.MissingIssuerRnc` | Conflict (409) |
| `Fiscal.ReferenciaRequerida` | `Fiscal.ReferenceRequired` | Validation (400) |
| `Fiscal.RncRequerido` | `Fiscal.RncRequired` | Validation (400) |
| `Fiscal.RncNoEncontrado` | `Fiscal.RncNotFound` | NotFound (404) |

`FiscalErrors` members: `SinSecuencia(tipo)` → `NoSequence(type)`, `RangoSolapado` → `OverlappingRange`, `SinRncEmisor` → `MissingIssuerRnc`, `ReferenciaRequerida` → `ReferenceRequired`, `RncRequerido` → `RncRequired`, `Nombre(tipo)` → `Name(type)` (returns the Spanish receipt name, unchanged).

`Fiscal.NoSequence`, `Fiscal.MissingIssuerRnc`, `Fiscal.ReferenceRequired` and `Fiscal.RncRequired` reach clients through `IFiscalDocuments.IssueAsync` (sales, voids, returns).

## Audit actions

| Old | New |
|---|---|
| `fiscal.cargar-secuencia`, resource type `Secuencia`, data = command `{ Tipo, Desde, Hasta, VenceEl }` | `fiscal.load-sequence`, resource type `NcfSequence`, data = command `{ Type, From, To, ExpiresOn }` |

## Config keys

| Old | New | Read by |
|---|---|---|
| `Fiscal:EnvioAutomatico` (bool, default false) | `Fiscal:AutoSubmit` | `EcfSubmissionWorker` (also in `src/Hosts/Abastalia.Host.Local/appsettings.json`) |
| `Fiscal:IntervaloEnvioSegundos` (int, default 30) | `Fiscal:SubmitIntervalSeconds` | `EcfSubmissionWorker` (also in `src/Hosts/Abastalia.Host.Local/appsettings.json`) |

## Health check

| Old | New |
|---|---|
| name `"cola-ecf"` | `"ecf-queue"` |
| class `ColaEcfHealthCheck` | `EcfQueueHealthCheck` |
| Unhealthy: `"Hay e-CF sin enviar desde hace más de 60 horas (el plazo de la DGII es 72)."` | `"Some e-CF have not been sent for more than 60 hours (the DGII deadline is 72)."` |
| Degraded: `"Hay e-CF pendientes desde hace más de 24 horas o rechazados."` | `"Some e-CF have been pending for more than 24 hours or were rejected."` |

## Log messages

| Old | New |
|---|---|
| `"No se pudo enviar el e-CF {ENcf} (intento {Intentos})"` (warning) | `"Could not send e-CF {ENcf} (attempt {Attempts})"` |
| `"Falló la cola de envío de e-CF"` (error) | `"The e-CF submission queue failed"` |
| `"Padrón RNC importado: {Cantidad} contribuyentes"` (information, category `Padron.Importado`) | `"RNC taxpayer registry imported: {Count} taxpayers"` (category `TaxpayerRegistry.Imported`) |

## Database (schema `fiscal`, unchanged)

| Old table | New table |
|---|---|
| `fiscal.SecuenciasEcf` | `fiscal.EcfSequences` |
| `fiscal.DocumentosEcf` | `fiscal.EcfDocuments` |
| `fiscal.Contribuyentes` | `fiscal.Taxpayers` |

| Table | Old column | New column |
|---|---|---|
| EcfSequences | `Id` | `Id` |
| EcfSequences | `Tipo` | `Type` |
| EcfSequences | `Desde` | `From` |
| EcfSequences | `Hasta` | `To` |
| EcfSequences | `Siguiente` | `Next` |
| EcfSequences | `VenceEl` | `ExpiresOn` |
| EcfSequences | `CreadaUtc` | `CreatedUtc` |
| EcfDocuments | `Id` | `Id` |
| EcfDocuments | `ENcf` | `ENcf` |
| EcfDocuments | `Tipo` | `Type` |
| EcfDocuments | `ReferenciaId` | `ReferenceId` |
| EcfDocuments | `ENcfModificado` | `ModifiedENcf` |
| EcfDocuments | `RncComprador` | `BuyerRnc` |
| EcfDocuments | `NombreComprador` | `BuyerName` |
| EcfDocuments | `Itbis` | `Itbis` |
| EcfDocuments | `Total` | `Total` |
| EcfDocuments | `FechaFirmaUtc` | `SignedUtc` |
| EcfDocuments | `CodigoSeguridad` | `SecurityCode` |
| EcfDocuments | `XmlFirmado` | `SignedXml` |
| EcfDocuments | `Estado` | `Status` |
| EcfDocuments | `FechaLimiteEnvioUtc` | `SubmitDeadlineUtc` |
| EcfDocuments | `TrackId` | `TrackId` |
| EcfDocuments | `Intentos` | `Attempts` |
| EcfDocuments | `ProximoIntentoUtc` | `NextAttemptUtc` |
| EcfDocuments | `UltimoError` | `LastError` |
| Taxpayers | `Rnc` | `Rnc` |
| Taxpayers | `Nombre` | `Name` |
| Taxpayers | `NombreComercial` | `TradeName` |
| Taxpayers | `Activo` | `IsActive` |

Keys and indexes: `PK_EcfSequences`, `PK_EcfDocuments`, `PK_Taxpayers` (on `Rnc`), `IX_EcfDocuments_ENcf` (unique), `IX_EcfDocuments_ReferenceId`, `IX_EcfDocuments_Status_SubmitDeadlineUtc`. The module had no filtered indexes or raw SQL. Column types and lengths are unchanged. `From`, `To` are SQL keywords; EF quotes them, but any hand-written SQL must bracket them.

Migration command used (from the repo root):

```
dotnet tool restore
dotnet dotnet-ef migrations add InitialFiscal --project src/Modules/Fiscal/Abastalia.Fiscal --startup-project src/Modules/Fiscal/Abastalia.Fiscal --context FiscalDbContext --output-dir Persistence/Migrations --no-build
```

## e-CF XML

The DGII-defined element names (`ECF`, `Encabezado`, `TipoeCF`, `eNCF`, `RNCEmisor`, …, `FechaHoraFirma`) and the QR URL (`https://ecf.dgii.gov.do/ecf/ConsultaTimbre?RncEmisor=…&CodigoSeguridad=…`) are unchanged. Only our own stand-in element changed: `<FirmaSimulada>` → `<SimulatedSignature>` (added by the simulated signer; the security code is still the first 6 characters of the hash computed before it is added).

DGII_RNC.TXT parsing is unchanged (pipe-separated, column 10 `ACTIVO`, ANSI/UTF-8 detection).

## Internal types and members (for tests and the Host)

All in namespace `Abastalia.Fiscal` unless noted. The project keeps `InternalsVisibleTo` for `Abastalia.UnitTests` and `Abastalia.IntegrationTests`.

### Module registration and DbContext

| Old | New |
|---|---|
| `FiscalModule.AddFiscalModule(services)` | (unchanged) |
| `FiscalDbContext` | (unchanged) |
| `FiscalDbContext.Secuencias` | `FiscalDbContext.Sequences` |
| `FiscalDbContext.Documentos` | `FiscalDbContext.Documents` |
| `FiscalDbContext.Contribuyentes` | `FiscalDbContext.Taxpayers` |
| `FiscalConfiguration` | (unchanged) |
| `DisenoFiscal : DisenoDbContext<FiscalDbContext>` | `FiscalDesignTimeFactory : DesignTimeFactory<FiscalDbContext>` |

### Domain (`Abastalia.Fiscal.Domain`)

| Old | New |
|---|---|
| `Secuencia` | `NcfSequence` |
| `Secuencia.Id / Tipo / Desde / Hasta / Siguiente / VenceEl / CreadaUtc` | `NcfSequence.Id / Type / From / To / Next / ExpiresOn / CreatedUtc` |
| `Secuencia.Usados / Total / PorcentajeUsado` (computed, not mapped) | `NcfSequence.Used / Total / PercentUsed` |
| `EstadoEnvio` | `SubmissionStatus` (values above) |
| `DocumentoFiscal` | `FiscalDocument` |
| `DocumentoFiscal.Id / ENcf / Tipo / ReferenciaId / ENcfModificado / RncComprador / NombreComprador / Itbis / Total / FechaFirmaUtc / CodigoSeguridad / XmlFirmado / Estado / FechaLimiteEnvioUtc / TrackId / Intentos / ProximoIntentoUtc / UltimoError` | `FiscalDocument.Id / ENcf / Type / ReferenceId / ModifiedENcf / BuyerRnc / BuyerName / Itbis / Total / SignedUtc / SecurityCode / SignedXml / Status / SubmitDeadlineUtc / TrackId / Attempts / NextAttemptUtc / LastError` |
| `Contribuyente` | `Taxpayer` |
| `Contribuyente.Rnc / Nombre / NombreComercial / Activo` | `Taxpayer.Rnc / Name / TradeName / IsActive` |
| `ENcf.Formatear(TipoEcf tipo, long numero)` | `ENcf.Format(EcfType type, long number)` |
| `FiscalErrors` | (unchanged; members above) |

### Issuing (`IFiscalDocuments` implementation and signer)

| Old | New |
|---|---|
| `ComprobantesFiscales` (implements `IFiscalDocuments`) | `FiscalDocuments` |
| `ComprobantesFiscales(db, IConfiguracionConsultas configuracion, IEcfFirmador firmador, IOutbox outbox, TimeProvider reloj)` | `FiscalDocuments(db, ISettingsQueries settings, IEcfSigner signer, IOutbox outbox, TimeProvider clock)` |
| `ComprobantesFiscales.PlazoEnvio` (72 h) | `FiscalDocuments.SubmitDeadline` |
| `ComprobantesFiscales.EmitirAsync(doc, ct)` | `FiscalDocuments.IssueAsync(doc, ct)` |
| `ReservarAsync` / `ArmarXml` / `Indicador` / `UrlQr` / `Comprimir` (private) | `ReserveAsync` / `BuildXml` / `BillingIndicator` / `QrUrl` / `Compress` |
| `IEcfFirmador` | `IEcfSigner` |
| `IEcfFirmador.EsSimulado` | `IEcfSigner.IsSimulated` |
| `IEcfFirmador.Firmar(XDocument xml)` → `(XmlFirmado, CodigoSeguridad)` | `IEcfSigner.Sign(XDocument xml)` → `(SignedXml, SecurityCode)` |
| `FirmadorSimulado` | `SimulatedEcfSigner` |

### Submission queue

| Old | New |
|---|---|
| `IEcfProveedor` | `IEcfProvider` |
| `IEcfProveedor.EnviarAsync(string encf, byte[] xmlFirmadoGzip, CancellationToken ct)` | `IEcfProvider.SubmitAsync(string encf, byte[] signedXmlGzip, CancellationToken ct)` |
| `ResultadoEnvio(EstadoEnvio Estado, string? TrackId, string? Mensaje = null)` | `SubmissionResult(SubmissionStatus Status, string? TrackId, string? Message = null)` |
| `ProveedorSimulado` | `SimulatedEcfProvider` |
| `EnvioEcf` | `EcfSubmissionQueue` |
| `EnvioEcf(FiscalDbContext db, IEcfProveedor proveedor, TimeProvider reloj, ILogger<EnvioEcf> log)` | `EcfSubmissionQueue(FiscalDbContext db, IEcfProvider provider, TimeProvider clock, ILogger<EcfSubmissionQueue> log)` |
| `EnvioEcf.Lote` (20) | `EcfSubmissionQueue.BatchSize` |
| `EnvioEcf.ProcesarAsync(ct)` | `EcfSubmissionQueue.ProcessAsync(ct)` |
| `EnvioEcf.Espera(int intentos)` | `EcfSubmissionQueue.RetryDelay(int attempts)` |
| `EnvioEcfWorker` (hosted service) | `EcfSubmissionWorker` |
| `ColaEcfHealthCheck` | `EcfQueueHealthCheck` |

### Feature classes (`Abastalia.Fiscal.Features`)

| Old | New |
|---|---|
| `CargarSecuencia` | `LoadSequence` |
| `ListarSecuencias` | `ListSequences` |
| `AlertasSecuencias` | `SequenceAlerts` |
| `EstadoCola` | `QueueStatus` |
| `ListarDocumentos` | `ListDocuments` |
| `Padron` | `TaxpayerRegistry` |
| `Padron.Lote` (private, 5000) | `TaxpayerRegistry.BatchSize` |
| `Padron.Leer(string linea)` → `Contribuyente?` | `TaxpayerRegistry.Read(string line)` → `Taxpayer?` |
| `Padron.CodificacionAsync(Stream archivo, CancellationToken ct)` | `TaxpayerRegistry.DetectEncodingAsync(Stream file, CancellationToken ct)` |

## For the test, host and client agents

- `src/Hosts/Abastalia.Host.Local/appsettings.json`: section `Fiscal` keys `EnvioAutomatico` → `AutoSubmit`, `IntervaloEnvioSegundos` → `SubmitIntervalSeconds`. `Program.cs` needs no change (`FiscalModule`, `AddFiscalModule` kept).
- Tests that build a queue by hand: `new EcfSubmissionQueue(db, provider, clock, NullLogger<EcfSubmissionQueue>.Instance)`; a test provider implements `IEcfProvider.SubmitAsync` and returns `SubmissionResult`.
- Tests that seed documents: `new FiscalDocument { …, Type = EcfType.Consumer32, ReferenceId, SignedUtc, SecurityCode, SignedXml, Status = SubmissionStatus.Pending, SubmitDeadlineUtc, NextAttemptUtc }` into `db.Documents`.
- JSON reads: e.g. the taxpayer lookup returns `name` (was `nombre`), `tradeName`, `isActive`; the queue returns `pending`, `overdue`, `nextDeadlineUtc`, `rejected`, `simulated`.
