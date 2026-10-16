# ADR-0010: Toma de pedidos (y luego caja ligera) en tablet Android contra la PC principal

- Fecha: 2026-10-15
- Estado: propuesta (spike TK-202, sin código de producción)
- Relacionadas: ADR-0007 (Blazor Hybrid), ADR-0009 (fijar la CA de la tienda y mTLS), TK-193 (encargos), `docs/competencia-poszy.md` §8

## Contexto

En una carnicería o un deli con fila, el dependiente que corta y pesa no está en la caja. Hoy anota en papel o grita el pedido. Poszy se usa desde el celular; Toki solo desde una PC con Windows. El pedido es una pantalla ligera en una tablet o un celular Android, en la red de la tienda, contra la PC principal (`Toki.Host.Local`):

1. **Toma de pedidos** (vendedor de piso): buscar o escanear, pesar (escribir el peso de la balanza de mostrador), armar el carrito y mandarlo a la caja, que cobra.
2. **Consulta de precio y existencia.**
3. **Caja ligera** (cobrar en la tablet): turno, efectivo, impresora, gaveta. Es mucho más grande.

Lo que ya hay y no se debe romper:
- Las pantallas viven en una Razor Class Library (`Toki.Client.UI`) con un cliente HTTP escrito a mano (`TokiApi`). WPF las aloja en BlazorWebView (ADR-0007).
- Cada equipo se empareja con el código de 8 dígitos y un CSR; la PC principal firma un certificado de cliente con la CA de la tienda; el equipo fija esa CA y Kestrel pide certificado de cliente en todo salvo `/health`, emparejar e instalar (ADR-0009). Sin certificado no hay API.
- El e-CF se firma solo en la PC principal. Ningún equipo cliente firma.
- Los encargos (`/api/orders`, TK-193) ya son "un carrito guardado que la caja cobra": F10 los lista y "Cobrar" los carga a precio de hoy.

## Opciones

| | A. PWA del cliente Blazor servida por la PC principal | B. MAUI Blazor Hybrid (Android) | C. App nativa (Kotlin) |
|---|---|---|---|
| Reuso de pantallas | Alto, pero hay que publicar `Toki.Client.UI` como Blazor WebAssembly (hoy solo Server y Hybrid) | **Total**: el mismo RCL en BlazorWebView, como WPF | Ninguno: otra UI, otro cliente HTTP, otras reglas |
| Emparejamiento y mTLS (ADR-0009) | **Malo.** El navegador no deja a la página generar el CSR ni guardar la llave; el certificado de cliente habría que instalarlo a mano en Android (Ajustes → Seguridad, con bloqueo de pantalla) y Chrome pregunta cuál usar. La CA de la tienda también se instala a mano y Android avisa "la red puede estar supervisada" | **Igual que la caja:** `SocketsHttpHandler` con `SslOptions` (fijar la CA) y `ClientCertificates`; la llave en el Android Keystore o cifrada con `SecureStorage` | Igual que B, escrito otra vez |
| HTTPS en la red local | La PWA (service worker, instalar en la pantalla) exige un origen de confianza. Una IP de la tienda con certificado de la CA propia no lo es sin instalar la CA | No aplica: la app valida la CA fijada por su cuenta | No aplica |
| Sin conexión | Sin service worker no hay nada sin red. Con Blazor Server (la otra forma de servirla) cada toque viaja a la PC: el wifi malo del local congela la pantalla | El carrito vive en la tablet; se manda cuando hay red, con el mismo "nunca se pierde el carrito" de la caja | Igual que B |
| Escáner | Lector Bluetooth en modo teclado: funciona | Lector Bluetooth en modo teclado (el campo de escaneo ya lo maneja); cámara con ML Kit o ZXing más adelante; lectores Zebra/Honeywell por *intent* después | Todo, con más trabajo |
| Impresora | No hay acceso a Bluetooth ni a la red cruda desde la página | ESC/POS por Bluetooth o red (`Toki.Hardware.EscPos` cambiando el transporte) o imprimir en la PC principal | Todo |
| Distribución | Una URL | APK firmado: instalación directa (MDM o archivo) o Google Play; actualizaciones a cargo nuestro | APK |
| Costo estimado (solo pedidos) | 2–3 semanas y queda mal en seguridad | **1.5–2 semanas** | 5–6 semanas |

**Native queda fuera:** duplica pantallas, reglas de UI y cliente HTTP para un caso que Blazor ya cubre. **La PWA queda fuera por seguridad, no por gusto:** para que funcione habría que aflojar ADR-0009 (aceptar el secreto `X-Device` en http, o instalar CA y certificado a mano en cada tablet), y es justo lo que se cerró con TK-091.

## Decisión (propuesta)

**B. Un anfitrión MAUI Blazor Hybrid para Android (`Toki.Client.Android`), que reusa `Toki.Client.UI`, empezando solo con toma de pedidos y consulta de precio.**

1. **Pantalla "Pedido"** nueva en `Toki.Client.UI`, pensada para 360–800 px de ancho y táctil (Escuadra: 48 px mínimo, el total grande): buscar o escanear, escribir el peso, cantidad, nota, nombre o número del cliente. **Enviar** crea un encargo para hoy sin abono (`POST /api/orders`, `Kind = Order`), así la caja lo ve en F10 y lo cobra con el peso y el precio de ese momento. El contrato ya existe y cambia poco: hoy un encargo exige cliente ("El pedido necesita un cliente."), y el pedido de alguien que espera en el mostrador solo lleva un nombre o un número de turno. Hace falta aceptar el encargo sin cliente con una nota (o un tipo "pedido de piso"), un filtro en F10 y un aviso en la caja cuando llega uno.
2. **Rol y permisos:** un rol de partida "Vendedor de piso" con `sales.create` (lo que hoy pide `POST /api/orders`) y sin caja, cobro, devoluciones ni costos (`costs.view`, TK-201). El dependiente entra con su código y PIN, como en la caja.
3. **Emparejamiento sin escribir nada:** Configuración → Equipos muestra un **QR** con la dirección de la PC principal, el puerto, el código de 8 dígitos y la huella del servidor. La tablet lo lee con la cámara. Como el QR se ve en la pantalla de la PC (canal aparte de la red), la huella llega por un camino que un atacante en el wifi no controla: es mejor que comparar 16 caracteres a ojo. Después todo es ADR-0009 sin cambios: CSR, certificado de cliente, CA fijada, revocar desde Equipos.
4. **Llaves en la tablet:** la llave del certificado se genera en el Android Keystore si .NET la puede usar ahí para TLS; si no, ECDSA P-256 cifrada con `SecureStorage` (que la protege con el Keystore). Es el mismo nivel que DPAPI del usuario en la caja (`shortcut:` documentado en ADR-0009).
5. **Sin red:** la pantalla trabaja con el catálogo que ya bajó (productos y precios se guardan al entrar), arma el pedido y lo deja en "Por enviar" hasta que vuelve la red. Nunca vende, cobra ni firma sin la PC principal. El aviso es ámbar, nunca rojo (principio 3 de Escuadra).
6. **Escáner:** lector Bluetooth en modo teclado desde el primer día (el campo de escaneo ya ignora lecturas en PIN y cantidad). Cámara con ML Kit después, si los clientes lo piden.
7. **Impresora:** la tablet no imprime en esta fase. Si hace falta una etiqueta del pedido, se imprime en la PC principal o en la caja.
8. **Licencia:** la tablet cuenta como equipo emparejado. Si cuenta como una caja más o como "puesto de pedidos" con otro precio es decisión del dueño (va con TK-203).

**Caja ligera (opción 3)** queda para una segunda fase y otro ADR: necesita turno por equipo, efectivo sin gaveta o con gaveta Bluetooth, impresora Bluetooth (transporte nuevo para ESC/POS), y probar el cuadre ciego en pantalla chica.

## Consecuencias

- Un proyecto nuevo con el *workload* de Android en CI (GitHub Actions en Windows o Ubuntu con `dotnet workload install maui-android`) y un APK firmado. Hay que guardar la llave de firma del APK como la de las licencias (fuera del repositorio).
- `Toki.Client.UI` tiene que funcionar en pantallas angostas: la pantalla "Pedido" es nueva y solo reusa componentes; la caja y la administración no se tocan.
- El servidor no cambia su modelo de seguridad. Lo nuevo en la PC principal es el QR de emparejamiento y el encargo sin cliente (punto 1).
- MAUI agrega una dependencia de Microsoft que hoy no usamos. Si .NET MAUI no permite usar la llave del Keystore para el certificado de cliente de `SocketsHttpHandler`, se usa `SecureStorage` (punto 4) y queda como `shortcut:`.

## Estimado

| Pieza | Tamaño |
|---|---|
| Anfitrión MAUI Android (BlazorWebView, almacén seguro, `HttpClient` con CA fijada y certificado de cliente) | S |
| QR de emparejamiento en Equipos y lectura en la tablet | S |
| Pantalla "Pedido" + catálogo guardado + "Por enviar" | M |
| Encargo sin cliente, rol "Vendedor de piso", aviso de pedido nuevo en la caja | S |
| CI con APK y prueba en una tablet real (Android 10 o más, 3 GB de RAM) | S |
| **Total, solo pedidos** | **1.5–2 semanas** |

## Por verificar antes de construir

- Que .NET para Android permita el certificado de cliente con la llave en el Keystore en `SocketsHttpHandler` (o confirmar el plan B con `SecureStorage`).
- Rendimiento de BlazorWebView en una tablet barata (2–3 GB de RAM) con el catálogo de una carnicería (500–2,000 productos).
- Cómo responden los lectores Bluetooth baratos en modo teclado con el teclado en pantalla de Android (a veces lo esconden o lo duplican).
