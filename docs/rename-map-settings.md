# Rename map, phase B: Settings module

Spanish → English renames in `src/Modules/Settings/Abastalia.Settings/**`. Tests, the Host and the client must update their references with this map. Behavior did not change: only names, comments, log text and the on-disk names listed under "Files on disk".

Builds on `docs/rename-map.md` (phase A). The Contracts (`BusinessProfile`, `ISettingsQueries`) were already renamed there.

## Source files

| Old | New |
|---|---|
| `ConfiguracionModule.cs` | `SettingsModule.cs` |
| `Auditoria.cs` | `Audit.cs` |
| `CifradoRespaldo.cs` | `BackupEncryption.cs` |
| `Licencias.cs` | `License.cs` |
| `Respaldos.cs` | `Backups.cs` |
| `Persistence/Migraciones/` (`InicialConfiguracion`, `FraseRespaldoConfiguracion`, `ConfiguracionDbContextModelSnapshot`) | `Persistence/Migrations/` (`20261015152345_InitialSettings`, `SettingsDbContextModelSnapshot`, regenerated) |

Namespace stays `Abastalia.Settings`; migrations are in `Abastalia.Settings.Persistence.Migrations`.

## Routes

| Old | New |
|---|---|
| `GET /api/configuracion/negocio` | `GET /api/settings/business` |
| `PUT /api/configuracion/negocio` | `PUT /api/settings/business` |
| `PUT /api/configuracion/licencia` | `PUT /api/settings/license` |
| `GET /api/configuracion/funciones` | `GET /api/settings/features` |
| `GET /api/auditoria/verificar` | `GET /api/audit/verify` |
| `PUT /api/respaldos/frase` | `PUT /api/backups/passphrase` |
| `POST /api/respaldos` (201, `Location: /api/respaldos/{archivo}`) | `POST /api/backups` (201, `Location: /api/backups/{fileName}`) |
| `GET /api/respaldos` | `GET /api/backups` |

Permissions are unchanged in meaning: every route that required `configuracion.gestionar` now requires `settings.manage` (`Permissions.SettingsManage`). `GET /api/settings/business` and `GET /api/settings/features` still only require a signed-in user.

OpenAPI tags: `Configuración` → `Settings`, `Auditoría` → `Audit`, `Respaldos` → `Backups`.

## Requests and responses (JSON, camelCase)

### `GET /api/settings/business` → `BusinessProfile` (Contracts)

| Old field | New field |
|---|---|
| `nombre` | `name` |
| `rnc` | `rnc` |
| `direccion` | `address` |
| `telefono` | `phone` |
| `pieRecibo` | `receiptFooter` |
| `topeDescuentoPct` | `discountCapPercent` |

### `PUT /api/settings/business`: `ActualizarNegocio.Command` → `UpdateBusinessProfile.Command`

| Old field | New field |
|---|---|
| `nombre` | `name` |
| `rnc` | `rnc` |
| `direccion` | `address` |
| `telefono` | `phone` |
| `pieRecibo` | `receiptFooter` |
| `topeDescuentoPct` | `discountCapPercent` |

### `PUT /api/settings/license`: `InstalarLicencia.Command` → `InstallLicense.Command`

| Old field | New field |
|---|---|
| `licencia` | `license` |

### `GET /api/settings/features`: `InstalarLicencia.Funciones` → `InstallLicense.Features`

| Old field | New field |
|---|---|
| `estado` (`EstadoLicencia`) | `status` (`LicenseStatus`) |
| `plan` | `plan` |
| `venceUtc` | `expiresUtc` |
| `activas` (list of `Funcion` names) | `enabled` (list of `Feature` names) |
| `cajas` | `registers` |

`status` values: `Prueba` → `Trial`, `Activa` → `Active`, `Gracia` → `Grace`, `Restringida` → `Restricted`.
`enabled` values: `ModuloCarniceria` → `ButcherModule`, `ModuloMayorista` → `WholesaleModule`, `CopiaNube` → `CloudBackup`, `AsistenteIA` → `AiAssistant`.

### `GET /api/audit/verify`: `VerificarAuditoria.Response` → `VerifyAudit.Response`

| Old field | New field |
|---|---|
| `intacta` | `isIntact` |
| `primerRegistroRoto` | `firstBrokenRecord` |

### `PUT /api/backups/passphrase`: `FraseRespaldo.Command` → `SetBackupPassphrase.Command`

| Old field | New field |
|---|---|
| `frase` | `passphrase` |

### `POST /api/backups` and `GET /api/backups` (item): `RespaldoHecho` → `BackupFile`

| Old field | New field |
|---|---|
| `archivo` | `fileName` |
| `bytes` | `bytes` |
| `fechaUtc` | `dateUtc` |

## License file (JWS payload)

`DatosLicencia` → `LicenseData`. The payload is JSON with web (camelCase) names, so licenses must be signed with the new field names; licenses signed with the old names no longer parse (they fail as `License.Invalid`). The license-issuing script kept outside the repo must change too.

| Old field | New field |
|---|---|
| `tienda` | `store` |
| `plan` | `plan` |
| `funciones` | `features` (values: `Feature` names, see above) |
| `limites` | `limits` (key `Cajas` → `Registers`) |
| `emitidaUtc` | `issuedUtc` |
| `venceUtc` | `expiresUtc` |

The JWS header (`{"alg":"ES256","typ":"JWT"}`) and the embedded public key are unchanged.

## Error codes (`detail` text unchanged, Spanish)

| Old | New | Where |
|---|---|---|
| `Licencia.Invalida` | `License.Invalid` | `LicenseErrors.Invalid` |
| `Respaldo.SinFrase` | `Backup.NoPassphrase` | `Backups.NoPassphrase` |
| `Respaldo.FraseIlegible` | `Backup.UnreadablePassphrase` | `Backups.UnreadablePassphrase` |

Note: phase A's convention says the prefix is the project name (`Settings.…`); these use the area names `License` / `Backup`, as the phase B instructions asked (`Respaldo.SinFrase` → `Backup.NoPassphrase`).

## Audit actions

| Old (action, resourceType) | New (action, resourceType) | Data |
|---|---|---|
| `configuracion.negocio`, `Negocio` | `settings.business`, `Business` | the command (`{ Name, Rnc, Address, Phone, ReceiptFooter, DiscountCapPercent }`) |
| `configuracion.licencia`, `Licencia` | `settings.license`, `License` | `{ Plan, ExpiresUtc, Features, Limits }` (was `{ Plan, VenceUtc, Funciones, Limites }`) |
| `configuracion.frase-respaldo`, `Negocio` | `settings.backup-passphrase`, `Business` | `{ }` |

## Health checks

| Old name | New name | Class |
|---|---|---|
| `auditoria` | `audit` | `UltimaVerificacionAuditoria` → `LastAuditCheck` |
| `respaldo` | `backup` | `RespaldoHealthCheck` → `BackupHealthCheck` |

Descriptions stay in Spanish (shown to the owner).

## Config keys

| Old | New | Read by | Default |
|---|---|---|---|
| `Abastalia:CarpetaClaves` (env `Abastalia__CarpetaClaves`) | `Abastalia:KeysFolder` (env `Abastalia__KeysFolder`) | `AddSettingsModule` (Data Protection key ring) | `C:\ProgramData\Abastalia\keys` (was `…\claves`) |
| `Auditoria:VerificacionNocturna` | `Audit:NightlyCheck` | `NightlyAuditCheck` | `true` |
| `Licencia:ClavePublica` | `License:PublicKey` | `LicenseService` (ignored in Production) | Abastalia's key |
| `Licencia:Archivo` | `License:File` | `LicenseService` | `C:\ProgramData\Abastalia\license.jws` (was `licencia.jws`) |
| `Respaldo:Carpeta` (env `Respaldo__Carpeta`) | `Backup:Folder` (env `Backup__Folder`) | `Backups.Folder` | `C:\ProgramData\Abastalia\backups` (was `…\respaldos`) |
| `Respaldo:Conservar` | `Backup:Keep` | `Backups` | `14` |
| `Respaldo:CopiaEn` | `Backup:CopyTo` | `Backups.CreateAsync` | none |
| `Respaldo:Automatico` | `Backup:Automatic` | `NightlyBackup` | `true` |
| `Respaldo:Hora` | `Backup:Time` | `NightlyBackup` | `23:30:00` |

Users of these keys outside the module: `tests/Abastalia.IntegrationTests/Infra/AbastaliaApp.cs` (`Auditoria:VerificacionNocturna`, `Respaldo:Automatico`, `Abastalia:CarpetaClaves`, `Licencia:ClavePublica`, `Licencia:Archivo`, `Respaldo:Carpeta`), `tests/Abastalia.IntegrationTests/Configuracion/Respaldos*Tests.cs` (`Respaldo:Carpeta`, `Respaldo:CopiaEn`, `Respaldo:Conservar`), `tests/Abastalia.E2ETests/ServidorAbastalia.cs` (`Respaldo__Carpeta`, `Abastalia__CarpetaClaves`), README.

## Files on disk

| Old | New | Note |
|---|---|---|
| backup extension `.abastalia-respaldo` (`Respaldos.Extension`) | `.abastalia-backup` (`Backups.Extension`) | Spanish word, translated. Old `.abastalia-respaldo` files are not listed, pruned or counted by the health check any more; `decrypt-backup` still opens them (same format). |
| temporary `<name>.abastalia-respaldo.parcial` | `<name>.abastalia-backup.partial` | |
| file prefix `abastalia-yyyyMMdd-HHmmss` | (unchanged) | |
| work folder `respaldos\trabajo` (`Respaldos.Trabajo`) | `backups\work` (`Backups.WorkFolder`) | **Host**: `PrepararServicio.cs` creates `respaldos`, `claves` and `respaldos\trabajo` and grants SQL Server Modify on the work folder; it must create `backups`, `keys` and `backups\work` instead, or SQL Server backups fail under the service account. Tests that look in `Path.Combine(folder, "trabajo")` must use `"work"`. |
| default backup folder `C:\ProgramData\Abastalia\respaldos` | `C:\ProgramData\Abastalia\backups` | README mentions the old path. |
| Data Protection key folder `C:\ProgramData\Abastalia\claves` | `C:\ProgramData\Abastalia\keys` | |
| license file `C:\ProgramData\Abastalia\licencia.jws` | `C:\ProgramData\Abastalia\license.jws` | |
| Data Protection purpose `Abastalia.Respaldo.Clave` | `Abastalia.Backup.Key` | Keys stored with the old purpose cannot be unprotected (`Backup.UnreadablePassphrase`); databases start fresh anyway. |

**Kept on purpose (backup file format, byte-compatible):** magic bytes `TOKIRSP2`, HKDF info `toki-respaldo`, header layout, chunk size, PBKDF2 iterations (600,000). A backup made before the rename decrypts with the new code. The `CryptographicException` messages (`No es un respaldo de Abastalia.`, `El respaldo está dañado.`, `El respaldo está cortado.`) stay in Spanish: `decrypt-backup` prints them to the person restoring.

## Console command (in the Host, not in this module)

`descifrar-respaldo <archivo> <salida>` → `decrypt-backup <file> <output>` (glossary). It calls `CifradoRespaldo.DescifrarAsync` → `BackupEncryption.DecryptAsync`. The environment variable `ABASTALIA_FRASE_RESPALDO` is defined in `Program.cs`; suggested `ABASTALIA_BACKUP_PASSPHRASE`.

## Database (schema `config` → `settings`)

| Old table | New table |
|---|---|
| `config.Negocio` | `settings.Business` |

| Old column | New column |
|---|---|
| `Id` | `Id` |
| `Nombre` | `Name` |
| `Rnc` | `Rnc` |
| `Direccion` | `Address` |
| `Telefono` | `Phone` |
| `PieRecibo` | `ReceiptFooter` |
| `TopeDescuentoPct` | `DiscountCapPercent` |
| `RespaldoSal` | `BackupSalt` |
| `RespaldoClave` | `BackupKey` |

Seed (unchanged values): `Id = 1`, `Name = "Mi tienda"`, `DiscountCapPercent = 10`.

Migration command used (from the repo root):

```
dotnet tool restore
dotnet dotnet-ef migrations add InitialSettings --project src/Modules/Settings/Abastalia.Settings --startup-project src/Modules/Settings/Abastalia.Settings --context SettingsDbContext --output-dir Persistence/Migrations --no-build
```

## Types and members

### Module registration and persistence (`SettingsModule.cs`)

| Old | New |
|---|---|
| `ConfiguracionModule` | `SettingsModule` |
| `ConfiguracionModule.AddConfiguracionModule(services, config)` | `SettingsModule.AddSettingsModule(services, config)` |
| `ConfiguracionDbContext` (internal) | `SettingsDbContext` |
| `ConfiguracionDbContext.Negocio` (`DbSet<Negocio>`) | `SettingsDbContext.Business` (`DbSet<Business>`) |
| `Negocio` (internal entity) | `Business` |
| `Negocio.Nombre` / `Direccion` / `Telefono` / `PieRecibo` / `TopeDescuentoPct` | `Business.Name` / `Address` / `Phone` / `ReceiptFooter` / `DiscountCapPercent` |
| `Negocio.RespaldoSal` / `RespaldoClave` | `Business.BackupSalt` / `BackupKey` |
| `Negocio.ADatos()` | `Business.ToProfile()` |
| `NegocioConfiguration` | `BusinessConfiguration` |
| `ConfiguracionConsultas` (implements `ISettingsQueries`) | `SettingsQueries` |
| `ObtenerNegocio` (endpoint) | `GetBusinessProfile` |
| `ActualizarNegocio` (`Command`, `Validator`, `Handler`, `Endpoint`) | `UpdateBusinessProfile` |
| `DisenoConfiguracion` (design-time factory) | `SettingsDesignTimeFactory` |

### Audit (`Audit.cs`)

| Old | New |
|---|---|
| `VerificarAuditoria` (endpoint, `Response`) | `VerifyAudit` |
| `UltimaVerificacionAuditoria` (singleton, health check) | `LastAuditCheck` |
| `UltimaVerificacionAuditoria.PrimerRegistroRoto` | `LastAuditCheck.FirstBrokenRecord` |
| `VerificacionNocturnaAuditoria` (hosted service) | `NightlyAuditCheck` |
| `VerificacionNocturnaAuditoria.VerificarAsync(ct)` | `NightlyAuditCheck.VerifyAsync(ct)` |
| `VerificacionNocturnaAuditoria.HastaLaProxima(DateTime ahoraUtc, TimeSpan hora)` | `NightlyAuditCheck.UntilNext(DateTime nowUtc, TimeSpan time)` |
| `VerificacionNocturnaAuditoria.Hora` (private) | `NightlyAuditCheck.CheckTime` |

### Backup encryption (`BackupEncryption.cs`, public)

| Old | New |
|---|---|
| `CifradoRespaldo` | `BackupEncryption` |
| `CifradoRespaldo.Iteraciones` | `BackupEncryption.Iterations` |
| `CifradoRespaldo.NuevaSal()` | `BackupEncryption.NewSalt()` |
| `CifradoRespaldo.DerivarClave(string frase, byte[] sal, int iteraciones = Iteraciones)` | `BackupEncryption.DeriveKey(string passphrase, byte[] salt, int iterations = Iterations)` |
| `CifradoRespaldo.CifrarAsync(Stream entrada, Stream salida, byte[] clave, byte[] sal, CancellationToken ct)` | `BackupEncryption.EncryptAsync(Stream input, Stream output, byte[] key, byte[] salt, CancellationToken ct)` |
| `CifradoRespaldo.DescifrarAsync(Stream entrada, Stream salida, string frase, CancellationToken ct)` | `BackupEncryption.DecryptAsync(Stream input, Stream output, string passphrase, CancellationToken ct)` |
| private `Bloque` / `LargoEncabezado` / `Firma` / `ClaveDelArchivo` / `Asociados` | `ChunkSize` / `HeaderLength` / `Magic` / `FileKey` / `AssociatedData` |

### Backups (`Backups.cs`)

| Old | New |
|---|---|
| `RespaldoHecho(Archivo, Bytes, FechaUtc)` (public record) | `BackupFile(FileName, Bytes, DateUtc)` |
| `EstadoRespaldo` (internal singleton) | `BackupStatus` |
| `EstadoRespaldo.ErrorCopia` | `BackupStatus.CopyError` |
| `Respaldos` (internal, scoped) | `Backups` |
| ctor `Respaldos(ConfiguracionDbContext db, DatabaseOptions opciones, IDataProtectionProvider proteccion, IConfiguration config, TimeProvider reloj, EstadoRespaldo estado, ILogger<Respaldos> log)` | `Backups(SettingsDbContext db, DatabaseOptions options, IDataProtectionProvider protection, IConfiguration config, TimeProvider clock, BackupStatus status, ILogger<Backups> log)` (same order) |
| `Respaldos.Extension` (`".abastalia-respaldo"`) | `Backups.Extension` (`".abastalia-backup"`) |
| `Respaldos.SinFrase` / `FraseIlegible` | `Backups.NoPassphrase` / `UnreadablePassphrase` |
| `Respaldos.Carpeta` / `Trabajo` | `Backups.Folder` / `WorkFolder` |
| `Respaldos.Conservar` (private) | `Backups.Keep` |
| `Respaldos.Protector` | `Backups.Protector` |
| `Respaldos.ClaveAsync(ct)` → `Result<(byte[] Clave, byte[] Sal)>` | `Backups.KeyAsync(ct)` → `Result<(byte[] Key, byte[] Salt)>` |
| `Respaldos.CrearAsync(ct)` → `Result<RespaldoHecho>` | `Backups.CreateAsync(ct)` → `Result<BackupFile>` |
| `Respaldos.Listar()` | `Backups.List()` |
| `Respaldos.Podar(carpeta, dejarLugar)` (private) | `Backups.Prune(folder, makeRoom)` |
| `FraseRespaldo` (`Command`, `Validator`, `Handler`, `Endpoint`) | `SetBackupPassphrase` |
| `RespaldoNocturno` (hosted service) | `NightlyBackup` |
| `RespaldoNocturno.RespaldarAsync(bool soloSiHaceFalta, ct)` | `NightlyBackup.BackUpAsync(bool onlyIfDue, ct)` |
| `RespaldoHealthCheck` | `BackupHealthCheck` |

### License (`License.cs`)

| Old | New |
|---|---|
| `DatosLicencia(Tienda, Plan, Funciones, Limites, EmitidaUtc, VenceUtc)` (public record) | `LicenseData(Store, Plan, Features, Limits, IssuedUtc, ExpiresUtc)` |
| `Jws.Firmar(DatosLicencia datos, ECDsa clavePrivada)` | `Jws.Sign(LicenseData data, ECDsa privateKey)` |
| `Jws.Verificar(string jws, ECDsa clavePublica)` → `Result<DatosLicencia>` | `Jws.Verify(string jws, ECDsa publicKey)` → `Result<LicenseData>` |
| `LicenciaErrors.Invalida` | `LicenseErrors.Invalid` |
| `ServicioLicencia` (internal singleton, also `IFeatureService`) | `LicenseService` |
| ctor `(IConfiguration config, IHostEnvironment entorno, TimeProvider reloj)` | `(IConfiguration config, IHostEnvironment environment, TimeProvider clock)` |
| `ServicioLicencia.Gracia` | `LicenseService.GracePeriod` |
| `ServicioLicencia.CajasDePrueba` | `LicenseService.TrialRegisters` |
| `ServicioLicencia.Archivo` | `LicenseService.FilePath` |
| `ServicioLicencia.Datos` | `LicenseService.Data` |
| `ServicioLicencia.Estado` | `LicenseService.Status` |
| `ServicioLicencia.EstaActiva(Funcion)` / `LimiteDe(Limite)` | `LicenseService.IsEnabled(Feature)` / `LimitOf(Limit)` |
| `ServicioLicencia.Instalar(string jws)` | `LicenseService.Install(string jws)` |
| private `ClavePublicaAbastalia` / `Vigente` | `AbastaliaPublicKey` / `IsCurrent` |
| `InstalarLicencia` (`Command`, `Validator`, `Handler`, `Funciones`, `Endpoint`) | `InstallLicense` (`Command`, `Validator`, `Handler`, `Features`, `Endpoint`) |

## Log texts (now English)

- `"La cadena de auditoría está rota desde el registro {Secuencia}"` → `"The audit chain is broken from record {Sequence}"`
- `"No se pudo verificar la auditoría"` → `"Could not verify the audit log"`
- `"No se pudo copiar el respaldo a {Copia}"` → `"Could not copy the backup to {Copy}"`
- `"No se hizo el respaldo: {Motivo}"` → `"The backup was not made: {Reason}"`
- `"Falló el respaldo"` → `"The backup failed"`
