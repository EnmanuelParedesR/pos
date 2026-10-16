# Consultas públicas de la DGII (TK-252)

Investigación del 16/10/2026 de las dos páginas públicas de la DGII que Toki puede usar para validar sin cargar el padrón: la de RNC y la de NCF. Todo se consulta desde la PC de la tienda (ADR-0011), nunca desde la nube.

## Resumen

| Página | Formulario real | CAPTCHA o anti-bots | ¿Se automatiza? |
|---|---|---|---|
| RNC: `dgii.gov.do/app/WebApps/ConsultasWeb2/ConsultasWeb/consultas/rnc.aspx` | ASP.NET WebForms, un POST con `__VIEWSTATE` | No hay CAPTCHA ni reCAPTCHA | Sí |
| NCF: `dgii.gov.do/herramientas/consultas/Paginas/NCF-.aspx` | Es una página de SharePoint que incrusta en un `<iframe>` el formulario `…/ConsultasWeb/consultas/ncf.aspx` | No hay CAPTCHA ni reCAPTCHA | Sí, el formulario incrustado |

**Hallazgo sobre protección:** ninguna de las dos tiene CAPTCHA, reCAPTCHA, hCaptcha, Turnstile ni reto de JavaScript. Delante hay un cortafuegos (Azure Front Door, cabecera `x-azure-ref`, y un balanceador NetScaler, cookie `NSC_…`) que **rechaza con 403 "Acceso Denegado"** las peticiones con el User-Agent de `curl` o sin User-Agent, incluso para `robots.txt`. Con un User-Agent propio y honesto (`TokiPOS/x.y.z (+https://github.com/EnmanuelParedesR/pos)`) responde 200. Toki **no se hace pasar por un navegador**: se identifica tal cual. Si un día la DGII bloquea ese User-Agent, agrega un CAPTCHA o cualquier otra detección de bots, Toki no lo esquiva: la consulta queda "sin respuesta" y se usa el padrón cargado (ver *Respaldo*), y esta nota se actualiza.

`robots.txt` (con User-Agent honesto): solo prohíbe `/_layouts/`, `/_vti_bin/` y `/_catalogs/` (rutas internas de SharePoint). `/app/WebApps/…` no está prohibido. No encontramos términos de uso que prohíban la consulta automatizada; por cortesía Toki limita cuánto pregunta (ver *Buen ciudadano*).

## Página de RNC (`rnc.aspx`)

1. `GET` de la página: trae los campos ocultos `__VIEWSTATE`, `__VIEWSTATEGENERATOR`, `__EVENTVALIDATION` (y `__EVENTTARGET`, `__EVENTARGUMENT` vacíos) y la cookie del balanceador.
2. `POST` a la misma URL (`application/x-www-form-urlencoded`) con esos campos, la cookie y:
   - `ctl00$cphMain$txtRNCCedula` = RNC o cédula, solo números (máximo 11).
   - `ctl00$cphMain$btnBuscarPorRNC` = `BUSCAR`.
   - `ctl00$cphMain$txtRazonSocial` y `ctl00$cphMain$hidActiveTab` vacíos.
3. Respuesta: la misma página (HTML, UTF-8). Si está inscrito, la tabla `cphMain_dvDatosContribuyentes` trae filas `<td><b>Etiqueta</b></td><td>Valor</td>`:
   Cédula/RNC (con guiones, `401-50625-4`), Nombre/Razón Social, Nombre Comercial, Categoría (puede venir en blanco), Régimen de pagos, **Estado** (`ACTIVO`, `SUSPENDIDO`, …), Actividad Económica, Administración Local, Facturador Electrónico (`SI`/`NO`), Licencias de Comercialización de VHM.
   Si no está inscrito, la tabla viene vacía y `cphMain_lblInformacion` dice "El RNC/Cédula consultado no se encuentra inscrito como Contribuyente."

## Página de NCF (`ncf.aspx`, dentro de `NCF-.aspx`)

1. `GET` de `…/ConsultasWeb/consultas/ncf.aspx`: `__VIEWSTATE`, `__VIEWSTATEGENERATOR`, `__EVENTVALIDATION`.
2. `POST` con esos campos y:
   - `ctl00$cphMain$txtRNC` = RNC del emisor (el suplidor).
   - `ctl00$cphMain$txtNCF` = NCF (máximo 19).
   - `ctl00$cphMain$txtRncComprador` y `ctl00$cphMain$txtCodigoSeg`: solo para e-NCF (serie E), donde son obligatorios el RNC del comprador y los 6 primeros caracteres del código de seguridad impreso bajo el QR.
   - `ctl00$cphMain$btnConsultar` = `Buscar`.
3. Respuesta:
   - Encontrado: `cphMain_lblRncCedula`, `cphMain_lblRazonSocial`, `cphMain_lblTipoComprobante` (p. ej. "FACTURA DE CRÉDITO FISCAL"), `cphMain_lblNCF`, **`cphMain_lblEstado`** (`VIGENTE`, `VENCIDO`) y **`cphMain_lblVigencia`** ("Válido hasta", `dd/MM/aaaa`); `cphMain_lblInformacion` = "El NCF digitado es válido."
   - No corresponde: sin tabla; `cphMain_lblInformacion` = "El Número de Comprobante Fiscal ingresado no es correcto o no corresponde a este RNC".

Un NCF `VENCIDO` puede ser un comprobante bueno: lo que importa es que la secuencia estuviera vigente en la fecha de la factura. Toki lo rechaza solo si "Válido hasta" es anterior a la fecha de la factura.

## Lo que hace Toki (TK-252)

- `IDgiiPublicLookup` (contrato del módulo Fiscal), implementado por `DgiiPublicSite` (`src/Modules/Fiscal/Toki.Fiscal/Features/DgiiPublicLookup.cs`). El HTML se lee con expresiones regulares tolerantes a espacios, saltos de línea, mayúsculas y atributos extra; las etiquetas se comparan sin tildes.
- **RNC:** `GET /api/fiscal/taxpayers/{rnc}` pregunta primero a la DGII y devuelve además `status`, `category` y `fromDgii`. Clientes, Suplidores y el diálogo de cliente de la caja muestran una insignia "Activo" (verde) o "Suspendido" / "Dado de baja"… (amarilla) junto al RNC.
- **NCF de compras y gastos (606):** al guardar un gasto, o una compra cuyo NCF es nuevo o cambió, se consulta el NCF con el RNC del suplidor. Si la DGII dice que no corresponde, o que la secuencia venció antes de la fecha de la factura, se rechaza con "NCF no válido según la DGII…" (`Purchasing.NcfNotValidAtDgii`). La pantalla muestra entonces "Motivo para guardarlo de todas formas"; con un motivo se guarda y queda en la auditoría (`purchasing.ncf-not-valid-at-dgii`, con RNC, NCF, respuesta y motivo).
- **e-NCF (serie E):** la página pública pide el código de seguridad del QR, que Toki no tiene al escribir la compra; no se consultan ahí. Los e-CF recibidos por el intercambio electrónico (TK-154) ya vienen validados por la DGII, y la compra desde un e-CF recibido (TK-240) los usa tal cual.

### Buen ciudadano

- Caché: un RNC 7 días (no inscrito: 1 día); un NCF válido o vencido, para siempre; "no corresponde", 1 hora (puede ser una secuencia que la DGII aún está cargando).
- Límite para toda la tienda: `Fiscal:DgiiPublic:PerMinute` (30) y `Fiscal:DgiiPublic:PerDay` (500) consultas; cada consulta son 2 peticiones (GET + POST).
- Tiempo de espera corto: `Fiscal:DgiiPublic:TimeoutSeconds` (6 s por petición).
- Tras un fallo (sin red, 403, 5xx, una página que no se entiende) no vuelve a preguntar en 2 minutos.
- `Fiscal:DgiiPublic:Enabled` = `false` la apaga (las pruebas lo hacen; nunca llaman a la DGII real).

### Respaldo

Sin respuesta de la DGII (sin internet, bloqueada, límite alcanzado), el RNC se busca en el padrón cargado (`DGII_RNC.TXT`, TK-162/239), sin insignia; y el NCF queda sin validar (solo el formato), sin frenar la compra.

### Pendiente / atajos

- La caché vive en memoria: al reiniciar el servidor de la tienda se vuelve a preguntar. Una tabla la haría permanente.
- La consulta de una compra se hace con el borrador bloqueado (unos segundos como máximo).
- Cruce con la DGII (TK-223): no consulta NCF uno por uno (el Excel ya viene de la DGII).
- La prueba `ncf-valid.html` es la respuesta real de `ncf-expired.html` con el estado cambiado a `VIGENTE` (no teníamos un NCF vigente público para grabar). Las demás son respuestas reales grabadas el 16/10/2026 (`tests/DgiiPages`), consultando el RNC de la propia DGII (401-50625-4).
