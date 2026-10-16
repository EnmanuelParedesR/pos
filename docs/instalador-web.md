# Instalador desde la web (TK-196, plan) 🟡

Plan corto de lo que falta para que una persona baje Toki de la web y empiece la prueba de 14 días **sin técnico**. No está construido; hoy se instala con `deploy/install.ps1` sobre una publicación de `Toki.Host.Local`. La parte dentro de la app (prueba desde la primera venta, avisos, Restringido, "Comprar licencia", asistente de un equipo y datos de ejemplo) sí está hecha.

## Qué tiene que hacer el instalador

1. **Comprobar el equipo:** Windows 10/11 de 64 bits, espacio libre (≥ 5 GB con SQL Server), .NET no hace falta (publicación *self-contained*). Pedir permisos de administrador una sola vez.
2. **Base de datos:** instalar **SQL Server Express 2025** en silencio si no hay una instancia `TOKI` (`/Q /IACCEPTSQLSERVERLICENSETERMS /ACTION=Install /INSTANCENAME=TOKI /TCPENABLED=0`), solo escuchando en la PC. **No LocalDB:** corre como el usuario que inició sesión y no como servicio, así que no sirve para el nodo que atiende a las cajas (`docs/arquitectura-v1.md`, decisión ya tomada). Si ya existe la instancia, reutilizarla.
3. **Servicio:** lo mismo que `deploy/install.ps1`: copiar a `C:\Program Files\Toki`, crear el servicio `Toki` con su cuenta virtual, `prepare-service` (certificado del nodo, `C:\ProgramData\Toki`, login de SQL), regla de firewall 5443 en perfil privado, arrancar.
4. **App de caja:** instalar la app WPF en la misma PC con acceso directo en el escritorio y abrirla al terminar. Abre en "Primera instalación" (el servidor está en esta PC) con la casilla de datos de ejemplo.
5. **Sin licencia:** no pide código. La prueba empieza sola con la primera venta; "Comprar licencia" está en Configuración → Licencia.
6. **Desinstalar:** detener y borrar el servicio y la app; **nunca** borrar la base de datos ni `C:\ProgramData\Toki` sin preguntar (ahí están las ventas y los respaldos).
7. **Mudarse a una PC nueva (R9 #16):** se instala como arriba y se restaura el respaldo con su frase. El respaldo trae **los datos, no las llaves** de la PC vieja: `C:\ProgramData\Toki\keys` está protegido con DPAPI de esa máquina y el certificado digital de e-CF está en su almacén de Windows, no exportable. En la PC nueva hay que: importar otra vez el certificado `.p12` (Configuración → Comprobantes), volver a escribir la frase de los respaldos, y volver a unir cada caja (la tienda crea una CA nueva; las cajas viejas aparecen en salud como "vuelva a unir"). Los números e-NCF no se repiten: siguen desde lo que tenga el respaldo, así que se restaura **el respaldo más nuevo**, y si la PC vieja firmó después, se cargan los rangos desde el siguiente número libre como en TK-221.

## Empaquetado: qué usar

| Opción | A favor | En contra | Uso |
|---|---|---|---|
| **Inno Setup** (gratis) | Un `.exe` firmado que hace todo lo de arriba, incluido SQL Express y el servicio; se descarga de la web | Script propio que mantener | **Elegida para V1** |
| **Velopack** | Actualizaciones delta de la app de caja (`docs/arquitectura-v1.md` §6.3) | No instala servicios ni SQL Server | Para **actualizar**, no para la primera instalación |
| **MSIX** | Instalación limpia de la tienda de Windows | No puede instalar un servicio de Windows clásico ni SQL Express | Descartada para el nodo |
| **winget** | `winget install Toki.Toki` para técnicos | Solo distribuye: necesita el `.exe` de arriba igual | Más adelante, apuntando al mismo `.exe` |

## Construido (TK-242)

- **`deploy/installer/toki.iss`** (Inno Setup 6): un `TokiSetup-<versión>.exe` con dos tipos en el asistente, **PC principal** (servidor + caja) y **Caja**. Silencioso: `/VERYSILENT /TYPE=server` o `/TYPE=register`. La PC principal usa `deploy/install.ps1` (servicio, `prepare-service`, firewall 5443 privado) e instala SQL Server Express en `.\SQLEXPRESS` si falta (es la instancia del `appsettings.json` del servidor; se reutiliza si existe). Ambos tipos instalan WebView2 Evergreen si falta. SQL Express y WebView2 se **descargan de Microsoft al instalar**. Apps publicadas *self-contained*: no instala .NET.
- **Carpeta `keys`:** `prepare-service` le quita la herencia de `C:\ProgramData` y la deja solo para el servicio, SYSTEM y Administradores.
- **App de caja con Velopack:** el instalador corre el `Toki-Setup.exe` de Velopack como el usuario que inició sesión (queda en `%LocalAppData%\Toki`). La app baja la versión nueva en segundo plano y se aplica **al abrirla de nuevo** (`AppUpdates.cs`). Solo con `"Updates": { "FeedUrl" }` y si está instalada: en desarrollo y pruebas no busca nada.
- **Servidor:** Velopack no sirve para un servicio de Windows. Con `Updates:FeedUrl`, `ServerUpdateCheck` lee `releases.<canal>.json` cada 30 min, anota "Hay una actualización" en el registro y escribe `C:\ProgramData\Toki\update.json`. La tarea programada `Toki\Actualizar servidor` (cada hora, `update-service.ps1`) baja `TokiSetup-<versión>.exe` desde el `FeedUrl` instalado a una carpeta solo de SYSTEM, exige la firma de Toki (`Updates:SignerThumbprints`) o el SHA-256 publicado en el feed (`TokiSetup-<versión>.exe.sha256`, R10 #1) y lo corre en silencio **solo si no hay turnos abiertos o fuera del horario** (`Updates:OpensAt`/`ClosesAt`, por defecto 07:00–22:00).
- **Canales (lanzamiento por etapas):** `pilot` y `stable` (`build.ps1 -Channel`). La caja usa el canal con que se empaquetó; `Updates:Channel` la cambia.
- **Desinstalar:** quita el servicio, la regla de firewall y la tarea. Pregunta "¿Borrar también los datos de la tienda?" (No por defecto); solo con Sí corre `remove-data.ps1` (base de datos Toki y `C:\ProgramData\Toki`). La app de caja se quita aparte desde Aplicaciones de Windows (es por usuario).
- **Construir:** `deploy/installer/build.ps1 -Version 1.0.0 -Channel pilot -FeedUrl <url>` publica las dos apps, las empaqueta con `vpk` y compila con `ISCC`. Sin `vpk` o sin Inno Setup publica lo que puede y dice qué falta. El feed (`artifacts/releases` + el setup) se sube a un GitHub Release o a un contenedor de Azure Blob.
- **Página de descarga:** `/descargar` en el sitio (con `site.installerUrl` vacío muestra "[URL DEL INSTALADOR]").

## Pendiente antes de construirlo

- Certificado de firma de código (Authenticode) para que SmartScreen no asuste en la primera descarga.
- Parámetros exactos de la instalación silenciosa de Express 2025 y su tamaño (verificar).
- Página de descarga en la web (TK-197) con el enlace al `.exe` y la explicación de la prueba.
- Medir el embudo `instalada → primera venta` (`docs/metricas-producto.md`): el instalador no envía nada; la nube lo sabe cuando la tienda se conecta.
