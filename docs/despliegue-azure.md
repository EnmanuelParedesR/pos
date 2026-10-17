# Despliegue de la nube en Azure

Cómo se crea y se publica la nube de Abastalia (ADR-0008): la API `Abastalia.Host.Cloud`, el panel del dueño y lo que necesitan. Todo vive en un solo grupo de recursos por ambiente (`rg-abastalia-prod`).

## Qué crea `deploy/azure/main.bicep`

| Recurso | Para qué | Costo en reposo |
|---|---|---|
| App Service Linux B1 + Web App .NET 10 | La API (`Abastalia.Host.Cloud`). Siempre encendida, solo HTTPS, con identidad administrada. | ~13 USD/mes |
| Azure SQL serverless (Gen5, 1 vCore, mínimo 0.5, pausa a los 60 min) | Esquemas `cloud`, `ingest` y `replica`. Solo cuentas de Entra ID: no hay usuario ni contraseña de SQL. | Casi nada mientras está en pausa; se cobra el almacenamiento |
| Key Vault (RBAC) | La clave de Azure OpenAI. La API la lee con su identidad administrada (referencia `@Microsoft.KeyVault(...)`). Las copias de certificados e-CF (CloudCustody, TK-151) están apagadas desde TK-246. | Centavos |
| Log Analytics + Application Insights | Logs y trazas de la API (agente automático de App Service). | Según el volumen |
| Static Web App (Free) | El sitio público y el panel del dueño: una sola app React (TK-228). | Gratis |
| Azure OpenAI (S0) + despliegue `chat` | El asistente. El modelo se elige por precio. | Solo por uso |
| Storage Account (StorageV2, LRS, Cool) + contenedor `ecf-archive` | Archivo frío de los e-CF firmados (TK-207): 10 años, escritura única, pasa a Archive a los 180 días. Solo la identidad de la API entra. | Centavos (ver abajo) |
| Contenedor `store-backups` (misma cuenta) | Respaldos cifrados de las tiendas (TK-238): 7 diarios + 4 semanales por tienda | Centavos (ver abajo) |

La API no consulta la base cuando no hay tiendas subiendo datos: el chequeo de salud de App Service (`/health`) no la toca y el aplicador del inbox solo trabaja cuando llega un lote. Así la base se pausa de noche. `/health/ready` sí prueba la base; desde R11 (TK-253) solo responde con el token de un administrador de la plataforma (con verificación en dos pasos): sin él, 401.

R11 (TK-253): la API entra a Azure OpenAI con su identidad administrada (rol "Cognitive Services OpenAI User"; el recurso no acepta claves) y no lee secretos del Key Vault; los roles de certificados del Key Vault solo existen con `allowCloudCustody=true`. La Web App no acepta usuario y contraseña de publicación (FTP ni SCM): `az webapp deploy` entra con Entra ID.

## Lo que está desplegado hoy (TK-139, 2026-10-15)

**Redespliegue del 16/10/2026** (main `ac83666`): infraestructura con la cuenta de almacenamiento del archivo frío (stabastaliaprod5qsy5n, retención inmutable sin bloquear), los roles de Key Vault del receptor y la protección contra purga del Key Vault (irreversible); API con todas las migraciones hasta TK-231 y las raíces de INDOTEL; sitio y panel en la Static Web App (/ sitio, /app panel). /health/ready responde Healthy.

Suscripción **Visual Studio Enterprise Subscription – MPN** (`fc0b2ba5-daf7-43fd-a6a2-7dc6f60b8dff`, tenant `8d025706-…`), grupo **`rg-abastalia-prod`**.

| Recurso | Nombre | Región | SKU |
|---|---|---|---|
| API (Web App Linux, .NET 10) | `app-abastalia-prod-5qsy5n` → https://app-abastalia-prod-5qsy5n.azurewebsites.net | eastus2 | Plan `asp-abastalia-prod` B1 |
| Panel (Static Web App) | `stapp-abastalia-prod` → https://gentle-glacier-0579a850f.6.azurestaticapps.net | eastus2 | Free |
| Azure SQL | servidor `sql-abastalia-prod-eigklc`, base `abastalia` | **centralus** | GP_S_Gen5, 1 vCore, mínimo 0.5, pausa a los 60 min, 32 GB máx., respaldo local |
| Azure OpenAI | `oai-abastalia-prod-5qsy5n`, despliegue `chat` = `gpt-5-nano` (`2025-08-07`) | eastus2 | S0, GlobalStandard, 10K tokens/min |
| Key Vault | `kv-abastalia-prod-5qsy5n` (secreto `AzureOpenAI--ApiKey`) | eastus2 | Standard, RBAC |
| Log Analytics / Application Insights | `log-abastalia-prod` / `appi-abastalia-prod` | eastus2 | Por GB, 30 días |

- **SQL en centralus:** eastus2 y eastus respondieron *"Provisioning is restricted in this region"* para servidores SQL nuevos en esta suscripción. centralus es la región disponible más cercana (parámetro `sqlLocation`). La latencia extra es de unos pocos milisegundos.
- **Firewall de SQL:** solo `AllowAzureServices`. Para conectarse desde una PC se agrega una regla temporal con su IP y se borra al terminar.
- **Acceso de la API a SQL:** usuario contenido `[app-abastalia-prod-5qsy5n]` (identidad administrada) con `db_owner`. Ya está creado. No hay inicios de sesión de SQL (solo Entra).
- **Migraciones:** las aplica la API al arrancar (`CreateCloudDatabaseOnStartup`). No hay paso aparte.
- **Logs del contenedor:** están activados en el sistema de archivos (`az webapp log config --docker-container-logging filesystem`). Se leen con `az webapp log tail -g rg-abastalia-prod -n app-abastalia-prod-5qsy5n`.

### Modelo del asistente: por qué `gpt-5-nano`

Precios de pago por uso (GlobalStandard, eastus2, API pública de precios de Azure, USD por millón de tokens). Todos tienen *function calling* y cuota en la región:

| Modelo | Entrada | Salida | Nota |
|---|---|---|---|
| **gpt-5-nano** | **0.05** | **0.40** | El más barato. GA hasta 2027-02. |
| gpt-4.1-nano | 0.10 | 0.40 | *Legacy*, se retira en 2027-04. |
| gpt-4o-mini | 0.15 | 0.60 | Se está retirando. |
| gpt-5.4-nano | 0.20 | 1.25 | Más nuevo, cuatro veces más caro. |

`gpt-5-nano` es un modelo de razonamiento: los tokens de razonamiento se cobran como salida. Con preguntas cortas del dueño igual sale en fracciones de centavo por pregunta. Necesita `api-version` `2025-04-01-preview` o más nueva (parámetro `openAiApiVersion`, va a `AzureOpenAI__ApiVersion`). Si algún día la factura del asistente sube, `gpt-4.1-nano` es el respaldo (mismo precio de salida, sin razonamiento).

**Lectura de facturas de suplidores (TK-224).** El mismo despliegue `chat` lee el PDF o la foto que sube la tienda (`POST /ai/v1/supplier-invoice`, autenticado como el nodo; la nube no guarda el archivo). `gpt-5-nano` acepta imágenes y PDF como entrada: la foto va como `image_url` y el PDF como parte `file` (base64) de Chat Completions. Costo estimado por factura: ~1,500–6,000 tokens de entrada y ~1,000–5,000 de salida con el razonamiento, o sea **~US$0.001–0.003** (menos de RD$0.20). Pendiente (🟡): probar el PDF contra el despliegue real (la parte `file` en Azure puede exigir una `api-version` preview más nueva; si la rechaza, el plan B es mandar fotos) y subir la cuota de 10K tokens/min si varias tiendas leen facturas a la vez. Si `gpt-5-nano` dejara de aceptar imágenes, el modelo con visión más barato de la tabla es `gpt-4.1-nano`.

### Costo estimado al mes en reposo

| Recurso | USD/mes |
|---|---|
| App Service B1 Linux (0.017/h) | ~12.40 |
| SQL serverless pausada: solo almacenamiento (0.138/GB) | ~0.20–0.50 |
| SQL despierta: 0.626 por vCore-hora, mínimo 0.5 vCore (~0.31/h). Cada arranque de la API la despierta al menos 1 h | ~0.30 por despertar |
| Key Vault, Log Analytics (5 GB/mes gratis), Static Web App Free | ~0 |
| Azure OpenAI | Solo por uso (~0.0005 por pregunta) |
| Archivo frío de e-CF (Blob Cool → Archive) | ~0.01–0.25 por tienda (ver abajo) |
| Respaldos de las tiendas (Blob Hot, TK-238) | ~0.05–0.10 por tienda que lo use |
| **Total en reposo** | **~13–15** |

Cada tienda conectada manda un latido cada 4 horas (`Sync:HeartbeatMinutes`, 240; R8 #21): con uno por hora la base no se pausaba nunca. Con tiendas sincronizando todo el día la base no se pausa y sube a ~0.31/h mientras haya actividad (unos 7–8 USD por día con actividad continua). La suscripción MPN trae 150 USD de crédito al mes.

### Archivo frío de e-CF (TK-207)

La tienda sube cada e-CF firmado (XML, RFCE y respuesta de la DGII) a `POST /fiscal/v1/archive`; la nube lo guarda primero en SQL. Pasados `Archive:HotDays` (7, R12 A) días, la API mueve el XML al contenedor `ecf-archive` (`{negocio}/{e-NCF}.xml.gz` y `.rfce.xml.gz`, en Cool) y deja la fila en SQL como índice (estado, respuesta de la DGII, dónde quedó). Lo hace después de recibir una subida y como mucho una vez al día, así que nunca despierta la base por su cuenta. El dueño o el contador lo buscan por e-NCF en el panel (Ventas → "Buscar un comprobante electrónico", `GET /api/fiscal/archive/{eNCF}`). Si ya pasó a Archive, la primera búsqueda pide sacarlo (prioridad estándar, hasta 15 horas) y el panel dice que vuelva más tarde.

- **Reglas del contenedor:** retención de 10 años desde la subida (`immutabilityPolicies`, gratis): nada se cambia ni se borra antes. La regla de ciclo de vida pasa a Archive a los 180 días (el mínimo de Archive, sin cargo por borrado temprano) y borra a los 3,660 días. Uno que se sacó de Archive queda en Cool 7 días antes de volver.
- **Bloquear la retención** (irreversible; hacerlo cuando el dueño lo apruebe): `az storage container immutability-policy lock --account-name <cuenta> -c ecf-archive --if-match <etag>` (el `etag` sale de `az storage container immutability-policy show`).
- **Costo** (precios de lista eastus2, LRS, USD; revisar en la calculadora): un e-CF comprimido pesa unos 3–5 KB. Una tienda con 300 facturas al día junta ~0.5 GB por año. Guardar: Cool ~0.01/GB-mes y Archive ~0.002/GB-mes, o sea centavos aun con 10 años acumulados. Lo que más pesa son las operaciones: ~0.10 por 10,000 escrituras en Cool y otro tanto al pasar a Archive, unos 0.10–0.25 USD al mes por tienda con ese volumen. Sacar uno de Archive cuesta ~0.02/GB más la lectura: nada para unos pocos documentos. En SQL, el mismo volumen costaría ~0.138/GB-mes y crecería sin parar.
- **Sin Azure** (desarrollo): `Archive:Folder` guarda los archivos en una carpeta; sin `Archive:Folder` ni `Archive:Blob:ServiceUri` los XML se quedan en SQL.

### Una sola instancia de la API (R12 B)

El plan B1 queda fijo en **una instancia** (`main.bicep`, `capacity: 1`, sin autoescalado). No subirlo a dos sin antes mover a la base (o a Redis) lo que hoy vive en la memoria del proceso:

- `FeSignBroker` (la tienda firma el acuse del receptor e-CF: el sondeo y la petición del suplidor tienen que caer en la misma instancia).
- La compuerta de `ReferralRewards` (aplica un descuento por vez).
- `LoginThrottle` (contraseñas y códigos 2FA equivocados), `IdentityEmailCap` (correos por destinatario), `FailedCodes` (códigos de conexión).
- Todos los límites por minuto u hora (`AddRateLimiter` y `NodeGate`, ventanas fijas en memoria).
- `InboxApplierWorker` (aplica la bandeja; con dos, un evento podría aplicarse dos veces: es inofensivo, las proyecciones reemplazan por id y un choque de clave de uno ya aplicado no cuenta como fallo).

Los nonces de los nodos ya están en la base (R11 B-Low3). Al arrancar, la API devuelve los intentos a los eventos que se agotaron (R12 E), así un arreglo desplegado los aplica.

### Tamaño de la base (R12 A)

`databaseMaxGigabytes` (250 por defecto; GP serverless con 1 vCore llega a 512). Lo que más crece sale de SQL: la bandeja borra lo aplicado a los 7 días (`InboxApplier.KeepApplied`) y el XML de los e-CF pasa al archivo frío a los 7 días (`Archive:HotDays`). Nada lee de SQL el XML de un e-CF más viejo: la búsqueda por e-NCF lo trae del archivo frío. **Costo:** si Azure cobra el almacenamiento por el máximo configurado y no por lo usado (~0.138 USD/GB-mes, ~34 USD con 250 GB), revisar la primera factura y bajarlo con el parámetro (64 GB ≈ 9 USD) hasta que haga falta; subirlo no requiere parar la base.

### Respaldos de las tiendas en la nube (TK-238)

Con "Copiar también a la nube" encendido en la tienda (Configuración → Respaldos; `Backup:Cloud`) y la función de licencia `CloudBackup`, el servidor de la tienda sube el respaldo programado más nuevo **tal como está en el disco**: ya va cifrado con la frase del dueño (AES-256-GCM), que nunca sale de la PC, así que la nube no puede leerlo. Sube por el canal firmado del nodo (`POST /api/nodes/backups/{archivo}`, firma ECDSA sobre el hash del archivo), lo revisa cada 15 minutos (`Backup:CloudCheckMinutes`) y tras un fallo espera 60 (`Backup:CloudRetryMinutes`); uno que la nube rechazó (cuota, tamaño, licencia) espera al respaldo siguiente. El resultado se ve en Configuración → Respaldos ("Copia en la nube") y en Salud.

- **Dónde:** contenedor privado `store-backups` de la misma cuenta del archivo frío (`{negocio}/{tienda}/{archivo}`, Hot: casi todos viven una semana y Cool cobra 30 días mínimo). Lo crea `main.bicep`; la API entra con su identidad administrada (mismo rol Storage Blob Data Contributor), sin llaves. `CloudBackup__Blob__Container` en la configuración de la API; la cuenta es `Archive__Blob__ServiceUri` (o `CloudBackup:Blob:ServiceUri`). Sin Azure, `CloudBackup:Folder`; sin ninguno, la nube no guarda respaldos (404).
- **Límites:** `CloudBackup:MaxMegabytes` (1,024) por archivo, también como límite del cuerpo de la petición; `CloudBackup:UploadsPerHourPerNode` (4); el medidor mensual `CloudBackups` (Estándar 62, Esencial 35, Varias sucursales 124; `docs/cuotas.md`); solo con la licencia vigente o la prueba (`LicenseCheck`, como la lectura de facturas). Solo acepta nombres de respaldos programados y archivos que empiezan como un respaldo cifrado.
- **Cuántos guarda:** por tienda, el más nuevo de cada uno de los últimos 7 días con respaldo y el más nuevo de cada una de las 4 semanas anteriores (`CloudBackup:KeepDaily`, `KeepWeekly`); borra el resto al recibir uno nuevo. Una regla de ciclo de vida borra lo que tenga más de 400 días, por si acaso (R12 #8: una tienda que dejó de subir, por ejemplo tras robarse la PC, conserva sus últimos respaldos; la API nunca borra el más nuevo, y si un archivo ya no está, la descarga lo dice y la fila se borra).
- **Descargar:** solo un Dueño, en el panel (Tiendas → "Respaldos en la nube" → Descargar, también sin cajas conectadas). R12 #9: el panel pide con su sesión un enlace que dura 2 minutos (`POST /api/stores/{tienda}/backups/{id}/link`, máximo `CloudBackup:DownloadsPerHourPerUser` = 10 por hora) y el navegador descarga solo (`GET /api/backups/download/{token}`), sin cargar el archivo en la página. Queda quién y cuándo en la fila y en el registro (solo ids, sin correos). Para restaurarlo hace falta la frase.
- **Costo:** Hot ~0.02/GB-mes. Una tienda con un respaldo de 300 MB guarda ~11 copias (~3.3 GB): ~0.07 USD al mes, más centavos de escrituras. La subida entra gratis; la descarga sale a ~0.087/GB.
- **Atajo:** la API recibe el archivo completo (la firma del nodo cubre el cuerpo, que se guarda en un archivo temporal del App Service) antes de mandarlo a Blob. Con respaldos de más de 1 GB conviene subir directo a Blob con un SAS de escritura corto.

### Prueba de humo (2026-10-15)

- `/health` → 200 `Healthy`. `/health/ready` → 200 `Healthy` (con el token de un administrador de la plataforma, R11).
- Registro de dueño, inicio de sesión, `/api/me`, alta de tienda y código de conexión de 8 dígitos: OK.
- `/api/assistant/ask` con "¿Cuánto vendí hoy?" → 200 en ~8 s, respuesta en español (RD$ 0.00, todavía sin datos).
- Panel: carga en la URL de la Static Web App, una ruta profunda (`/sales`) responde 200 y el *preflight* de CORS a la API devuelve el origen del panel.
- La cuenta de prueba está en `src/Client/Abastalia.Client.Web/appsettings.Development.local.json` (clave `AzureTestOwner`, ignorado por git).

### Conectar una tienda local a esta nube

En la caja, el ajuste `Cloud:Url` (en `appsettings.json` del host local o con la variable `Cloud__Url`) tiene que ser `https://app-abastalia-prod-5qsy5n.azurewebsites.net`; el instalador ya lo trae (`deploy/installer/store-settings.ps1`, TK-251). Después, en **Configuración → Nube**, se pega el código de conexión que da el panel. Los valores por defecto del repositorio no cambian.

## Antes de desplegar

1. Instalar la CLI de Azure y entrar: `az login`.
2. Elegir el modelo más barato con *function calling* en la región:

   ```
   az cognitiveservices model list -l eastus2 --query "[?kind=='OpenAI'].{modelo:model.name, version:model.version, skus:model.skus[].name}" -o table
   ```

   Hoy el parámetro trae `gpt-5-nano` (`2025-08-07`) con SKU `GlobalStandard` y `openAiApiVersion` `2025-04-01-preview`. Si ya no está o hay uno más barato, se cambia en `main.parameters.json`. El código no usa `temperature` ni `max_tokens`, así que sirve un modelo de razonamiento.
   Ver también la cuota: `az cognitiveservices usage list -l eastus2 --query "[?contains(name.value,'nano')]"`.
3. Ver si la región acepta servidores SQL nuevos (si no, cambiar `sqlLocation`):

   ```
   az rest --method get --url "https://management.azure.com/subscriptions/<suscripción>/providers/Microsoft.Sql/locations/eastus2/capabilities?api-version=2021-11-01" --query "{status:status,reason:reason}"
   ```
4. El administrador de Entra de SQL se pasa en la línea de comandos (no se guarda en git): `sqlAdminLogin` (correo) y `sqlAdminObjectId` (`az ad signed-in-user show --query id -o tsv`).

## Desplegar

```
az group create -n rg-abastalia-prod -l eastus2
az deployment group create -g rg-abastalia-prod -f deploy/azure/main.bicep -p deploy/azure/main.parameters.json `
  -p sqlAdminLogin=<correo> sqlAdminObjectId=<object id>
```

Las salidas dan el nombre de la Web App, su identidad, el servidor SQL y la URL del panel.

## Después del primer despliegue (una sola vez)

1. **Dar acceso a la API en la base.** Conectado a la base `abastalia` como el administrador de Entra (por ejemplo, desde el editor de consultas del portal):

   ```sql
   CREATE USER [app-abastalia-prod-xxxxxx] FROM EXTERNAL PROVIDER;  -- el nombre de la Web App
   ALTER ROLE db_owner ADD MEMBER [app-abastalia-prod-xxxxxx];
   ```

   `db_owner` porque la API aplica sus migraciones al arrancar (`InitialCloud` y las siguientes).

   Sin portal: abrir una regla temporal con la IP de la PC (`az sql server firewall-rule create -g rg-abastalia-prod -s <servidor> -n tmp-pc --start-ip-address <ip> --end-ip-address <ip>`), conectarse con un token de Entra (`az account get-access-token --resource https://database.windows.net/`) desde cualquier cliente que acepte *access token* (por ejemplo un script de `Microsoft.Data.SqlClient` con `SqlConnection.AccessToken`), correr las dos líneas y borrar la regla (`az sql server firewall-rule delete … -n tmp-pc`).
2. **Publicar la API:**

   ```
   dotnet publish src/Hosts/Abastalia.Host.Cloud -c Release -o publish/cloud
   Compress-Archive publish/cloud/* publish/cloud.zip -Force
   az webapp deploy -g rg-abastalia-prod -n <nombre de la Web App> --src-path publish/cloud.zip --type zip
   ```

   Al arrancar crea las tablas. Si la base estaba en pausa, el primer arranque tarda hasta un minuto más.
3. **Publicar el panel** en la Static Web App con su token de despliegue (`az staticwebapp secrets list -n <nombre del panel>`), como dice la sección siguiente. El origen del panel ya está permitido en CORS (`Panel__Origins__0`). Si se le pone un dominio propio, se agrega como `Panel__Origins__1`.
4. Revisar `https://<api>/health/ready` con el token de un administrador de la plataforma: tiene que responder `Healthy`.
5. **Equipo de Abastalia (consola Plataforma):** cada cuenta se registra en el panel y luego `dotnet run --project src/Hosts/Abastalia.Host.Cloud -- grant-platform-admin <correo>` contra la base de producción. La lista `Platform__Admins` ya no da el rol (R8 #1): si estaba en la configuración de la Web App, se borra.
6. **Un dueño perdió el teléfono y los códigos de respaldo (TK-298).** Con un código de respaldo él mismo entra y, en Seguridad, toca «Cambiar de teléfono»; no hace falta el equipo. Si perdió también los códigos: primero se comprueba que quien pide es el dueño (llamada al teléfono del negocio que ya teníamos, el correo de la cuenta respondiendo desde su buzón y, si hay dudas, la cédula del dueño en una videollamada; nunca solo por WhatsApp desde un número nuevo). Luego `dotnet run --project src/Hosts/Abastalia.Host.Cloud -- reset-2fa <correo> --reason "<quién pidió y cómo se comprobó>"` contra la base de producción: quita la verificación en dos pasos, borra la clave y los códigos, cierra todas sus sesiones, queda en la auditoría de la plataforma con el motivo y le avisa por correo a la cuenta (R12 #5). El dueño entra con su contraseña y la activa de nuevo. Para una cuenta del equipo (administrador de la plataforma) hace falta además `--force`, y lo corre otra persona del equipo. Los códigos de respaldo se guardan como hash y la clave del autenticador cifrada con Data Protection (R12 #3): si se perdiera el anillo de claves de la API, esas cuentas necesitan este mismo `reset-2fa`.

## Publicar el sitio y el panel (Static Web App)

El panel (`src/Cloud/panel`, React + Vite) es un sitio estático: se compila con la URL de la API y se sube la carpeta `dist`. Necesita Node 18 o más nuevo (Vite 6); se recomienda Node 22 LTS.

```
cd src/Cloud/panel
npm ci
$env:VITE_API_URL = "https://app-abastalia-prod-5qsy5n.azurewebsites.net"
npm run build
$env:SWA_CLI_DEPLOYMENT_TOKEN = az staticwebapp secrets list -n stapp-abastalia-prod -g rg-abastalia-prod --query properties.apiKey -o tsv
npx @azure/static-web-apps-cli deploy dist --env production
```

El token va en una variable de entorno, nunca en el repositorio. El panel importa el CSS del sistema de diseño desde `src/Client/Abastalia.Escuadra/wwwroot`, así que si se compila desde una copia (por ejemplo `git archive HEAD src/Cloud/panel src/Client/Abastalia.Escuadra/wwwroot`) hay que incluir esa carpeta.

### Sitio con código de acceso antes del lanzamiento (TK-285)

Hasta el lanzamiento, el sitio público puede quedar detrás de un código de acceso. El código lo da el dueño por fuera del repositorio; se pasa al compilar y solo su SHA-256 entra al build:

```
$env:SITE_GATE_CODE = "<código que dio el dueño>"
$env:VITE_API_URL = "https://app-abastalia-prod-5qsy5n.azurewebsites.net"
npm run build
Remove-Item Env:SITE_GATE_CODE
```

(También se puede pasar directamente el hash en `VITE_SITE_GATE`, en hexadecimal; `SITE_GATE_CODE` tiene prioridad.) La consola dice `Prerendered 13 public pages behind the site gate`. Con el código puesto:

- Cada página pública (`index.html`, `precios.html`…, `404.html`) trae solo la pantalla "Sitio en preparación" (logo, campo "Código de acceso" y "Entrar"), con título "Abastalia", descripción genérica y `noindex, nofollow`; sin canonical, `og:` ni JSON-LD.
- `robots.txt` queda en `Disallow: /` y no se genera `sitemap.xml`.
- Quien escribe el código correcto (se compara el SHA-256 en el navegador) ve el sitio; el navegador lo recuerda (`localStorage`). Un código equivocado muestra el error al lado del campo; desde el cuarto intento cada uno espera 1 s.
- El panel y lo que abren los correos (`/app`, `/c/…`, `/invite`, `/unsubscribe`) no tienen código: tienen su propio inicio de sesión.

Es una barrera del lado del navegador: aleja buscadores y curiosos, pero el texto del sitio sigue dentro del JavaScript que se descarga. Cambiar el código obliga a todos a escribirlo de nuevo.

**Al lanzar:** compilar sin `SITE_GATE_CODE` ni `VITE_SITE_GATE` (cerrar la terminal o `Remove-Item Env:SITE_GATE_CODE, Env:VITE_SITE_GATE`) y volver a publicar. El build vuelve a ser el de siempre (contenido, `robots.txt` con el sitemap, `sitemap.xml`). CI y Lighthouse compilan siempre sin código.

### Una sola app: sitio público y panel (TK-228)

La misma Static Web App sirve el sitio de Abastalia y el panel del dueño, porque son una sola app React:

| Dirección | Qué es | Sesión |
|---|---|---|
| `/`, `/carniceria`, `/colmado`, `/deli`, `/e-cf`, `/certificacion`, `/precios`, `/novedades` | Sitio público (`src/pages/site/`) | No |
| `/c/{tienda}` | Catálogo público de una tienda (TK-195) | No |
| `/invite#token` (y la vieja `/invite/{token}`), `/unsubscribe#token` | Invitación y baja de avisos (enlaces de los correos) | No |
| `/app`, `/app/sales`, `/app/shifts/{id}`, `/app/platform`… | Panel del dueño; sin sesión muestra Entrar / Crear cuenta | Sí |
| `/sales`, `/shifts/…`, `/stores`, `/notifications`, `/members`, `/platform/…`… | Direcciones viejas del panel: redirigen a `/app/…` | — |

`npm run build` compila la app y después **pre-renderiza** las páginas públicas (`src/prerender.tsx` + `scripts/prerender.mjs`): cada una queda en `dist` como HTML de verdad (`index.html`, `precios.html`…, más `404.html`) con su `<title>`, descripción, `canonical`, etiquetas `og:`/`twitter:` (imagen `/og/abastalia.png`, 1200×630) y JSON-LD (Organization y SoftwareApplication en el inicio, BreadcrumbList en las demás, FAQPage donde hay preguntas frecuentes), para buscadores y vistas previas de enlaces; en el navegador, React hidrata esa página. El bundle principal solo trae el sitio; el panel, el catálogo, la invitación, la baja de avisos y las páginas de los enlaces de correo se descargan aparte al abrir su dirección (TK-278). También escribe `dist/sitemap.xml` (todas las páginas públicas, `lastmod` = fecha de la compilación), al que apunta `robots.txt`. La app vacía queda en `dist/shell.html`, con `noindex`. `src/prerender.test.tsx` falla si una página pública no tiene título (≤ 60), descripción (140–160), canonical o lugar en el sitemap, o si se repiten. El job "Sitio (Lighthouse)" de CI (`lighthouserc.json`, `@lhci/cli` 0.15.1) mide las páginas en móvil: SEO 100 y accesibilidad ≥ 0.95 frenan el build; rendimiento ≥ 0.9 y LCP < 2.5 s avisan; CLS < 0.1 frena. Localmente: `npm run build` y `npx @lhci/cli@0.15.1 autorun`. El script arma `staticwebapp.config.json` (`scripts/swa-config.mjs`, probado en `swa-config.test.mjs`): cada ruta pública sirve su HTML; las direcciones de la app (`/app` y `/app/…`, `/c/…`, `/invite` y `/invite/…`, `/unsubscribe` y las viejas del panel con lo que sigue, como `/shifts/…` o `/platform/…`) van a `shell.html`, así que recargar o abrir un enlace de un correo funciona; cualquier otra dirección responde **404** con `404.html` ("No encontramos esa página."). No hay `navigationFallback`: con él, toda dirección desconocida respondería 200. Una ruta nueva de la app fuera de esas tiene que agregarse en `swa-config.mjs` (o en `movedToApp` de `App.tsx` si es una dirección vieja del panel), si no, al recargarla da 404. `robots.txt` excluye `/app`.

Las rutas viven en `src/App.tsx`; los textos del sitio en `src/pages/site/SitePages.tsx` y sus ajustes (dominio, WhatsApp, precios) en `src/pages/site/config.ts`.

**Pendiente del dueño antes de anunciar el sitio:** los precios definitivos de cada plan (TK-203; hoy el sitio muestra los provisionales, TK-293), el precio de la instalación y del acompañamiento en la certificación (TK-219), el número de WhatsApp. Todo va en `config.ts` (`url` = https://abastalia.com sin barra final, de ahí salen `canonical`, `og:url`, `og:image`, JSON-LD y el sitemap; `whatsapp` solo dígitos con el código de país, `prices` con el texto a mostrar): con `whatsapp`, los botones "Hablemos" abren WhatsApp. Las novedades se escriben a mano en `SitePages.tsx` (en lenguaje del cliente, no los textos del backlog). Que un contador revise "¿Ya te toca el e-CF?" antes de anunciarla (TK-198).

**Dominio propio (después):** un solo dominio para sitio y panel. Se agrega con `az staticwebapp hostname set -n stapp-abastalia-prod --hostname <dominio>` después de crear el CNAME (o el registro TXT para el dominio raíz) en el DNS; Azure emite el certificado solo. Luego: `url` en `config.ts`, `Panel__Origins__1` en la API (CORS) y `Panel:Url` para los enlaces de los correos.

## Volver a publicar la API

```
dotnet publish src/Hosts/Abastalia.Host.Cloud -c Release -o publish/cloud
Compress-Archive publish/cloud/* publish/cloud.zip -Force
az webapp deploy -g rg-abastalia-prod -n app-abastalia-prod-5qsy5n --src-path publish/cloud.zip --type zip
```

Las migraciones nuevas se aplican solas al arrancar. Después, revisar `/health/ready`. Si cambia la infraestructura, se vuelve a correr el `az deployment group create` de arriba: es idempotente.

Las configuraciones de la Web App están en un recurso aparte (`apiSettings`) que se aplica después del rol de Key Vault: App Service resuelve las referencias `@Microsoft.KeyVault(...)` una vez y guarda el "acceso denegado" hasta un día. Si el asistente responde 503 `Assistant.Unavailable` justo después de crear todo, revisar `az rest --method get --url "https://management.azure.com/subscriptions/<suscripción>/resourceGroups/rg-abastalia-prod/providers/Microsoft.Web/sites/app-abastalia-prod-5qsy5n/config/configreferences/appsettings?api-version=2022-03-01"` (tiene que decir `Resolved`) y reiniciar la Web App.

## Borrar todo

```
az group delete -n rg-abastalia-prod --yes
az keyvault purge -n kv-abastalia-prod-5qsy5n          # el Key Vault queda 30 días en borrado temporal
az cognitiveservices account purge -g rg-abastalia-prod -n oai-abastalia-prod-5qsy5n -l eastus2
```

Borra la base con todos los datos de las tiendas: no tiene vuelta atrás.

## Configuración de la API

| Clave | Qué es |
|---|---|
| `Database:Provider` | `SqlServer` (Azure SQL o SQL Express) o `Sqlite` (pruebas). |
| `Database:ConnectionString` | En Azure, con `Authentication=Active Directory Managed Identity`. En desarrollo, `.\SQLEXPRESS` (`appsettings.Development.json`). |
| `Panel:Origins` | Orígenes del panel permitidos por CORS. |
| `AzureOpenAI:Endpoint`, `Deployment`, `ApiVersion` (y `ApiKey` solo en desarrollo) | El asistente. Sin `ApiKey` entra con la identidad administrada (R11). Si falta `Endpoint` o `Deployment`, `/api/assistant/ask` responde 503 `Assistant.NotConfigured`. |
| `Ingest:ApplyInBackground` | `false` solo en pruebas. |
| `Nodes:RegisterPerMinute` | Intentos de conectar una tienda por minuto y por IP (10). |
| `Email:Provider` | `AzureCommunicationServices` envía correos de verdad (avisos de TK-222 e invitaciones). Sin él, los correos solo quedan en el log y el panel muestra el enlace de la invitación. |
| `Email:Endpoint`, `Email:Sender` | El recurso de Azure Communication Services (`https://<nombre>.communication.azure.com`, con la identidad administrada) y el remitente verificado (`DoNotReply@<dominio>`). |
| `Email:ConnectionString` | Alternativa a `Email:Endpoint` (fuera de Azure): es un secreto, va en user-secrets o en una referencia a Key Vault, nunca en el repositorio. |

### Correo (TK-222), encendido en producción desde TK-241 (2026-10-16)

Los avisos por correo (rechazos de la DGII, certificado por vencer, comprobantes sin enviar por más de 24 horas, avance de la certificación) salen
del latido y de los eventos que las tiendas ya suben: se revisan justo después de aplicar un lote, nunca con un temporizador, para no despertar la
base serverless. Uno por condición, tienda y día. Cada miembro los elige en **Avisos** del panel; el enlace del correo da de baja sin entrar.
También van por correo las invitaciones del Equipo (solo por correo, R8 #1) y los de confirmar la cuenta y cambiar la contraseña (TK-230).

Lo que hay en `rg-abastalia-prod` (creado con az el 2026-10-16, TK-241):

| Recurso | Qué es |
|---|---|
| `ecs-abastalia-prod` (Email Communication Service, datos en Estados Unidos) | Con el dominio administrado de Azure `AzureManagedDomain` = `03a51e41-8b38-4672-bdb5-8ae7432a35f7.azurecomm.net` (gratis, sin DNS propio; seguimiento de lectura apagado). Remitente `DoNotReply`, nombre visible "Abastalia". |
| `acs-abastalia-prod` (Communication Services, datos en Estados Unidos) | Enlazado a ese dominio. Endpoint `https://acs-abastalia-prod.unitedstates.communication.azure.com`. La identidad administrada de `app-abastalia-prod-5qsy5n` tiene "Communication and Email Service Owner" sobre él. |

Configuración de la Web App: `Email__Provider=AzureCommunicationServices`, `Email__Endpoint=https://acs-abastalia-prod.unitedstates.communication.azure.com`,
`Email__Sender=DoNotReply@03a51e41-8b38-4672-bdb5-8ae7432a35f7.azurecomm.net`. Los mismos valores están en `main.parameters.json`
(`emailEndpoint`, `emailSender`, `emailResourceName`); con `emailResourceName` el Bicep también da el rol a la API.

Cómo se creó (por si hay que repetirlo en otro grupo; en PowerShell, porque Git Bash cambia las rutas `/subscriptions/...`):

```powershell
az extension add --name communication
az communication email create -n ecs-abastalia-prod -g rg-abastalia-prod --location global --data-location "United States"
az communication email domain create --email-service-name ecs-abastalia-prod -g rg-abastalia-prod -n AzureManagedDomain --location global `
  --domain-management AzureManaged --user-engmnt-tracking Disabled
az communication create -n acs-abastalia-prod -g rg-abastalia-prod --location global --data-location "United States" `
  --linked-domains $(az communication email domain show --email-service-name ecs-abastalia-prod -g rg-abastalia-prod -n AzureManagedDomain --query id -o tsv)
az communication email domain sender-username update --email-service-name ecs-abastalia-prod -g rg-abastalia-prod --domain-name AzureManagedDomain -n donotreply --display-name Abastalia
az role assignment create --assignee-object-id <apiPrincipalId> --assignee-principal-type ServicePrincipal `
  --role "Communication and Email Service Owner" --scope $(az communication show -n acs-abastalia-prod -g rg-abastalia-prod --query id -o tsv)
az webapp config appsettings set -n app-abastalia-prod-5qsy5n -g rg-abastalia-prod --settings Email__Provider=AzureCommunicationServices `
  Email__Endpoint=https://acs-abastalia-prod.unitedstates.communication.azure.com Email__Sender=DoNotReply@<dominio>.azurecomm.net
```

Prueba: un correo a la cuenta del dueño con `az communication email send --sender DoNotReply@<dominio> --to <correo> ...` (lee la cadena de
conexión de `AZURE_COMMUNICATION_CONNECTION_STRING`, que se toma con `az communication list-key` solo en esa sesión y se borra después): estado
`Succeeded`. El dominio administrado de Azure tiene un límite bajo de envíos por hora; para volumen real se conecta un dominio propio.

Costo: unos 0.00025 USD por correo; sin tráfico, nada.
WhatsApp queda como complemento pagado futuro (sin código todavía).

### Alertas (TK-241)

El Bicep crea cuatro alertas de Azure Monitor; con `alertEmail` también un grupo de acción `ag-abastalia-prod` que avisa por correo (vacío = las alertas
existen, se ven en el portal y no avisan a nadie):

| Alerta | Señal |
|---|---|
| `alert-abastalia-prod-5xx` | Métrica `Http5xx` de la Web App: más de 5 en 15 minutos. |
| `alert-abastalia-prod-health` | Métrica `HealthCheckStatus` (el chequeo de `/health` de App Service) por debajo de 100 en 5 minutos. |
| `alert-abastalia-prod-inbox` | Registro: un evento de una tienda que el inbox no pudo aplicar (`could not be applied`) o una corrida fallida del aplicador. |
| `alert-abastalia-prod-fair-use` | Registro (TK-286): un negocio pasó el uso justo de firmas del receptor del mes (`passed its fair use of receiver signatures`; sigue firmando). Se escribe una vez por negocio y mes; el id del negocio está en la línea del registro: revisar su plan. Cada hora. |

A Application Insights solo llegan las solicitudes (Serilog escribe en la consola), así que el Bicep manda los registros de consola de la Web App
(`AppServiceConsoleLogs`) al área de trabajo `log-abastalia-prod` y la alerta del inbox los consulta. Validado con `az bicep build`; se aplica con el
próximo `az deployment group create`.

### Certificados e-CF en Key Vault: prueba real (TK-151, 2026-10-16)

Con el código de la nube (`KeyVaultFeVault`, el mismo archivo enlazado en un programa de prueba) y la sesión de az del dueño, contra
`kv-abastalia-prod-5qsy5n`, con un .p12 autofirmado de usar y tirar: importar como no exportable (política `exportable=False`, RSA) ✅, firmar RS256 un
SHA-256 con la llave del vault y verificarla con el certificado ✅, borrar ✅ (después da 404 y la llave ya no firma). El certificado
`tk151-test-20261016042431` quedó en borrado suave hasta el 2026-11-15 (la protección contra purga no deja borrarlo antes). Para la prueba se le
dieron al usuario "Key Vault Certificates Officer" y "Key Vault Crypto User" en el vault y se quitaron al terminar.

### Receptor e-CF: la tienda firma (TK-246, ADR-0011)

Desde TK-246 el modo por defecto del receptor es **StoreSigned**: la nube no guarda certificados de las tiendas y no llama a la DGII; cada acuse lo
firma la PC de la tienda por el canal del nodo. La copia en Key Vault (TK-151, **CloudCustody**) sigue en el código pero apagada en producción:

| Configuración | Producción | Qué hace |
|---|---|---|
| `Fe__DefaultSigningMode` | `StoreSigned` | El modo que ofrece primero la pantalla de la tienda. |
| `Fe__AllowCloudCustody` | `false` | Con false la API se niega a activar CloudCustody (`Fe.CloudCustodyUnavailable`). Se enciende solo con las condiciones de ADR-0011. |
| `Fe__SignTimeoutSeconds` | 10 (por defecto) | Cuánto espera el receptor la firma de la tienda antes de responder 503 con `Retry-After`. |
| `Fe__SignPollSeconds` | 25 (por defecto) | Cuánto dura el sondeo largo de la tienda (menor que el tiempo de espera del cliente de la tienda, 60 s). |

Los roles "Key Vault Certificates Officer" y "Key Vault Crypto User" de la API se quedan en el Bicep para CloudCustody. **No hay que borrar nada en
Azure:** el certificado de la prueba ya está en borrado suave y el vault tiene protección contra purga. La cola de solicitudes de firma vive en memoria:
con más de una instancia de App Service el sondeo de la tienda y la petición del suplidor pueden caer en instancias distintas (igual que los nonces
de ADR-0008); mientras haya una sola instancia, no cambia nada.

## Desarrollo local

`dotnet run --project src/Hosts/Abastalia.Host.Cloud` levanta la API en `http://localhost:5100` contra `.\SQLEXPRESS` (base `AbastaliaCloud`, se crea sola). El perfil `Sqlite` usa un archivo local. En Development, la referencia de la API está en `/scalar`.

## Pendiente

- Leer Azure OpenAI con la identidad administrada en vez de la clave.
- Un dominio propio para la API y para el sitio con el panel.
- Un dominio propio para el correo (el administrado de Azure limita los envíos por hora).
