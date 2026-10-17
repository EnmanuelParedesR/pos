# Instalador desde la web (TK-196, plan) 🟡

Plan corto de lo que falta para que una persona baje Abastalia de la web y empiece la prueba de 14 días **sin técnico**. No está construido; hoy se instala con `deploy/install.ps1` sobre una publicación de `Abastalia.Host.Local`. La parte dentro de la app (prueba desde la primera venta, avisos, Restringido, "Comprar licencia", asistente de un equipo y datos de ejemplo) sí está hecha.

## Qué tiene que hacer el instalador

1. **Comprobar el equipo:** Windows 10/11 de 64 bits, espacio libre (≥ 5 GB con SQL Server), .NET no hace falta (publicación *self-contained*). Pedir permisos de administrador una sola vez.
2. **Base de datos:** instalar **SQL Server Express 2025** en silencio si no hay una instancia `ABASTALIA` (`/Q /IACCEPTSQLSERVERLICENSETERMS /ACTION=Install /INSTANCENAME=ABASTALIA /TCPENABLED=0`), solo escuchando en la PC. **No LocalDB:** corre como el usuario que inició sesión y no como servicio, así que no sirve para el nodo que atiende a las cajas (`docs/arquitectura-v1.md`, decisión ya tomada). Si ya existe la instancia, reutilizarla.
3. **Servicio:** lo mismo que `deploy/install.ps1`: copiar a `C:\Program Files\Abastalia`, crear el servicio `Abastalia` con su cuenta virtual, `prepare-service` (certificado del nodo, `C:\ProgramData\Abastalia`, login de SQL), regla de firewall 5443 en perfil privado, arrancar.
4. **App de caja:** instalar la app WPF en la misma PC con acceso directo en el escritorio y abrirla al terminar. Abre en "Primera instalación" (el servidor está en esta PC) con la casilla de datos de ejemplo.
5. **Sin licencia:** no pide código. La prueba empieza sola con la primera venta; "Comprar licencia" está en Configuración → Licencia.
6. **Desinstalar:** detener y borrar el servicio y la app; **nunca** borrar la base de datos ni `C:\ProgramData\Abastalia` sin preguntar (ahí están las ventas y los respaldos).
7. **Mudarse a una PC nueva (R9 #16):** se instala como arriba y se restaura el respaldo con su frase. El respaldo trae **los datos, no las llaves** de la PC vieja: `C:\ProgramData\Abastalia\keys` está protegido con DPAPI de esa máquina y el certificado digital de e-CF está en su almacén de Windows, no exportable. En la PC nueva hay que: importar otra vez el certificado `.p12` (Configuración → Comprobantes), volver a escribir la frase de los respaldos, y volver a unir cada caja (la tienda crea una CA nueva; las cajas viejas aparecen en salud como "vuelva a unir"). Los números e-NCF no se repiten: siguen desde lo que tenga el respaldo, así que se restaura **el respaldo más nuevo**, y si la PC vieja firmó después, se cargan los rangos desde el siguiente número libre como en TK-221. Si el respaldo está en la nube (TK-238): el Dueño lo descarga en el panel (Tiendas → "Respaldos en la nube" → Descargar), lo copia a `C:\ProgramData\Abastalia\backups` de la PC nueva y lo restaura desde Configuración → Respaldos con su frase.

## Empaquetado: qué usar

| Opción | A favor | En contra | Uso |
|---|---|---|---|
| **Inno Setup** (gratis) | Un `.exe` firmado que hace todo lo de arriba, incluido SQL Express y el servicio; se descarga de la web | Script propio que mantener | **Elegida para V1** |
| **Velopack** | Actualizaciones delta de la app de caja (`docs/arquitectura-v1.md` §6.3) | Instala por usuario, en una carpeta que el cajero puede cambiar (R11 C6); no instala servicios ni SQL Server | **Quitado** (R11): las dos partes se actualizan con el mismo `.exe` firmado |
| **MSIX** | Instalación limpia de la tienda de Windows | No puede instalar un servicio de Windows clásico ni SQL Express | Descartada para el nodo |
| **winget** | `winget install Abastalia.Abastalia` para técnicos | Solo distribuye: necesita el `.exe` de arriba igual | Más adelante, apuntando al mismo `.exe` |

## Construido (TK-242)

- **`deploy/installer/abastalia.iss`** (Inno Setup 6): un `AbastaliaSetup-<versión>.exe` con dos tipos en el asistente, **PC principal** (servidor + caja) y **Caja**. Silencioso: `/VERYSILENT /TYPE=server` o `/TYPE=register`. La PC principal usa `deploy/install.ps1` (servicio con solo `SeChangeNotifyPrivilege`, R11 C7; `prepare-service`; firewall 5443 privado) e instala SQL Server Express en **su propia instancia `.\ABASTALIA`** si falta (R11 C1): sin TCP ni canalizaciones con nombre, y administradores de SQL = el grupo Administradores de la PC (buscado por su SID, `BUILTIN\Administradores` en Windows en español), nunca el usuario que instala. Un `SQLEXPRESS` que ya tenga la PC no se reutiliza (sus administradores y su red son de otro). En desarrollo se sigue usando `.\SQLEXPRESS` (perfil `SqlExpress` de `launchSettings.json`). Ambos tipos instalan WebView2 Evergreen si falta. SQL Express y WebView2 se **descargan de Microsoft al instalar**. Apps publicadas *self-contained*: no instala .NET.
- **Disco cifrado (R11 C2):** en la PC principal, si el disco del sistema no tiene BitLocker o Cifrado de dispositivo, el instalador avisa (se puede seguir) y la salud marca "Cifrado del disco" en amarillo (`disk-encryption.json`, que escribe la tarea de SYSTEM cada hora). Qué hacer si roban la PC: `docs/guia-pc-robada.md`.
- **Carpeta `keys`:** `prepare-service` le quita la herencia de `C:\ProgramData` y la deja solo para el servicio, SYSTEM y Administradores. La llave del certificado e-CF no va ahí: es una llave no exportable de `NT SERVICE\Abastalia`, en el TPM cuando la PC lo tiene, con ACL solo para esa cuenta y SYSTEM (R11 C8).
- **App de caja (R11 C6):** va en `C:\Program Files\Abastalia\app` para toda la PC (un cajero no la puede cambiar), con acceso directo en Inicio y en el escritorio. Su llave de caja es no exportable, en el TPM cuando se puede (R11 C5), y su certificado dura un año: se renueva solo por mTLS en los últimos 30 días.
- **Actualizaciones (las dos partes):** la tarea `Abastalia\Actualizar` (SYSTEM, cada hora, `update-service.ps1 -Role server|register`). En la PC principal, `ServerUpdateCheck` lee `releases.<canal>.json` cada 30 min, anota "Hay una actualización" y escribe `C:\ProgramData\Abastalia\update.json`; la tarea instala **solo si no hay turnos abiertos o fuera del horario** (`Updates:OpensAt`/`ClosesAt`, por defecto 07:00–22:00). En una caja, la tarea lee el feed ella misma e instala **solo con la app cerrada**. En ambas: feed `https`, versión `x.y.z` **más nueva** que la instalada (R11 C13), descarga a una carpeta solo de SYSTEM (un enlace plantado ahí la detiene) y **firma Authenticode de Abastalia** (`Updates:SignerThumbprints`, R11 C3). Sin huella configurada no se instala nada, salvo `Updates:AllowUnsignedUpdates: true` (solo piloto, antes de tener el certificado: entonces vale el SHA-256 del feed, que no protege si alguien controla el feed).
- **Canales (lanzamiento por etapas):** `pilot` y `stable` (`build.ps1 -Channel`, `Updates:Channel`).
- **Desinstalar:** quita el servicio, la regla de firewall, la tarea y la app. Pregunta "¿Borrar también los datos de la tienda?" (No por defecto); solo con Sí corre `remove-data.ps1`: base de datos Abastalia y el login de SQL del servicio, `C:\ProgramData\Abastalia`, el perfil de `NT SERVICE\Abastalia` (con la llave del e-CF), el certificado e-CF si alguna versión lo puso en `LocalMachine\My` y el certificado viejo `CN=Abastalia Nodo` (R11 C9). Se niega a borrar si encuentra un enlace (*reparse point*) en esas carpetas (R11 C13).
- **Construir:** `deploy/installer/build.ps1 -Version 1.0.0 -Channel pilot -FeedUrl <url> -RequireSigned` publica las dos apps, las firma, compila con `ISCC` y deja el feed en `artifacts/releases` (`releases.<canal>.json`, el setup y su `.sha256`), que se sube a un GitHub Release o a un contenedor de Azure Blob. Sin Inno Setup publica lo que puede y dice qué falta.

### Firma de código (R11 C3): qué hace el dueño con el certificado

1. **Comprar** un certificado de firma de código (Authenticode) a nombre de la empresa. Hoy las CA lo entregan en un token USB o en un HSM en la nube (la llave no sale de ahí): mejor así.
2. **Dónde vive:** solo en la PC que arma las versiones (o en el HSM). Nunca en el repositorio, en un correo, en una PC de tienda ni en el feed. El PIN del token lo sabe solo quien publica; una copia de la frase de recuperación va en sobre cerrado con los papeles de la empresa.
3. **Al armar:** `ABASTALIA_SIGNTOOL` = la línea de `signtool` sin el archivo (por ejemplo `signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /sha1 <HUELLA>`) y `ABASTALIA_SIGN_THUMBPRINT` = la huella SHA-1 del certificado. `build.ps1 -RequireSigned` no arma nada sin las dos. La huella queda en el `appsettings.json` de las dos apps: las PCs instaladas desde entonces solo aceptan setups firmados con ese certificado.
4. **Renovar** (cada 1–3 años): con el certificado nuevo, poner las **dos** huellas en `ABASTALIA_SIGN_THUMBPRINT` (separadas por coma) durante una versión, para que las tiendas reciban la huella nueva firmada con la vieja; después, solo la nueva.
5. **Si se pierde o se filtra:** revocarlo con la CA el mismo día, comprar otro y publicar una versión firmada con el nuevo. Las tiendas que solo conocen la huella vieja no aceptan la nueva: el técnico reinstala con el `.exe` nuevo (descargado de la página y comprobada su firma) o agrega la huella en `Updates:SignerThumbprints`.

### Respaldos fuera de la PC (R11 C14, C15)

- Desde la pantalla (permiso `backups.manage`) la copia solo va a un disco de esta PC (otro disco o una memoria USB). Una carpeta de red la agrega el técnico en `appsettings.json` del servidor: `Backup:CopyTo` o `Backup:AllowedCopyFolders` (lista).
- `Abastalia.Host.Local decrypt-backup <archivo> <salida>` pide la frase escrita. `ABASTALIA_BACKUP_PASSPHRASE` solo se lee cuando nadie puede escribir (entrada redirigida, un script); al terminar, borrarla: `Remove-Item Env:ABASTALIA_BACKUP_PASSPHRASE` (PowerShell) o `set ABASTALIA_BACKUP_PASSPHRASE=` (cmd), y cerrar esa consola.
- **Página de descarga:** `/descargar` en el sitio (con `site.installerUrl` vacío muestra "[URL DEL INSTALADOR]").

## Pendiente antes de construirlo

- Certificado de firma de código (Authenticode): para que SmartScreen no asuste en la primera descarga y para que las tiendas acepten actualizaciones (sección "Firma de código").
- ~~Parámetros de la instalación silenciosa de Express 2025~~: probados en una VM limpia (TK-160, `docs/prueba-vm-2026-10-17.md`): el SSEI solo descarga (`/ACTION=Download /MEDIATYPE=Core`, 750 MB) y se corre el `setup.exe` del paquete; unos 7 minutos en total.
- Página de descarga en la web (TK-197) con el enlace al `.exe` y la explicación de la prueba.
- Medir el embudo `instalada → primera venta` (`docs/metricas-producto.md`): el instalador no envía nada; la nube lo sabe cuando la tienda se conecta.
