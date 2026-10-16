# R11 · Revisión de seguridad: cajero, nube, equipos y red (16/10/2026)

Tres revisiones en paralelo, solo lectura, sobre main en `35c1da4`/`07738ee`. Correcciones en TK-253. Lo ya corregido en R7 a R10 no se repite.

## A · Lo que un cajero o supervisor puede ver o hacer (tienda)

Altos:
1. **La autorización de supervisor no está atada al monto ni a las líneas** (`Approval.cs:114-125`, `IdentityModule.cs:387-398`; retiros `Shifts.cs:179-184`, devoluciones `VoidAndReturn.cs:95-100`, fondo `Shifts.cs:107-111`, precio/fiado/cierre `RegisterSale.cs:156,222-241`, `Shifts.cs:324-326`). Se pide "Salida de RD$100" y se usa la misma autorización con RD$20,000. Arreglo: hash del contenido (monto, tipo, líneas, total) guardado en la autorización, mostrado al supervisor desde el servidor y comparado al consumirla; atada también al usuario que pide.
2. **El cierre a ciegas se puede sondear** (`Shifts.cs:318`): un id de autorización inventado evita que cuente el fallo; además el esperado se deduce de `/shifts/current` y `/api/sales`. Arreglo: contar cada conteo distinto sin `approve-close` aunque traiga id; mejor cerrar siempre y marcar "pendiente de aprobación"; ocultar pagos y devueltas al cajero hasta cerrar.
3. **`backups.manage` lleva al PIN del dueño** (`Backups.cs:263-289,439-505`): cambia la frase sin la anterior, copia a un recurso compartido propio, descifra con `decrypt-backup` y rompe los PIN de 4 dígitos. Arreglo: pedir la frase actual o Administrador, carpetas permitidas, auditoría con antes y después, sacar o salar con secreto del servidor los `PinHash` del respaldo.

Medios: 4) PIN ajeno por `me/pin` sin bloqueo y sesiones de 12 h sin bloqueo por inactividad (`Sessions.cs:135-148`); 5) quitar líneas y anular la venta en curso solo se controla en la pantalla (`Sale.razor:372-396`); 6) fiado y saldo a favor de cualquier cliente con solo `sales.create` (`RegisterSale.cs:64,152-154,183-188`); 7) el cajero declara su propio fondo (`Shifts.cs:107-141`); 8) E31 a cualquier RNC escrito a mano (`RegisterSale.cs:60-62,102`); 9) el Cajero sembrado tiene `inventory.view` y `customers.manage` (`Identity.cs:44,49`); 10) la cadena de auditoría sin llave se puede recalcular con acceso a la base (`OutboxAndAudit.cs:76-81`); 11) Supervisor sembrado con compras + pagos + retiros (`Identity.cs:34-39`); 12) costos visibles sin `costs.view` en compras, 606 y auditoría.

Bajos: 13) lecturas sin dueño (ventas, carritos en espera, pedidos, comprobantes B); 14) cualquier cajero bloquea al dueño con PIN malos y el error revela el PIN correcto; 15) `users.manage` quita permisos que no tiene y restablece PIN para entrar como otro; 16) pagos con tarjeta o transferencia sin referencia; 17) llave del equipo copiable; 18) reloj atrasado solo avisa; 19) gastos y pagos con fecha atrasada sin límite.

## B · Nube

Altos:
1. **Pre-secuestro de cuentas**: se puede entrar sin confirmar el correo (`Program.cs:45-50`, `Members.cs:166-184`). Arreglo: `RequireConfirmedEmail`; al confirmar o restablecer por primera vez, rotar el sello y quitar miembros e invitaciones previos.
2. **Cualquier certificado de cédula pasa por el de una empresa** (`FeSigning.cs:256-257`, usado en `FeReceiver.cs:114,159-160`, `FeRemoteSigning.cs:180-182`, `FeEndpoints.cs:155`): facturas falsas de cualquier empresa, aprobaciones falsas, ocupar el RNC del receptor. Arreglo: no aceptarlo hasta poder verificar la delegación; marcar "firmante sin verificar" y dejarlo fuera de compras y del 606; activación con cédula para RNC de empresa solo con soporte.

Medios: 1) SSRF y lentitud al armar la cadena de certificados (descargas AIA/CRL) (`FeSigning.cs:236-251`); 2) correos ilimitados por `/api/auth/manage/info`; 3) un nodo comprometido llena la base (ingesta sin límite por nodo ni tamaño por evento); 4) dentro de un negocio, la PC de una tienda pisa datos de otra y apaga el receptor; 5) token de refresco en localStorage y panel sin CSP, `frame-ancestors`, HSTS; 6) administrador de plataforma sin MFA y el rol vive en el token 1 h; 7) cualquiera agota el cupo por RNC del receptor con semillas anónimas.

Bajos: 1) códigos y correos en la telemetría por la URL; 2) datos de clientes en logs de errores del proyector; 3) secreto compartido de nodos aún aceptado (TK-251 ya lo quitó) y nonces en memoria; 4) `/health/ready` anónimo despierta la base; 5) registro con pocos límites y códigos de conexión adivinables; 6) bloqueo de cuentas por terceros; 7) permisos de Azure más amplios de lo necesario; 8) la licencia del lector de IA confía en la tienda.

## C · Equipos, red y datos en reposo

Altos:
1. **El instalador hace sysadmin de SQL al usuario diario** (`toki.iss:176-178`) y de ahí a SYSTEM; reutiliza cualquier `SQLEXPRESS`. Arreglo: `/SQLSYSADMINACCOUNTS="BUILTIN\Administrators"`, instancia propia `TOKI` o revisar la existente.
2. **PC principal robada**: sin cifrado de disco se sacan la llave del e-CF, la CA, la llave del nodo y la base. Arreglo: el instalador avisa y la salud marca degradado sin BitLocker/Device Encryption; llave del e-CF en TPM donde se pueda; guía "PC robada".
3. **Actualizaciones sin firma de código** (`appsettings.json:22`, `toki.iss:43`, `ServerUpdates.cs:182`, `AppUpdates.cs:184-191`, `build.ps1:93`). Arreglo: firmar instalador y paquetes, huellas de firmante obligatorias o manifiesto firmado con llave fuera de línea, exigir https.

Medios: 4) instalación inicial desde la red (TK-251 ya la limitó a la PC principal); 5) llave del equipo de caja exportable; 6) app de caja instalada por usuario y modificable; 7) el servicio conserva SeImpersonate; 8) un administrador saca la llave del e-CF (TK-160); 9) "Borrar datos" deja la llave del e-CF.

Bajos: 10) PII en logs 60 días; 11) http aceptado (TK-251 corrigió las URL); 12) códigos de emparejamiento y bloqueos sin límite; 13) borrado recursivo y sin control de versión en scripts; 14) `CopyTo` acepta UNC; 15) frase de respaldo en variable de entorno.

## Estado de las correcciones

TK-253.

### Parte C (equipos, red y datos en reposo)

| # | Estado | Qué se hizo |
|---|---|---|
| C1 | ✅ | Instancia propia `.\TOKI` (`appsettings.json`, `toki.iss`): `/SQLSYSADMINACCOUNTS` = grupo Administradores por SID (nombre traducido), sin TCP ni canalizaciones; nunca `/ADDCURRENTUSERASSQLADMIN`; un `SQLEXPRESS` ajeno no se reutiliza. Desarrollo sigue con `.\SQLEXPRESS` (perfil `SqlExpress`). |
| C2 | ✅ | Instalador avisa sin BitLocker/Cifrado de dispositivo; salud `disk-encryption` (Degraded) con el `ProtectionStatus` que escribe cada hora la tarea de SYSTEM (`Win32_EncryptableVolume` necesita administrador; el servicio no lo es). Llave e-CF en el TPM cuando se puede. `docs/guia-pc-robada.md`. |
| C3 | ✅ | Authenticode: `build.ps1` firma `Toki.exe`, `Toki.Host.Local.exe`, el setup y el desinstalador (`TOKI_SIGNTOOL`, `-RequireSigned`) y escribe `Updates:SignerThumbprints` (`TOKI_SIGN_THUMBPRINT`). La tarea no instala nada sin huella salvo `Updates:AllowUnsignedUpdates` (piloto), exige feed https. Velopack quitado (ver C6). Manejo del certificado para el dueño: `docs/instalador-web.md`, "Firma de código". |
| C5 | ✅ | Llave de la caja CNG no exportable del usuario (TPM si hay, `CngDeviceKeys`); `client.dat` guarda solo la referencia. Certificado de 1 año, renovado por mTLS en los últimos 30 días (`/api/identity/devices/current/certificate`); el anterior vale hasta vencer (`PreviousCertificateThumbprint`). |
| C6 | ✅ | App de caja en `Program Files\Toki\app` para toda la PC (WebView2 guarda en el perfil); la tarea `Toki\Actualizar` (SYSTEM) actualiza las dos partes, la caja solo con la app cerrada. |
| C7 | ✅ | `sc.exe privs Toki SeChangeNotifyPrivilege` en `install.ps1`. |
| C8 | ✅ | La llave e-CF se importa como llave CNG no exportable de `NT SERVICE\Toki` (TPM o software), ACL solo esa cuenta y SYSTEM. No es `LocalMachine`: crear llaves CNG de máquina exige administrador. Falta probarlo en VM limpia con el servicio (TK-160). |
| C9 | ✅ | `remove-data.ps1` borra el login de SQL del servicio, el perfil de `NT SERVICE\Toki` (llave e-CF y su `CurrentUser\My`), el certificado e-CF de `LocalMachine\My` por la huella de `ecf-certificate.json` y el viejo `CN=Toki Nodo`. |
| C10 | ✅ | El registro de peticiones enmascara segmentos de 9 u 11 dígitos (`RequestLogs`). |
| C12 | ✅ | Un código de emparejamiento a la vez; 5 códigos malos lo cierran; 10 por dirección esperan 15 min; 10 PIN malos por caja (inicio y autorización) esperan 15 min (`AttemptLimiter`, en memoria). |
| C13 | ✅ | `remove-tree.ps1`: no borra si hay un enlace (*reparse point*); la tarea rechaza una versión igual o anterior a la instalada. |
| C14 | ✅ | Desde la pantalla la copia va a un disco local; una carpeta de red solo si el técnico la lista en `Backup:CopyTo` o `Backup:AllowedCopyFolders`. (Si A3 agrega su propia lista, usar `BackupSettings.CopyAllowed`.) |
| C15 | ✅ | `decrypt-backup` pide la frase escrita; la variable solo con entrada redirigida, y avisa cómo borrarla. |

C4 y C11 ya estaban corregidos (TK-251). Sin probar en una PC real: ISCC (no instalado), la instancia `TOKI` con el SSEI de Express 2025, el TPM con el servicio real y la firma.

### Parte B (nube)

| # | Estado | Qué se hizo |
|---|---|---|
| Alto 1 | ✅ | `SignIn.RequireConfirmedEmail` (desarrollo puede apagarlo con `Auth:RequireConfirmedEmail`). La primera confirmación (GET de Identity o el nuevo `POST /api/auth/confirm`, y la invitación que llegó por correo) quita los otros miembros y las invitaciones del negocio que la cuenta creó, rota el sello (ningún token anterior se renueva) y el panel avisa "si usted no creó esta cuenta, cambie la contraseña" (`CloudUserManager`). |
| Alto 2 | ✅ | La cédula ya no pasa por el RNC de una empresa (`FeTrust.BelongsTo` exacto). e-CF firmados así: se acusan pero quedan "firmante sin verificar" (`SignerUnverified`), fuera del pull de la tienda (compras y 606) y sin decisión en el panel. ACECF de ese firmante: 400. Activar el receptor con cédula para RNC de empresa crea una solicitud que soporte aprueba o rechaza en la consola (`/api/platform/fe/delegations`, auditado, pestaña "Firmantes"); aprobada, la tienda activa de nuevo y su firma vale por la huella de ese certificado. |
| Medio 1 | ✅ | Cadena sin descargas (`DisableCertificateDownloads`); primero llega a una raíz configurada sin revocación; luego CRL/OCSP solo si todas las direcciones están en hosts de las propias CA (sus extensiones CDP/AIA) o en `Fe:RevocationHosts`. |
| Medio 2 | ✅ | `/api/auth/manage/*` por usuario (`Auth:ManagePerMinutePerUser`, 10); correos de Identity a una dirección: 3 por hora (`IdentityEmailCap`, en memoria). |
| Medio 3 | ✅ | Ingesta: 60 lotes/min por PC, 64 KB por evento, tipos solo `Toki.*`, desconocidos pendientes ≤ 1000 por negocio, ≤ 100.000 eventos/día por negocio (429 y aviso "Ingest cap reached" en la alerta de Azure); archivo 120/min por PC; ACECF repetido no se guarda dos veces. |
| Medio 4 | ✅ | Proyectores: una fila de otra tienda nunca se reemplaza ni se anula su venta; `CommercialApprovalRecordedV1` solo de la PC que activó el receptor y con ACECF de ese e-NCF firmado por su certificado; activar/retirar el receptor solo desde esa PC (o el dueño en el panel); el pull de recibidos solo a esa PC. |
| Medio 5 | ✅ | `staticwebapp.config.json` con CSP (scripts solo del sitio y Paddle.js; connect-src la API y Paddle; `frame-ancestors 'none'`), HSTS y `X-Frame-Options: DENY`. Tokens: acceso 30 min, refresco 7 días. Se queda en localStorage (cookie HttpOnly sería de terceros: panel y API en sitios distintos); la CSP es la defensa. |
| Medio 6 | ✅ | Consola de plataforma: rol leído de la base en cada llamada, cuenta con 2FA y token de un inicio con código (`amr=mfa`); el token renovado no lo trae, así la consola dura 30 min. Pantalla "Seguridad" (activar 2FA) y código en Entrar. |
| Medio 7 | ✅ | Semilla y validacioncertificado con su propio cupo por RNC (`Fe:SeedRequestsPerMinutePerRnc`, 60). |
| Bajo 1 | ✅ | Sin código en la URL: confirmación por `POST /api/auth/confirm`, búsqueda de clientes por `POST /api/platform/tenants/search`. (La telemetría es el agente sin código de App Service: no admite procesador; por eso se quitó lo sensible de las URL.) |
| Bajo 2 | ✅ | El inbox guarda y registra solo el tipo de excepción (o la ruta JSON), nunca su texto. |
| Bajo 3 | ✅ | Nonces de nodo en la base (`NodeNonces`, clave primaria). |
| Bajo 4 | ✅ | `/health/ready` solo con token de administrador de plataforma. |
| Bajo 5 | ✅ | Registro: misma respuesta si el correo ya existe; tope por destinatario (Medio 2). Códigos de conexión: 5 vivos por negocio; 200 códigos malos en 10 min (de cualquier IP) cierran el registro de nodos hasta que pase la ventana. Sin CAPTCHA. |
| Bajo 6 | ✅ | Contraseña o código 2FA mal: 5 por IP y cuenta en 15 min (`LoginThrottle`); el bloqueo por cuenta de Identity queda en 50 como respaldo. |
| Bajo 7 | ✅ | Bicep: roles de certificados y llaves del Key Vault solo con `allowCloudCustody`; Crypto User solo sobre la llave de licencias; Secrets User solo con Paddle; OpenAI con `disableLocalAuth` e identidad administrada (la API usa token de Entra sin `ApiKey`); FTP y SCM sin usuario y contraseña. |
| Bajo 8 | ✅ | El lector de facturas con IA decide en la nube: suscripción pagada (TK-249), licencia firmada que manda el latido (`NodeLicenseV1.Jws`, verificada con la llave de Toki) o prueba de 14 días desde la primera venta en la réplica. Pendiente: la licencia no está atada al negocio (`shortcut`). |

Al desplegar: las cuentas sin correo confirmado ya no entran hasta confirmarlo (el panel ofrece reenviar); el personal de Toki debe activar 2FA en Seguridad antes de usar la consola.
