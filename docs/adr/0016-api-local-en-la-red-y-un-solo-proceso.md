# ADR-0016: La API local de la tienda sigue en un servicio de Windows; qué expone a la red y qué falta cerrar

- Fecha: 2026-10-17
- Estado: aceptada (TK-359, issue #327). Pendiente de la decisión del dueño en los puntos marcados **Decide el dueño**
- Relacionadas: ADR-0003 (identidad y dispositivos), ADR-0009 (certificado del nodo y mTLS), `docs/licencias.md`, `deploy/install.ps1`, `deploy/installer/abastalia.iss`

## Contexto

El dueño preguntó si la API local (`Abastalia.Host.Local`, la que usan las cajas) es segura, y si conviene que todo corra dentro de la misma app de la PC principal en vez de un servicio de Windows aparte. Esta revisión miró el código del 17/10/2026: `Program.cs`, `DeviceCertificates.cs`, Identity (emparejar, sesiones, CA de la tienda), todos los endpoints sin `RequirePermission`, el instalador y los scripts de despliegue.

## Qué hay hoy

**Qué escucha en la red.** Un solo puerto: **TCP 5443, HTTPS**, en todas las interfaces (`https://0.0.0.0:5443`, `appsettings.Production.json`). Nada más: SQL Server Express se instala sin TCP ni named pipes (`/TCPENABLED=0 /NPENABLED=0`), la app de caja (WPF + WebView2) no abre ningún puerto, y `Abastalia.Client.Web` (solo desarrollo) escucha en `localhost`. La regla del firewall abre 5443 solo en redes **Privadas**; el instalador avisa si la red está como Pública.

**TLS y la CA de la tienda (ADR-0009).** La PC principal tiene su propia CA (ECDSA P-256, 30 años, llave cifrada con DPAPI de la máquina en la carpeta de llaves cerrada al servicio, SYSTEM y administradores). El certificado del servidor lo emite esa CA (1 año, se renueva solo). La caja fija la CA al emparejarse y desde ahí no habla con ningún otro servidor. TLS 1.2/1.3 de Kestrel por defecto.

**Cómo se autentica una caja.** Tres capas:

1. **Certificado de cliente (mTLS).** Al emparejarse con el código de 8 dígitos (10 minutos, uno a la vez), la caja manda un CSR y recibe un certificado de su llave (1 año, se renueva por mTLS). Kestrel corta el saludo TLS si el certificado no es de la CA; un middleware rechaza todo lo que llega sin certificado salvo `/health`, emparejar y la instalación inicial. La tabla `Devices` es la lista de revocación.
2. **Sesión.** Código de usuario + PIN (PBKDF2 con pimienta del servidor fuera de la base). 12 horas, atada al dispositivo. Límites: 10 PIN errados por caja y 4 por usuario por caja en 15 minutos, más el bloqueo progresivo de cada usuario. Quien puede autorizar se cierra a los 5 minutos sin uso.
3. **Permisos.** Política por defecto "sesión obligatoria"; todo lo sensible (respaldos y restaurar, licencia, usuarios y roles, equipos, reportes y exportes, auditoría, certificado e-CF, nube) tiene `RequirePermission`. El certificado e-CF y su contraseña nunca salen por la API.

**Sin autenticación**, solo: `GET /health` (vivo o no, nada más), `POST /api/identity/devices/pair` (el código, con los límites de arriba) y `POST /api/identity/setup` (solo desde la misma PC, `localhost`, y solo mientras no hay usuarios). No hay CORS (no hace falta: ningún navegador llama esta API; sin `AddCors`, el navegador no deja leer las respuestas de otro origen), no se leen `X-Forwarded-For` ni encabezados de identidad, y el secreto `X-Device` solo vale por `http` (desarrollo).

**Qué puede hacer alguien en el Wi-Fi de la tienda** (sin una caja emparejada): ver que el puerto existe y pedir `/health`; probar códigos de emparejamiento (5 errados apagan el código abierto, 10 por IP en 15 minutos: con 10⁸ códigos la probabilidad de acertar es despreciable, pero sí puede **apagar** el código que el dueño acaba de abrir); mandar cuerpos grandes a esos dos endpoints (hasta 30 MB antes de validar; cerrado en esta revisión); saturar el puerto con conexiones (Kestrel sin límite de conexiones); y, **si está activo justo mientras se empareja una caja y nadie compara la huella**, hacerse pasar por el servidor ante esa caja (ADR-0009: la comparación es voluntaria). Después del emparejamiento no puede leer ni cambiar nada: no tiene certificado de la CA. Muchos colmados comparten el Wi-Fi con los clientes: todo lo anterior lo puede hacer cualquier cliente con un teléfono.

## Hallazgos, de mayor a menor

| # | Severidad | Hallazgo | Qué se hace |
|---|---|---|---|
| 1 | 🟠 Media | Suplantar el servidor al emparejar: la huella se muestra pero no se exige compararla (ADR-0009 `shortcut:`). Un atacante en la misma red durante esos minutos se queda con los PIN que pase esa caja | Ticket propuesto A |
| 2 | 🟠 Media | Tiendas con una sola PC escuchan en la LAN igual (0.0.0.0:5443 y la regla del firewall), aunque ninguna caja vaya a conectarse | Ticket propuesto B |
| 3 | 🟡 Baja | Cuerpos de hasta 30 MB sin autenticar en emparejar e instalación | **Arreglado**: 16 KB para todo lo que llega sin certificado |
| 4 | 🟡 Baja | La regla del firewall abre 5443 a cualquier programa, y una reinstalación no la corrige | **Arreglado**: la regla es solo para `Abastalia.Host.Local.exe` y se reescribe en cada instalación |
| 5 | 🟡 Baja | Cualquiera en la red puede apagar el código de emparejamiento abierto (5 errados) | Se acepta: el dueño genera otro; subir el límite debilita la protección contra muchas IP |
| 6 | 🟡 Baja | Sin límite de conexiones ni de peticiones por IP: alguien en el Wi-Fi puede saturar la PC principal | Ticket propuesto C |
| 7 | 🟡 Baja | Con la aprobación de supervisor apagada para una acción (retiro, anular), cualquier sesión la hace, aunque su rol no tenga el permiso (`ApprovalDecorator`). Es a propósito ("el usuario la hace solo") pero sorprende | **Decide el dueño** (ver abajo) |
| 8 | 🟢 Info | Cualquier sesión ve la contingencia (comprobantes B con montos) y el resultado de la DGII de cualquier e-NCF (`/api/fiscal/contingency`, `.../await-result`) | Se acepta: datos de poco valor, la caja los necesita |
| 9 | 🟢 Info | La llave de la CA, la del e-CF y la pimienta de PIN están protegidas con DPAPI de la máquina: un administrador de esa PC las lee | Se acepta (ADR-0009); la carpeta está cerrada al resto de usuarios |

Lo que está bien y no se toca: un solo puerto; mTLS con la CA propia y revocación inmediata; SQL Server sin red; servicio con cuenta virtual sin privilegios (`SeChangeNotifyPrivilege` solo); carpeta de datos cerrada; política de sesión por defecto; límites de PIN y de código; nada de CORS ni encabezados de confianza; OpenAPI solo en desarrollo; actualizaciones solo firmadas.

## ¿Todo dentro de la misma app?

La propuesta: que la app de la PC principal (la de caja, WPF) hospede la API en su propio proceso, sin servicio de Windows.

| | Servicio de Windows (hoy) | Dentro de la app |
|---|---|---|
| Cajas por la red | Siempre disponibles mientras la PC esté encendida | Solo mientras alguien tenga la sesión de Windows abierta **y** la app abierta. Cerrar la app, cerrar la sesión o el reinicio de Windows Update deja sin servidor a todas las cajas |
| Después de un reinicio | El servicio arranca solo (inicio retrasado, se reinicia si falla) | Nadie vende hasta que alguien entre a Windows en la PC principal y abra Abastalia |
| Respaldo de las 23:30, revisión de la auditoría, cola de e-CF (72 h), latido y licencia de la nube | Corren aunque nadie use la PC | Se saltan cuando la app está cerrada; el plazo de 72 h de la DGII corre igual |
| Actualizaciones | La tarea de SYSTEM actualiza el servidor cuando no hay turnos abiertos, sin tocar la app del cajero | Actualizar el servidor es cerrar la app del dueño; las cajas se caen a mitad de una venta |
| Seguridad | Corre como `NT SERVICE\Abastalia` sin privilegios; llaves, base y respaldos cerrados a los usuarios de Windows | Corre como el usuario que entró: cualquiera que use esa PC (un cajero) puede leer la llave de la CA, la del e-CF, la pimienta de PIN y la base. Hay que dar la base SQL a ese usuario |
| Fallos | Un error de la pantalla no tumba el servidor | Un cuelgue de WebView2 o de la pantalla tumba el servidor de todas las cajas |
| Instalar | Servicio, `prepare-service`, login de SQL: más pasos (ya automatizados) | Más simple: un solo ejecutable |
| Firewall | Regla del instalador | Windows pregunta al usuario la primera vez si no hay regla |

**Decisión: se queda el servicio de Windows.** Lo que se gana en simplicidad lo paga la tienda en cajas sin servidor, respaldos que no se hacen y llaves al alcance de cualquier usuario de la PC. Lo que sí vale de la idea va en el ticket B: una tienda con **una sola PC** no necesita escuchar en la red.

## Arreglos de esta revisión

1. **16 KB para lo que llega sin certificado** (`DeviceCertificates.MaxAnonymousBody`, middleware de `Program.cs`): emparejar y la instalación inicial son un código, un nombre y un CSR (máx. 4,000 caracteres). Más grande responde 413 antes de leerlo. Prueba: `DeviceCertificateTests.Without_a_certificate_only_health_and_pairing_answer…` (Kestrel y TLS de verdad).
2. **Regla del firewall solo para el servidor** (`deploy/install.ps1`): `-Program` = `Abastalia.Host.Local.exe`; si la regla ya existe (instalaciones anteriores), se reescribe con el programa, el puerto y el perfil Privado. Prueba: `InstallerScriptTests.TK359_…`.

## Tickets propuestos (sin número; el dueño los asigna desde TK-370)

- **A. Comparar la huella al emparejar, obligatorio.** "Unir a una tienda" no manda el código hasta marcar "La huella es igual a la de la PC principal" (como ya pasa en "Primera instalación" contra otra PC). Costo: un paso más al unir cada caja. Alternativa más cómoda: mostrar la huella como QR en Configuración → Equipos y leerla con la cámara de la tablet (ADR-0010).
- **B. Modo "una sola PC".** Mientras no haya cajas emparejadas, ni el plan permita ninguna (Esencial, `Registers` 0), Kestrel escucha solo en `127.0.0.1` y la regla del firewall queda apagada; al generar el primer código de emparejamiento se abre (el servicio la abre con una tarea programada como administrador, o el instalador la deja creada pero deshabilitada). Cierra todo lo del Wi-Fi para la mayoría de los colmados.
- **C. Límites de conexión y de peticiones en la API local.** `Kestrel:Limits:MaxConcurrentConnections` (p. ej. 200) y un `RateLimiter` por IP para lo anónimo (`/health`, emparejar), con números que no corten a una tienda grande (medirlo en la prueba de 10 cajas).
- **D. Aviso del Wi-Fi compartido.** En la instalación y en Configuración → Equipos: "Si sus clientes usan el mismo Wi-Fi, ponga las cajas en una red aparte (la de invitados del router para los clientes)". Solo texto.

## Decide el dueño

- **Aprobación apagada = cualquiera lo hace (hallazgo 7).** Hoy, si el dueño apaga "pedir supervisor" para retiros o anulaciones, la hace cualquier cajero, tenga o no el permiso en su rol. Opción 1 (hoy): "apagar la aprobación" significa "cualquiera puede". Opción 2: apagarla solo quita al supervisor; quien no tiene el permiso en su rol sigue sin poder. La 2 es más segura y más fácil de explicar, pero cambia lo que pasa en tiendas que ya la apagaron.
  **Decidido por el dueño (17/10/2026): opción 2** («no queremos que el cajero haga cosas malas»). Apagada = solo los roles con el permiso; Cajero recibe por defecto los permisos cuya aprobación viene apagada (hoy quitar línea). Se implementa en TK-378.
- **Ticket A:** ¿obligatorio para todos, o solo cuando la caja no está en la misma PC?
- **Ticket B:** ¿abrir la red sola al generar el primer código (necesita un paso de administrador) o pedirle al técnico que la abra?

## Consecuencias

- La superficie en la red queda en un puerto con mTLS, dos endpoints anónimos con cuerpo pequeño y `/health`.
- El servicio de Windows sigue siendo la pieza que mantiene la tienda funcionando sin nadie frente a la PC principal; la simplicidad de "una sola app" se busca en el instalador (que ya es uno solo) y en el modo de una sola PC.
