# ADR-0013: Versión Ligera en línea (plan `Lite`): arquitectura y alcance de F1

- Fecha: 2026-10-17
- Estado: aceptada (TK-318, issue #287). F1 depende de que pase la prueba de firma en el navegador (TK-317).
- Relacionadas: ADR-0002 (Sqlite), ADR-0003 (dispositivos), ADR-0005 (migraciones por módulo), ADR-0007 (Blazor), ADR-0008 (nube y nodos), ADR-0011 (firma del receptor en la tienda), `arquitectura-v1-vsa.md` §7 y §10, `despliegue-azure.md`, `cuotas.md`, `cobro-paddle.md`, `licencias.md`

## Contexto

El análisis de TK-266 (privado) recomendó un plan **Ligero**: vender y facturar e-CF desde el navegador o el celular, sin PC principal, para el negocio que se pasa del Facturador Gratuito de la DGII y no tiene computadora. Las condiciones que puso, y que este ADR mantiene:

- La firma se hace en el dispositivo, nunca en la nube: ADR-0011 queda igual.
- Una prueba técnica de firma en el navegador pasa primero (TK-317, en curso).
- El soporte es por autoservicio.
- No le quita tiempo a la certificación del producto de tienda.

Hay una restricción nueva del dueño: **toda la suscripción de Azure debe quedar muy por debajo del crédito de US$150 al mes durante el piloto**. Hoy Abastalia gasta ~US$20 al mes (App Service B1 de una instancia ~US$12.40, SQL Basic de 5 DTU y 2 GB ~US$4.90, SWA Free, storage y OpenAI por uso). El 17/10 se dejó la base serverless porque nunca se pausaba (~US$310 al mes). El costo del Ligero tiene que crecer con los clientes que pagan, no estar fijo desde el día uno.

Lo que hay en el código y define las opciones:

- Los módulos de la tienda (`src/Modules`) suponen **un negocio por base**: no hay `TenantId` ni filtros de tenant (solo Settings y Sync lo conocen). `UnitOfWork` abre una conexión con una sola cadena de `DatabaseOptions`.
- `arquitectura-v1-vsa.md` §10 ya preveía `IMultiTenant`, un interceptor, el filtro con nombre `Tenant` y Row-Level Security (RLS) como segunda barrera. Nunca se construyó.
- `Abastalia.Client.UI` es una Razor Class Library que solo depende de `Microsoft.AspNetCore.Components.Web`, Escuadra y las abstracciones de `Abastalia.Hardware`.
- .NET en WebAssembly no tiene RSA: la firma tiene que ir por WebCrypto.

## Decisión 1: dónde corren los módulos y dónde viven los datos

**Host:** un host nuevo, `Abastalia.Host.Lite`, que corre los **mismos módulos** de la tienda (ventas, caja, inventario, fiscal, clientes, reportes, identidad, configuración y sync). Es otra Web App en el **mismo plan B1**, así que no cuesta más. `Abastalia.Host.Cloud` no cambia: el Ligero le habla como le habla una tienda (ADR-0008). Por cada negocio Ligero, `Host.Lite` hace de "PC principal": tiene un nodo de servidor (llave en Data Protection), sube el outbox, pide la licencia y manda el latido. Así el panel, Talia, las alertas, las cuotas y las licencias se reutilizan sin cambios. Meter los módulos dentro de `Host.Cloud` se descartó: sus workers y su DI suponen un solo negocio y ensuciarían la API de la nube.

**Datos.** Se compararon cuatro opciones. Precios de lista de eastus2, aproximados (**verificarlos en la calculadora antes del piloto**). Solo cuenta la base de datos.

| Opción | Reutiliza el código | 10 clientes | 100 clientes | 500 clientes | Veredicto |
|---|---|---|---|---|---|
| **(b) Una base compartida con `TenantId`**, filtro de EF y RLS | Toca todos los módulos una vez (columna, índices y filtro), pero de forma mecánica y ya prevista en §10. Las migraciones se aplican una sola vez | Basic US$4.90 → **US$0.49** por cliente | S1 (20 DTU) US$29.40 → **US$0.29** | S2–S3 (50–100 DTU) US$74–147 → **US$0.15–0.29** | **Elegida** |
| (c) Una base Basic por negocio | Total: cambia solo la cadena de conexión | US$49 → US$4.90 | US$490 → US$4.90 | US$2,450 → US$4.90 | No: más de la mitad del precio, y 500 migraciones en cada versión |
| (a') Una base por negocio en un pool elástico Basic | Total | Pool de 50 eDTU, US$74 fijos → US$7.40 | 100 eDTU (hasta 200 bases), US$147 → US$1.47 | 200–300 eDTU (hasta 500 bases), US$294–442 → US$0.59–0.88 | No en el piloto: US$74 fijos antes del primer cliente. Se puede revisar si algún día hace falta aislamiento físico |
| (d) Un SQLite por negocio en archivos (Azure Files o el `/home` de App Service) | Total: ADR-0002 ya corre los módulos sobre Sqlite | ~US$0.01–0.05 | igual | igual | No: Sqlite **no tiene migraciones** en este código (ADR-0002 crea las tablas una sola vez), los bloqueos de SQLite sobre SMB no son confiables, no hay restauración a un punto en el tiempo y amarra el Ligero a una sola instancia |

**Por qué (b):**

- Es la única opción que empieza en ~US$5 y baja por cliente a medida que crece.
- Los datos fiscales tienen el respaldo y la restauración a un punto en el tiempo de Azure SQL.
- La base se escala en línea: Basic → S0 → S1 → S2/S3, cambiando `databaseSku` y sin parar.
- Usa una base aparte de la de la nube, `sqldb-abastalia-lite`, en el mismo servidor. Las DTU no se mezclan con la ingesta y la política de RLS queda solo en esa base.
- **Cuándo subir de nivel:** Basic hasta ~20 negocios o 1.5 GB; luego S0, que da 250 GB. Se sube cuando la DTU pase del 80 %, como en `despliegue-azure.md`.
- **Lo que crece se saca de SQL, igual que hoy:** el XML de los e-CF pasa al archivo frío a los 7 días y el catálogo se guarda en caché en el navegador (1,000 productos), así que escanear no consulta la base.

**Costo total por cliente al mes** (nube incremental del Ligero + Paddle):

| | 10 clientes | 100 clientes | 500 clientes |
|---|---|---|---|
| SQL (b) | 4.90 (Basic) | 29.40 (S1) | 74–147 (S2–S3) |
| App Service | 0 (el mismo B1) | +12.40 (pasar a B2, compartido con la nube) | +50–100 (P0v3/P1v3, una instancia) |
| Blob frío, registros (tope de 0.1 GB al día), OpenAI (30 preguntas), correo | ~1 | ~10 | ~50 |
| **Nube del Ligero, total** | **~US$6** | **~US$52** | **~US$175–300** |
| **Nube por cliente** | **~US$0.60** | **~US$0.52** | **~US$0.35–0.60** |
| Paddle (5 % + US$0.50): mensual US$9 / anual US$90 | 0.95 / 0.42 | igual | igual |

En el piloto (≤ 20 negocios) la suscripción entera queda en **~US$26–30 al mes**. Queda un presupuesto de Azure con aviso a US$100 (ticket F1-10).

**Aislamiento entre negocios:**

- El `TenantId` sale **siempre de la identidad del dispositivo**, nunca del cuerpo ni de la URL (§10).
- Un interceptor de `SaveChanges` lo rellena. El filtro con nombre `Tenant` va en todas las entidades.
- Los índices únicos llevan `TenantId` primero: código de barras, secuencias e-NCF, usuarios y demás (hoy hay 15 `IsUnique` en los módulos).
- RLS sobre `SESSION_CONTEXT(N'TenantId')`, fijado de solo lectura cada vez que se abre una conexión. Cubre también el SQL a mano: la reserva de e-NCF con `UPDLOCK`, `TaxpayerRegistry` y `Backups`.
- Una prueba de CI con dos negocios falla si alguna ruta o consulta devuelve filas del otro, **también con los filtros de EF apagados**.
- En la tienda la columna existe con un valor fijo y el filtro no se registra (una base, un negocio). Las migraciones siguen siendo las mismas (ADR-0005).

## Decisión 2: firma en el navegador, sin custodia en la nube

- **ADR-0011 queda intacto.** La nube no guarda certificados, no firma semillas y no ve el token de la DGII. El dueño importa su `.p12` una vez en el dispositivo de venta. Se lee en el navegador y queda como `CryptoKey` RSA **no exportable** en IndexedDB. Después se descartan el archivo y la contraseña.
- **Flujo de una venta:**
  1. `Host.Lite` registra la venta, reserva el e-NCF en la misma transacción (sin huecos, §7.2), arma el XML sin firmar y lo deja `AwaitingDeviceSignature` (el estado `PendienteFirma` que ya preveía §7.2).
  2. El navegador revisa el XML: tipo permitido y RNC propio. Lo firma (XMLDSig envuelta + C14N + RSA-SHA256 con WebCrypto; la librería la elige TK-317, hoy `xmldsigjs`), calcula el código de seguridad y lo envía **directo a la DGII**: semilla → token guardado solo en memoria, recepción o `recepcionfc` (RFCE), consulta de estado.
  3. El navegador sube a `Host.Lite` el XML firmado y la respuesta. El servidor verifica la firma, que el digest sea el del XML que armó y que el certificado sea del RNC. Después imprime o comparte.
- **Lista blanca en el navegador** (la regla de ADR-0011, del lado del dispositivo): firma solo e-CF, RFCE, ANECF, ACECF y ARECF de su propio RNC, y la semilla solo para sí mismo. Cada firma va a un registro local que el dueño puede ver.
- **CORS de la DGII:** el 16/10/2026 la semilla, validar semilla, la recepción y `recepcionfc` respondieron `Access-Control-Allow-Origin: *` en TesteCF y en producción, en el preflight con `Origin`. TK-317 tiene que confirmar además que el preflight admite `Authorization` y el envío `multipart/form-data`.
- **Si el navegador no puede firmar:**
  - Falla en un navegador → lista de navegadores admitidos (Chrome/Edge en Android y Windows; Safari en iOS solo si pasa TK-317) y aviso al entrar.
  - El navegador borró la llave → aviso para volver a importar el `.p12`, con `navigator.storage.persist()`. Las ventas pendientes se firman al volver.
  - La DGII cierra el CORS → relevo propio sin estado en `Host.Lite` que solo reenvía bytes ya firmados. La firma sigue en el navegador, pero la nube vería el token de la DGII en tránsito (vale ~1 h). **Eso lo decide el dueño antes de encenderlo.**
  - TK-317 falla en todos → **el Ligero no sale**. No hay firma en la nube de respaldo: eso sería la custodia de ADR-0011 con sus 7 condiciones (TK-247), y queda fuera de este ADR.
- **Receptor (ARECF):** modo A de ADR-0011. El firmante es el navegador abierto: `Host.Lite` contesta `sign-requests/next` como nodo del negocio y le pasa la solicitud al navegador. Con la app cerrada aplican el 503 + `Retry-After` y `FeLateAcknowledger`, como hoy. Los pasos 8 a 11 de la certificación piden el navegador abierto.

## Decisión 3: pantalla de venta

**Blazor WebAssembly con `Abastalia.Client.UI`**, en un anfitrión nuevo, `Abastalia.Client.Lite`: PWA alojada en SWA Free, en su propio origen (por ejemplo `venta.abastalia.com`). Es el camino con más reutilización:

- La caja ya existe: carrito, cobro, PIN, el diálogo de supervisor atado al código de ProblemDetails, el cliente `AbastaliaApi` y las teclas F. La RCL no depende del servidor, así que compila a WebAssembly.
- Una PWA en React con el código del panel tendría que reescribir todo eso y mantener **dos copias de las reglas de la pantalla de venta**. El panel no tiene ninguna pantalla de caja.
- Lo que cambia: hardware del navegador (impresión con `window.print`, PDF o compartir por WhatsApp con Web Share; no hay balanza ni gaveta), una vista táctil de la caja para el celular y el firmador como módulo JS por interop (hace falta igual con React).
- Costo: la primera descarga pesa unos MB (con recorte, sin AOT; medirlo). Después queda en la caché del service worker.
- **El origen propio no carga scripts de terceros:** Paddle.js y el sitio se quedan en el panel. CSP estricta y SRI en todos los recursos.

## Decisión 4: en línea primero

F1 **solo vende con conexión**, y un solo dispositivo vende por negocio. Sin conexión la caja se pone ámbar, guarda el carrito y no cierra la venta. No existe "vender ahora y firmar después" (`ecf-propio.md` §6).

La venta sin conexión llega en una fase posterior, solo si los pilotos la piden:

- un bloque de e-NCF reservado por dispositivo;
- el generador de XML compilado a WebAssembly;
- una cola en IndexedDB;
- la contingencia de 72 h.

Si la DGII no responde, el documento ya firmado queda en el navegador y en el servidor, y el navegador reintenta. El servidor lleva el plazo de 72 h y avisa al dueño a las 48 h.

## Decisión 5: plan, cobro y licencia

| | Ligero (`Lite`) |
|---|---|
| Precio | **US$9 al mes** (decidido). Anual US$90, con la regla de 10 × mensual |
| Dispositivos | 1 que vende (registrar otro revoca el anterior; el dueño también lo puede revocar en el panel) |
| Usuarios | Dueño + 1 cajero con PIN (`cashiers` 1, `registers` 0), 1 usuario del panel (`Invites` 0) |
| e-CF | 1,500 al mes de **uso justo**: nunca se frena la venta. Si se pasa, alerta como en TK-286; dos meses seguidos, se sugiere el Esencial |
| Productos | 1,000 (solo impide crear más) |
| No incluye | Balanza, gaveta, impresora ESC/POS, varias cajas o tiendas, venta sin conexión |
| Asistente | 30 preguntas al mes; la lectura de facturas va como adicional |

- **Reutilización:** el producto de Paddle lleva `custom_data.abastalia` con `plan: "Lite"`, `cashiers`, `registers` y `quotas` (`docs/cobro-paddle.md`). También van `Quotas:Lite:*` y `Billing:Plans:Lite` en appsettings, y la licencia firmada por la nube con `plan: "Ligero"`. `Host.Lite` la pide por el nodo del negocio y `Settings` aplica los límites igual que en la tienda. El conteo de ventas llega por el latido (`DailyUsage`).
- **Subir a Esencial:** `Host.Lite` exporta todas las filas del negocio, tabla por tabla, siguiendo el modelo de EF: un paquete comprimido, firmado por la nube, que el instalador de la PC principal importa sobre una base recién migrada. El `TenantId` de la nube no cambia, así que el historial del panel sigue. Las secuencias e-NCF continúan donde quedaron. El Ligero queda en solo lectura y el `.p12` se vuelve a importar en la PC.

## Seguridad

- **Aislamiento:** ver la Decisión 1. Antes del primer piloto, una revisión de seguridad intenta cruzar negocios.
- **Llave en el navegador:**
  - "No exportable" protege menos que el almacén de Windows. Un XSS no se lleva la llave, pero convierte el navegador en un firmador. Se mitiga con el origen propio sin terceros, CSP, SRI, la lista blanca de firma, el registro de firmas visible para el dueño y una alerta en la nube si hay firmas fuera de lo normal.
  - Un despliegue malicioso del sitio podría pedir firmas: el despliegue de `Client.Lite` necesita aprobación.
  - Celular perdido: revocar el dispositivo en el panel y seguir la guía de PC robada (`guia-pc-robada.md`), incluido revocar el certificado con la entidad.
- **Límites:**
  - Por minuto: por dispositivo en `Host.Lite` (ventas y API), y por IP en el registro del dispositivo y el código de conexión.
  - Por mes: los medidores de `cuotas.md` con los valores de `Lite`.
  - La DGII la llama el navegador: no le cuesta nada a la nube.

## Alcance de F1 (tickets para crear; los números TK los asigna el chat general)

| # | Ticket | Tamaño | Depende de |
|---|---|---|---|
| F1-1 | `TenantId` en todos los módulos: `IMultiTenant`, interceptor, filtro `Tenant`, índices únicos con el tenant, RLS en la base del Ligero, prueba de aislamiento en CI con los filtros apagados; en la tienda, valor fijo sin filtro | L | — |
| F1-2 | `Abastalia.Host.Lite`: los mismos módulos, `ITenantContext` sacado del dispositivo, workers (outbox, contingencia, licencia, latido) que recorren los negocios, un nodo de servidor por negocio hacia `Host.Cloud`, Web App y `sqldb-abastalia-lite` (Basic) en Bicep, alta del negocio al elegir el plan `Lite` | L | F1-1 |
| F1-3 | Dispositivo de venta: código de conexión del panel → llave ECDSA P-256 no exportable en el navegador con la firma de pedidos de ADR-0008; uno solo vende; dueño + 1 cajero con PIN; revocar en el panel | M | F1-2 |
| F1-4 | Firmador del navegador (lo que sale de TK-317): importar el `.p12`, `CryptoKey` en IndexedDB, lista blanca, registro de firmas; semilla, token, recepción, RFCE y estado directo a la DGII | M | TK-317 |
| F1-5 | Fiscal en modo `DeviceSigned`: e-NCF y XML en la transacción, `AwaitingDeviceSignature`, endpoints de pendientes, firmado y resultado, verificación de firma, digest y RNC en el servidor, plazo de 72 h con aviso y archivo frío | M | F1-2, F1-4 |
| F1-6 | `Abastalia.Client.Lite`: PWA Blazor WebAssembly con `Client.UI` en su propio origen (SWA Free), CSP y SRI, catálogo en caché, recibo por impresión, PDF o WhatsApp, caja táctil para el celular y crítica de diseño | L | F1-3, F1-5 |
| F1-7 | Plan `Lite`: producto de Paddle de US$9/US$90, `Quotas:Lite`, licencia `Ligero`, límite de 1,000 productos, alerta de uso justo de 1,500, `/precios` | S | F1-2 |
| F1-8 | Receptor del Ligero (ADR-0011 modo A): `Host.Lite` le pasa `sign-requests` al navegador abierto; 503 y acuse tardío como hoy | M | F1-2, F1-4 |
| F1-9 | Subir a Esencial: exportar el negocio en un paquete firmado, importarlo en el instalador de la PC principal, el Ligero queda en solo lectura | M | F1-1, F1-2 |
| F1-10 | Cierre de seguridad y costo: límites por minuto por dispositivo e IP, alerta de firmas fuera de lo normal, presupuesto de Azure con aviso a US$100, revisión de seguridad (cruce de negocios) antes del primer piloto | S | F1-2 a F1-8 |

**Orden:**

1. Primero F1-1 → F1-2, y en paralelo TK-317 → F1-4.
2. Luego F1-3, F1-5, F1-7 y F1-8.
3. Al final F1-6, F1-9 y F1-10.

**Salida de F1:** 10–20 pilotos que pagan, certificados en TesteCF desde el navegador.

## Consecuencias

- Hay un tercer host y una tercera pantalla que mantener, pero sin una segunda copia de las reglas: los mismos módulos y la misma RCL.
- Los módulos ganan `TenantId` también en la tienda. Es el cambio más grande de F1 y deja preparado el paso a *shards* por grupo de negocios si algún día pasan de miles.
- El receptor del Ligero solo acusa con el navegador abierto (pregunta 44 a la DGII sin responder): es un riesgo aceptado, igual que con la PC apagada.
- El Ligero rompe la regla de "todas las funciones en todos los planes" (`cobro-paddle.md`). Se justifica porque es otro producto, en la nube, y lo que no trae es por la tecnología.
