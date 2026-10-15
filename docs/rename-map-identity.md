# Rename map, phase B: Identity module

Spanish → English renames in `src/Modules/Identity/Toki.Identity/**`. Behavior did not change. Tests, the Host and the client update their references with this map. Phase A names (`docs/rename-map.md`) are used as given.

## Files and folders

| Old | New |
|---|---|
| `IdentidadModule.cs` | `IdentityModule.cs` |
| `Domain/Identidad.cs` | `Domain/Identity.cs` |
| `Features/AutorizarSupervisor.cs` | `Features/SupervisorApprovals.cs` |
| `Features/Dispositivos.cs` | `Features/Devices.cs` |
| `Features/Instalar.cs` | `Features/Setup.cs` |
| `Features/Roles.cs` | (unchanged) |
| `Features/Sesiones.cs` | `Features/Sessions.cs` |
| `Features/Usuarios.cs` | `Features/Users.cs` |
| `Persistence/IdentidadDbContext.cs` | `Persistence/IdentityDbContext.cs` |
| `Persistence/Migraciones/` (`20261015095635_InicialIdentidad`) | `Persistence/Migrations/` (`20261015152435_InitialIdentity`, regenerated) |

## Module registration (Host)

| Old | New |
|---|---|
| `IdentidadModule` (public static class) | `IdentityModule` |
| `IdentidadModule.AddIdentidadModule(services)` | `IdentityModule.AddIdentityModule(services)` |
| `IdentidadModule.Esquema` (`"Toki"`) | `IdentityModule.Scheme` (value `"Toki"`, unchanged) |
| `IdentidadModule.HeaderDispositivo` (`"X-Dispositivo"`) | `IdentityModule.DeviceHeader` (`"X-Device"`) |

`src/Hosts/Toki.Host.Local/Program.cs`: `typeof(IdentidadModule).Assembly` → `typeof(IdentityModule).Assembly`, `AddIdentidadModule()` → `AddIdentityModule()`.

## HTTP header

| Old | New |
|---|---|
| `X-Dispositivo` | `X-Device` |

## Routes

All endpoints use OpenAPI tag `Identity` (was `Identidad`).

| Method | Old | New | Feature class (old → new) |
|---|---|---|---|
| POST | `/api/identidad/instalacion` | `/api/identity/setup` | `Instalar` → `Setup` |
| POST | `/api/identidad/sesiones` | `/api/identity/sessions` | `IniciarSesion` → `OpenSession` |
| DELETE | `/api/identidad/sesiones/actual` | `/api/identity/sessions/current` | `CerrarSesion` → `CloseSession` |
| GET | `/api/identidad/yo` | `/api/identity/me` | `Yo` → `Me` |
| POST | `/api/identidad/yo/pin` | `/api/identity/me/pin` | `CambiarMiPin` → `ChangeMyPin` |
| POST | `/api/identidad/autorizaciones` | `/api/identity/approvals` | `AutorizarSupervisor` → `GrantSupervisorApproval` |
| POST | `/api/identidad/dispositivos/codigos` | `/api/identity/devices/pairing-codes` | `CrearCodigoEmparejamiento` → `CreatePairingCode` |
| POST | `/api/identidad/dispositivos/emparejar` | `/api/identity/devices/pair` | `EmparejarDispositivo` → `PairDevice` |
| GET | `/api/identidad/dispositivos` | `/api/identity/devices` | `ListarDispositivos` → `ListDevices` |
| POST | `/api/identidad/dispositivos/{id:guid}/revocar` | `/api/identity/devices/{id:guid}/revoke` | `RevocarDispositivo` → `RevokeDevice` |
| GET | `/api/identidad/roles` | `/api/identity/roles` | `ListarRoles` → `ListRoles` |
| POST | `/api/identidad/roles` | `/api/identity/roles` | `GuardarRol` → `SaveRole` |
| PUT | `/api/identidad/roles/{id:guid}` | `/api/identity/roles/{id:guid}` | `GuardarRol` → `SaveRole` |
| POST | `/api/identidad/usuarios` | `/api/identity/users` | `CrearUsuario` → `CreateUser` |
| PUT | `/api/identidad/usuarios/{id:guid}` | `/api/identity/users/{id:guid}` | `ActualizarUsuario` → `UpdateUser` |
| GET | `/api/identidad/usuarios` | `/api/identity/users` | `ListarUsuarios` → `ListUsers` |
| GET | `/api/identidad/usuarios/activos` | `/api/identity/users/active` | `ListarCajeros` → `ListCashiers` |

`Location` headers of 201 responses: `/api/identidad/usuarios/{id}` → `/api/identity/users/{id}` (setup and create user), `/api/identidad/roles/{id}` → `/api/identity/roles/{id}` (create role).

Status codes, auth requirements (`AllowAnonymous` / `RequireAuthorization` / permission) and result shapes are unchanged.

## Request and response bodies (JSON, camelCase)

Every nested type keeps its name (`Command`, `Response`, `Item`, `Body`) inside the renamed feature class, e.g. `IniciarSesion.Command` → `OpenSession.Command`.

### POST /api/identity/setup (`Setup`)

| | Old | New |
|---|---|---|
| Command | `nombreAdministrador` | `administratorName` |
| | `codigoAdministrador` | `administratorCode` |
| | `pin` | `pin` |
| | `nombreDispositivo` | `deviceName` |
| Response | `usuarioId` | `userId` |
| | `dispositivoId` | `deviceId` |
| | `tokenDispositivo` | `deviceToken` |

### POST /api/identity/sessions (`OpenSession`)

| | Old | New |
|---|---|---|
| Command | `codigo` | `code` |
| | `pin` | `pin` |
| | `tokenDispositivo` (init property, overwritten by the endpoint from the header) | `deviceToken` |
| Response | `token` | `token` |
| | `usuarioId` | `userId` |
| | `nombre` | `name` |
| | `expiraUtc` | `expiresUtc` |
| | `fallo` (always `null` on 200) | `failure` |

### DELETE /api/identity/sessions/current (`CloseSession`)

No body. Internal `Command(SesionId)` → `Command(SessionId)`.

### GET /api/identity/me (`Me`)

| Old | New |
|---|---|
| `usuarioId` | `userId` |
| `nombre` | `name` |
| `dispositivoId` | `deviceId` |
| `permisos` | `permissions` |

### POST /api/identity/me/pin (`ChangeMyPin`)

| Old | New |
|---|---|
| `pinActual` | `currentPin` |
| `pinNuevo` | `newPin` |

### POST /api/identity/approvals (`GrantSupervisorApproval`)

| | Old | New |
|---|---|---|
| Command | `codigo` | `code` |
| | `pin` | `pin` |
| | `permiso` | `permission` |
| | `recursoId` | `resourceId` |
| Response | `autorizacionId` | `approvalId` |
| | `supervisor` | `supervisor` |
| | `expiraUtc` | `expiresUtc` |
| | `fallo` (always `null` on 200) | `failure` |

The client sends `approvalId` back as `supervisorApprovalId` on commands that implement `IRequiresApproval` (phase A).

### POST /api/identity/devices/pairing-codes (`CreatePairingCode`)

| | Old | New |
|---|---|---|
| Command | `nombreDispositivo` | `deviceName` |
| Response | `codigo` | `code` |
| | `expiraUtc` | `expiresUtc` |

`CrearCodigoEmparejamiento.Vigencia` → `CreatePairingCode.Validity`.

### POST /api/identity/devices/pair (`PairDevice`)

| | Old | New |
|---|---|---|
| Command | `codigo` | `code` |
| Response | `dispositivoId` | `deviceId` |
| | `nombre` | `name` |
| | `token` | `token` |

### GET /api/identity/devices (`ListDevices`), array of `Item`

| Old | New |
|---|---|
| `id` | `id` |
| `nombre` | `name` |
| `revocado` | `isRevoked` |
| `creadoUtc` | `createdUtc` |

### POST /api/identity/devices/{id}/revoke (`RevokeDevice`)

No body. `Command(Id)` unchanged.

### GET /api/identity/roles (`ListRoles`)

| | Old | New |
|---|---|---|
| Response | `roles` | `roles` |
| | `permisosDisponibles` | `availablePermissions` |
| Item | `id` | `id` |
| | `nombre` | `name` |
| | `permisos` | `permissions` |

### POST / PUT /api/identity/roles (`SaveRole`)

| | Old | New |
|---|---|---|
| Body | `nombre` | `name` |
| | `permisos` | `permissions` |
| Response (POST) | `id` | `id` |
| Command (internal) | `Command(Id, Nombre, Permisos)` | `Command(Id, Name, Permissions)` |

### POST /api/identity/users (`CreateUser`)

| | Old | New |
|---|---|---|
| Command | `nombre` | `name` |
| | `codigo` | `code` |
| | `pin` | `pin` |
| | `rolId` | `roleId` |
| Response | `id` | `id` |

### PUT /api/identity/users/{id} (`UpdateUser`)

| | Old | New |
|---|---|---|
| Body | `nombre` | `name` |
| | `rolId` | `roleId` |
| | `activo` | `isActive` |
| | `pin` (optional) | `pin` |
| Command (internal) | `Command(Id, Nombre, RolId, Activo, Pin)` | `Command(Id, Name, RoleId, IsActive, Pin)` |

### GET /api/identity/users (`ListUsers`), array of `Item`

| Old | New |
|---|---|
| `id` | `id` |
| `nombre` | `name` |
| `codigo` | `code` |
| `rolId` | `roleId` |
| `rol` | `role` |
| `activo` | `isActive` |
| `bloqueado` | `isLocked` |

### GET /api/identity/users/active (`ListCashiers`), array of `Item`

| Old | New |
|---|---|
| `id` | `id` |
| `nombre` | `name` |
| `codigo` | `code` |

## Error codes

`detail` texts are unchanged (Spanish).

| Old | New | Constant (old → new) |
|---|---|---|
| `Identidad.CredencialesInvalidas` | `Identity.InvalidCredentials` | `IdentidadErrors.CredencialesInvalidas` → `IdentityErrors.InvalidCredentials` |
| `Identidad.UsuarioBloqueado` | `Identity.UserLocked` | `UsuarioBloqueado` → `UserLocked` |
| `Identidad.DispositivoNoEmparejado` | `Identity.DeviceNotPaired` | `DispositivoNoEmparejado` → `DeviceNotPaired` |
| `Identidad.YaInstalado` | `Identity.AlreadySetUp` | `YaInstalado` → `AlreadySetUp` |
| `Identidad.CodigoDuplicado` | `Identity.DuplicateCode` | `CodigoDuplicado` → `DuplicateCode` |
| `Identidad.UsuarioNoEncontrado` | `Identity.UserNotFound` | `UsuarioNoEncontrado` → `UserNotFound` |
| `Identidad.RolNoEncontrado` | `Identity.RoleNotFound` | `RolNoEncontrado` → `RoleNotFound` |
| `Identidad.DispositivoNoEncontrado` | `Identity.DeviceNotFound` | `DispositivoNoEncontrado` → `DeviceNotFound` |
| `Identidad.CodigoEmparejamientoInvalido` | `Identity.InvalidPairingCode` | `CodigoEmparejamientoInvalido` → `InvalidPairingCode` |
| `Identidad.SupervisorSinPermiso` | `Identity.SupervisorLacksPermission` | `SupervisorSinPermiso` → `SupervisorLacksPermission` |
| `Identidad.AutorizacionInvalida` | `Identity.InvalidApproval` | `AutorizacionInvalida` → `InvalidApproval` |
| `Identidad.PermisoDesconocido` | `Identity.UnknownPermission` | `PermisoDesconocido` → `UnknownPermission` (declared, not used) |
| `Identidad.SoloAdministrador` | `Identity.AdministratorOnly` | `SoloAdministrador` → `AdministratorOnly` |
| `Identidad.NoAutoRevocar` | `Identity.CannotRevokeOwnDevice` | inline in `RevokeDevice` |
| `Identidad.RolProtegido` | `Identity.ProtectedRole` | inline in `SaveRole` |
| `Identidad.NoAutoBloqueo` | `Identity.CannotLockSelf` | inline in `UpdateUser` |
| `Licencia.LimiteCajas` | `Identity.RegisterLimitReached` | inline in `PairDevice` |

`Licencia.LimiteCajas` was raised by Identity (pairing), so it takes the `Identity` prefix per the phase A convention. It is checked by `src/Client/Toki.Client.UI/Paginas/Configurar.razor` and `tests/Toki.IntegrationTests/Configuracion/LicenciaTests.cs`.

Validation errors still use the cross-cutting `Validation` code; their messages are unchanged (Spanish).

## Audit actions

Resource types are English too.

| Old action | New action | Old resource type → new | Data (old → new) |
|---|---|---|---|
| `identidad.instalar` | `identity.setup` | `Usuario` → `User` | `{ Nombre, Codigo, Dispositivo }` → `{ Name, Code, Device }` |
| `identidad.pin-fallido` (login) | `identity.pin-failed` | `Usuario` → `User` | `{ Codigo, Dispositivo, Resultado }` → `{ Code, Device, Result }` |
| `identidad.pin-fallido` (supervisor approval) | `identity.pin-failed` | `Usuario` → `User` | `{ Codigo, Permiso, Resultado }` → `{ Code, Permission, Result }` |
| `identidad.emparejar` | `identity.pair` | `Dispositivo` → `Device` | `{ Nombre }` → `{ Name }` |
| `identidad.revocar` | `identity.revoke` | `Dispositivo` → `Device` | `{ Nombre }` → `{ Name }` |
| `identidad.guardar-rol` | `identity.save-role` | `Rol` → `Role` | `{ Nombre, Permisos }` → `{ Name, Permissions }` |
| `identidad.crear-usuario` | `identity.create-user` | `Usuario` → `User` | `{ Nombre, Codigo, RolId }` → `{ Name, Code, RoleId }` |
| `identidad.actualizar-usuario` | `identity.update-user` | `Usuario` → `User` | `{ Nombre, RolId, Activo, CambioPin }` → `{ Name, RoleId, IsActive, PinChanged }` |
| `identidad.cambiar-pin` | `identity.change-pin` | `Usuario` → `User` | `{ }` |

`Result` in the audit data is a `PinResult` value (see enums).

## Permissions

Only the phase A constants are used (`Permissions.UsersManage`, `Permissions.DevicesManage`, `Permissions.All`, …). No new permission strings. The seeded roles now store the English strings (see database).

## Enums

| Old | New |
|---|---|
| `ResultadoPin` | `PinResult` |
| `ResultadoPin.Correcto` | `PinResult.Correct` |
| `ResultadoPin.Incorrecto` | `PinResult.Incorrect` |
| `ResultadoPin.Bloqueado` | `PinResult.Locked` |
| `ResultadoPin.Inactivo` | `PinResult.Inactive` |

## Config keys

None in this module.

## Database (schema `identidad` → `identity`)

| Old table | New table |
|---|---|
| `identidad.Usuarios` | `identity.Users` |
| `identidad.Roles` | `identity.Roles` |
| `identidad.Dispositivos` | `identity.Devices` |
| `identidad.CodigosEmparejamiento` | `identity.PairingCodes` |
| `identidad.Sesiones` | `identity.Sessions` |
| `identidad.AutorizacionesSupervisor` | `identity.SupervisorApprovals` |

| Table | Old column | New column |
|---|---|---|
| Users | `Id` | `Id` |
| Users | `Nombre` | `Name` |
| Users | `Codigo` | `Code` |
| Users | `PinHash` | `PinHash` |
| Users | `RolId` | `RoleId` |
| Users | `Activo` | `IsActive` |
| Users | `IntentosFallidos` | `FailedAttempts` (concurrency token) |
| Users | `BloqueadoHastaUtc` | `LockedUntilUtc` |
| Roles | `Id` | `Id` |
| Roles | `Nombre` | `Name` |
| Roles | `Permisos` | `Permissions` |
| Devices | `Id` | `Id` |
| Devices | `Nombre` | `Name` |
| Devices | `TokenHash` | `TokenHash` |
| Devices | `Revocado` | `IsRevoked` |
| Devices | `CreadoUtc` | `CreatedUtc` |
| PairingCodes | `Id` | `Id` |
| PairingCodes | `NombreDispositivo` | `DeviceName` |
| PairingCodes | `CodigoHash` | `CodeHash` |
| PairingCodes | `ExpiraUtc` | `ExpiresUtc` |
| PairingCodes | `UsadoUtc` | `UsedUtc` |
| Sessions | `Id` | `Id` |
| Sessions | `UsuarioId` | `UserId` |
| Sessions | `DispositivoId` | `DeviceId` |
| Sessions | `TokenHash` | `TokenHash` |
| Sessions | `CreadaUtc` | `CreatedUtc` |
| Sessions | `ExpiraUtc` | `ExpiresUtc` |
| Sessions | `CerradaUtc` | `ClosedUtc` |
| SupervisorApprovals | `Id` | `Id` |
| SupervisorApprovals | `SupervisorId` | `SupervisorId` |
| SupervisorApprovals | `SolicitanteId` | `RequesterId` |
| SupervisorApprovals | `DispositivoId` | `DeviceId` |
| SupervisorApprovals | `Permiso` | `Permission` |
| SupervisorApprovals | `RecursoId` | `ResourceId` |
| SupervisorApprovals | `ExpiraUtc` | `ExpiresUtc` |
| SupervisorApprovals | `UsadaUtc` | `UsedUtc` |

Indexes: `IX_Usuarios_Codigo` → `IX_Users_Code` (unique), `IX_Dispositivos_TokenHash` → `IX_Devices_TokenHash` (unique), `IX_CodigosEmparejamiento_CodigoHash` → `IX_PairingCodes_CodeHash`, `IX_Sesiones_TokenHash` → `IX_Sessions_TokenHash` (unique). Primary keys follow the table names (`PK_Users`, …).

Seed data (`Roles`): same ids and the same Spanish names. The `Permissions` column now holds the English permission strings.

| Id | Name | Constant (old → new) |
|---|---|---|
| `0199e000-0000-7000-8000-000000000001` | `Administrador` | `Rol.AdministradorId` → `Role.AdministratorId` |
| `0199e000-0000-7000-8000-000000000002` | `Supervisor` | `Rol.SupervisorId` → `Role.SupervisorId` |
| `0199e000-0000-7000-8000-000000000003` | `Cajero` | `Rol.CajeroId` → `Role.CashierId` |

Migration command used (from the repo root):

```
dotnet tool restore
dotnet dotnet-ef migrations add InitialIdentity --project src/Modules/Identity/Toki.Identity --startup-project src/Modules/Identity/Toki.Identity --context IdentityDbContext --output-dir Persistence/Migrations --no-build
```

## Internal types and members (visible to `Toki.UnitTests` and `Toki.IntegrationTests`)

### Persistence (`Toki.Identity.Persistence`)

| Old | New |
|---|---|
| `IdentidadDbContext` | `IdentityDbContext` |
| `IdentidadDbContext.Usuarios` | `IdentityDbContext.Users` |
| `IdentidadDbContext.Roles` | (unchanged) |
| `IdentidadDbContext.Dispositivos` | `IdentityDbContext.Devices` |
| `IdentidadDbContext.CodigosEmparejamiento` | `IdentityDbContext.PairingCodes` |
| `IdentidadDbContext.Sesiones` | `IdentityDbContext.Sessions` |
| `IdentidadDbContext.Autorizaciones` | `IdentityDbContext.Approvals` |
| `IdentidadDbContext.EsAdministradorAsync(usuarioId, ct)` | `IdentityDbContext.IsAdministratorAsync(userId, ct)` |
| `IdentidadConfiguration` | `IdentityConfiguration` |
| `DisenoIdentidad` | `IdentityDesignTimeFactory` |

### Domain (`Toki.Identity.Domain`)

| Old | New |
|---|---|
| `Rol` | `Role` |
| `Rol.Nombre` / `Permisos` | `Role.Name` / `Permissions` |
| `Rol.Predeterminados()` | `Role.Defaults()` |
| `Usuario` | `User` |
| `Usuario.IntentosAntesDeBloquear` | `User.AttemptsBeforeLockout` |
| `Usuario.Nombre` | `User.Name` |
| `Usuario.Codigo` | `User.Code` |
| `Usuario.NormalizarCodigo(codigo)` | `User.NormalizeCode(code)` |
| `Usuario.PinHash` | (unchanged) |
| `Usuario.RolId` | `User.RoleId` |
| `Usuario.Activo` | `User.IsActive` |
| `Usuario.IntentosFallidos` | `User.FailedAttempts` |
| `Usuario.BloqueadoHastaUtc` | `User.LockedUntilUtc` |
| `Usuario.CambiarPin(pin)` | `User.ChangePin(pin)` |
| `Usuario.IntentarPin(pin, ahoraUtc)` | `User.TryPin(pin, nowUtc)` |
| `Dispositivo` | `Device` |
| `Dispositivo.Nombre` / `TokenHash` / `Revocado` / `CreadoUtc` | `Device.Name` / `TokenHash` / `IsRevoked` / `CreatedUtc` |
| `CodigoEmparejamiento` | `PairingCode` |
| `CodigoEmparejamiento.NombreDispositivo` / `CodigoHash` / `ExpiraUtc` / `UsadoUtc` | `PairingCode.DeviceName` / `CodeHash` / `ExpiresUtc` / `UsedUtc` |
| `Sesion` | `Session` |
| `Sesion.Duracion` | `Session.Duration` |
| `Sesion.UsuarioId` / `DispositivoId` / `TokenHash` / `CreadaUtc` / `ExpiraUtc` / `CerradaUtc` | `Session.UserId` / `DeviceId` / `TokenHash` / `CreatedUtc` / `ExpiresUtc` / `ClosedUtc` |
| `AutorizacionSupervisor` | `SupervisorApproval` |
| `AutorizacionSupervisor.Duracion` | `SupervisorApproval.Duration` |
| `AutorizacionSupervisor.SupervisorId` / `SolicitanteId` / `DispositivoId` / `Permiso` / `RecursoId` / `ExpiraUtc` / `UsadaUtc` | `SupervisorApproval.SupervisorId` / `RequesterId` / `DeviceId` / `Permission` / `ResourceId` / `ExpiresUtc` / `UsedUtc` |
| `Pin.EsValido(pin)` | `Pin.IsValid(pin)` |
| `Pin.Hash(pin)` | (unchanged) |
| `Pin.Verificar(hash, pin)` | `Pin.Verify(hash, pin)` |
| `Token.Nuevo()` | `Token.New()` |
| `Token.CodigoCorto()` | `Token.ShortCode()` |
| `Token.Hash(token)` | (unchanged) |
| `IdentidadErrors` | `IdentityErrors` (members in the error code table) |
| `ResultadoPin` | `PinResult` (values in the enum table) |

### Module (`Toki.Identity`)

| Old | New |
|---|---|
| `SesionAuthenticationHandler` | `SessionAuthenticationHandler` |
| `AutorizacionesSupervisor` (implements `ISupervisorApprovals`) | `SupervisorApprovals` |
| auth failure message `"Sesión inválida o vencida."` | `"Invalid or expired session."` |

### Features (`Toki.Identity.Features`)

| Old | New |
|---|---|
| `Instalar` | `Setup` |
| `IniciarSesion` | `OpenSession` |
| `CerrarSesion` | `CloseSession` |
| `Yo` | `Me` |
| `CambiarMiPin` | `ChangeMyPin` |
| `AutorizarSupervisor` | `GrantSupervisorApproval` |
| `CrearCodigoEmparejamiento` | `CreatePairingCode` |
| `EmparejarDispositivo` | `PairDevice` |
| `ListarDispositivos` | `ListDevices` |
| `RevocarDispositivo` | `RevokeDevice` |
| `ListarRoles` | `ListRoles` |
| `GuardarRol` | `SaveRole` |
| `CrearUsuario` | `CreateUser` |
| `ActualizarUsuario` | `UpdateUser` |
| `ListarUsuarios` | `ListUsers` |
| `ListarCajeros` | `ListCashiers` |

## Known references to update outside the module

- `src/Hosts/Toki.Host.Local/Program.cs`: `IdentidadModule`, `AddIdentidadModule`.
- `tests/Toki.UnitTests/Identidad/UsuarioTests.cs`: `Usuario`, `CambiarPin`, `IntentarPin`, `ResultadoPin.*`, `IntentosFallidos`, `BloqueadoHastaUtc`, `Activo`, `Codigo`, `Pin.EsValido`.
- `tests/Toki.IntegrationTests/Identidad/SesionesTests.cs`: `IdentidadDbContext().Usuarios`, `IntentosFallidos`, routes, error codes.
- `tests/Toki.IntegrationTests/**`: every `/api/identidad/...` route, `X-Dispositivo` header, JSON fields (`token`, `codigo`, `nombreDispositivo`, …) and `Identidad.*` / `Licencia.LimiteCajas` codes.
- `src/Client/**`: routes, header, JSON fields, `Licencia.LimiteCajas` in `Paginas/Configurar.razor`.
