# Despliegue de la nube en Azure

Cómo se crea y se publica la nube de Toki (ADR-0008): la API `Toki.Host.Cloud`, el panel del dueño y lo que necesitan. Todo vive en un solo grupo de recursos por ambiente (`rg-toki-prod`).

## Qué crea `deploy/azure/main.bicep`

| Recurso | Para qué | Costo en reposo |
|---|---|---|
| App Service Linux B1 + Web App .NET 10 | La API (`Toki.Host.Cloud`). Siempre encendida, solo HTTPS, con identidad administrada. | ~13 USD/mes |
| Azure SQL serverless (Gen5, 1 vCore, mínimo 0.5, pausa a los 60 min) | Esquemas `cloud`, `ingest` y `replica`. Solo cuentas de Entra ID: no hay usuario ni contraseña de SQL. | Casi nada mientras está en pausa; se cobra el almacenamiento |
| Key Vault (RBAC) | La clave de Azure OpenAI. La API la lee con su identidad administrada (referencia `@Microsoft.KeyVault(...)`). | Centavos |
| Log Analytics + Application Insights | Logs y trazas de la API (agente automático de App Service). | Según el volumen |
| Static Web App (Free) | El sitio público y el panel del dueño: una sola app React (TK-228). | Gratis |
| Azure OpenAI (S0) + despliegue `chat` | El asistente. El modelo se elige por precio. | Solo por uso |
| Storage Account (StorageV2, LRS, Cool) + contenedor `ecf-archive` | Archivo frío de los e-CF firmados (TK-207): 10 años, escritura única, pasa a Archive a los 180 días. Solo la identidad de la API entra. | Centavos (ver abajo) |

La API no consulta la base cuando no hay tiendas subiendo datos: el chequeo de salud de App Service (`/health`) no la toca y el aplicador del inbox solo trabaja cuando llega un lote. Así la base se pausa de noche. `/health/ready` sí prueba la base.

## Lo que está desplegado hoy (TK-139, 2026-10-15)

**Redespliegue del 16/10/2026** (main `ac83666`): infraestructura con la cuenta de almacenamiento del archivo frío (sttokiprodjhzq7n, retención inmutable sin bloquear), los roles de Key Vault del receptor y la protección contra purga del Key Vault (irreversible); API con todas las migraciones hasta TK-231 y las raíces de INDOTEL; sitio y panel en la Static Web App (/ sitio, /app panel). /health/ready responde Healthy.

Suscripción **Visual Studio Enterprise Subscription – MPN** (`fc0b2ba5-daf7-43fd-a6a2-7dc6f60b8dff`, tenant `8d025706-…`), grupo **`rg-toki-prod`**.

| Recurso | Nombre | Región | SKU |
|---|---|---|---|
| API (Web App Linux, .NET 10) | `app-toki-prod-jhzq7n` → https://app-toki-prod-jhzq7n.azurewebsites.net | eastus2 | Plan `asp-toki-prod` B1 |
| Panel (Static Web App) | `stapp-toki-prod` → https://black-coast-0594b990f.5.azurestaticapps.net | eastus2 | Free |
| Azure SQL | servidor `sql-toki-prod-e7csjd`, base `toki` | **centralus** | GP_S_Gen5, 1 vCore, mínimo 0.5, pausa a los 60 min, 32 GB máx., respaldo local |
| Azure OpenAI | `oai-toki-prod-jhzq7n`, despliegue `chat` = `gpt-5-nano` (`2025-08-07`) | eastus2 | S0, GlobalStandard, 10K tokens/min |
| Key Vault | `kv-toki-prod-jhzq7n` (secreto `AzureOpenAI--ApiKey`) | eastus2 | Standard, RBAC |
| Log Analytics / Application Insights | `log-toki-prod` / `appi-toki-prod` | eastus2 | Por GB, 30 días |

- **SQL en centralus:** eastus2 y eastus respondieron *"Provisioning is restricted in this region"* para servidores SQL nuevos en esta suscripción. centralus es la región disponible más cercana (parámetro `sqlLocation`). La latencia extra es de unos pocos milisegundos.
- **Firewall de SQL:** solo `AllowAzureServices`. Para conectarse desde una PC se agrega una regla temporal con su IP y se borra al terminar.
- **Acceso de la API a SQL:** usuario contenido `[app-toki-prod-jhzq7n]` (identidad administrada) con `db_owner`. Ya está creado. No hay inicios de sesión de SQL (solo Entra).
- **Migraciones:** las aplica la API al arrancar (`CreateCloudDatabaseOnStartup`). No hay paso aparte.
- **Logs del contenedor:** están activados en el sistema de archivos (`az webapp log config --docker-container-logging filesystem`). Se leen con `az webapp log tail -g rg-toki-prod -n app-toki-prod-jhzq7n`.

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
| **Total en reposo** | **~13–15** |

Cada tienda conectada manda un latido cada 4 horas (`Sync:HeartbeatMinutes`, 240; R8 #21): con uno por hora la base no se pausaba nunca. Con tiendas sincronizando todo el día la base no se pausa y sube a ~0.31/h mientras haya actividad (unos 7–8 USD por día con actividad continua). La suscripción MPN trae 150 USD de crédito al mes.

### Archivo frío de e-CF (TK-207)

La tienda sube cada e-CF firmado (XML, RFCE y respuesta de la DGII) a `POST /fiscal/v1/archive`; la nube lo guarda primero en SQL. Pasados `Archive:HotDays` (90) días, la API mueve el XML al contenedor `ecf-archive` (`{negocio}/{e-NCF}.xml.gz` y `.rfce.xml.gz`, en Cool) y deja la fila en SQL como índice (estado, respuesta de la DGII, dónde quedó). Lo hace después de recibir una subida y como mucho una vez al día, así que nunca despierta la base por su cuenta. El dueño o el contador lo buscan por e-NCF en el panel (Ventas → "Buscar un comprobante electrónico", `GET /api/fiscal/archive/{eNCF}`). Si ya pasó a Archive, la primera búsqueda pide sacarlo (prioridad estándar, hasta 15 horas) y el panel dice que vuelva más tarde.

- **Reglas del contenedor:** retención de 10 años desde la subida (`immutabilityPolicies`, gratis): nada se cambia ni se borra antes. La regla de ciclo de vida pasa a Archive a los 180 días (el mínimo de Archive, sin cargo por borrado temprano) y borra a los 3,660 días. Uno que se sacó de Archive queda en Cool 7 días antes de volver.
- **Bloquear la retención** (irreversible; hacerlo cuando el dueño lo apruebe): `az storage container immutability-policy lock --account-name <cuenta> -c ecf-archive --if-match <etag>` (el `etag` sale de `az storage container immutability-policy show`).
- **Costo** (precios de lista eastus2, LRS, USD; revisar en la calculadora): un e-CF comprimido pesa unos 3–5 KB. Una tienda con 300 facturas al día junta ~0.5 GB por año. Guardar: Cool ~0.01/GB-mes y Archive ~0.002/GB-mes, o sea centavos aun con 10 años acumulados. Lo que más pesa son las operaciones: ~0.10 por 10,000 escrituras en Cool y otro tanto al pasar a Archive, unos 0.10–0.25 USD al mes por tienda con ese volumen. Sacar uno de Archive cuesta ~0.02/GB más la lectura: nada para unos pocos documentos. En SQL, el mismo volumen costaría ~0.138/GB-mes y crecería sin parar.
- **Sin Azure** (desarrollo): `Archive:Folder` guarda los archivos en una carpeta; sin `Archive:Folder` ni `Archive:Blob:ServiceUri` los XML se quedan en SQL.

### Prueba de humo (2026-10-15)

- `/health` → 200 `Healthy`. `/health/ready` → 200 `Healthy`.
- Registro de dueño, inicio de sesión, `/api/me`, alta de tienda y código de conexión de 8 dígitos: OK.
- `/api/assistant/ask` con "¿Cuánto vendí hoy?" → 200 en ~8 s, respuesta en español (RD$ 0.00, todavía sin datos).
- Panel: carga en la URL de la Static Web App, una ruta profunda (`/sales`) responde 200 y el *preflight* de CORS a la API devuelve el origen del panel.
- La cuenta de prueba está en `src/Client/Toki.Client.Web/appsettings.Development.local.json` (clave `AzureTestOwner`, ignorado por git).

### Conectar una tienda local a esta nube

En la caja, el ajuste `Cloud:Url` (en `appsettings.json` del host local o con la variable `Cloud__Url`) tiene que ser `https://app-toki-prod-jhzq7n.azurewebsites.net`. Después, en **Configuración → Nube**, se pega el código de conexión que da el panel. Los valores por defecto del repositorio no cambian.

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
az group create -n rg-toki-prod -l eastus2
az deployment group create -g rg-toki-prod -f deploy/azure/main.bicep -p deploy/azure/main.parameters.json `
  -p sqlAdminLogin=<correo> sqlAdminObjectId=<object id>
```

Las salidas dan el nombre de la Web App, su identidad, el servidor SQL y la URL del panel.

## Después del primer despliegue (una sola vez)

1. **Dar acceso a la API en la base.** Conectado a la base `toki` como el administrador de Entra (por ejemplo, desde el editor de consultas del portal):

   ```sql
   CREATE USER [app-toki-prod-xxxxxx] FROM EXTERNAL PROVIDER;  -- el nombre de la Web App
   ALTER ROLE db_owner ADD MEMBER [app-toki-prod-xxxxxx];
   ```

   `db_owner` porque la API aplica sus migraciones al arrancar (`InitialCloud` y las siguientes).

   Sin portal: abrir una regla temporal con la IP de la PC (`az sql server firewall-rule create -g rg-toki-prod -s <servidor> -n tmp-pc --start-ip-address <ip> --end-ip-address <ip>`), conectarse con un token de Entra (`az account get-access-token --resource https://database.windows.net/`) desde cualquier cliente que acepte *access token* (por ejemplo un script de `Microsoft.Data.SqlClient` con `SqlConnection.AccessToken`), correr las dos líneas y borrar la regla (`az sql server firewall-rule delete … -n tmp-pc`).
2. **Publicar la API:**

   ```
   dotnet publish src/Hosts/Toki.Host.Cloud -c Release -o publish/cloud
   Compress-Archive publish/cloud/* publish/cloud.zip -Force
   az webapp deploy -g rg-toki-prod -n <nombre de la Web App> --src-path publish/cloud.zip --type zip
   ```

   Al arrancar crea las tablas. Si la base estaba en pausa, el primer arranque tarda hasta un minuto más.
3. **Publicar el panel** en la Static Web App con su token de despliegue (`az staticwebapp secrets list -n <nombre del panel>`), como dice la sección siguiente. El origen del panel ya está permitido en CORS (`Panel__Origins__0`). Si se le pone un dominio propio, se agrega como `Panel__Origins__1`.
4. Revisar `https://<api>/health/ready`: tiene que responder `Healthy`.
5. **Equipo de Toki (consola Plataforma):** cada cuenta se registra en el panel y luego `dotnet run --project src/Hosts/Toki.Host.Cloud -- grant-platform-admin <correo>` contra la base de producción. La lista `Platform__Admins` ya no da el rol (R8 #1): si estaba en la configuración de la Web App, se borra.

## Publicar el sitio y el panel (Static Web App)

El panel (`src/Cloud/panel`, React + Vite) es un sitio estático: se compila con la URL de la API y se sube la carpeta `dist`. Necesita Node 18 o más nuevo (Vite 6); se recomienda Node 22 LTS.

```
cd src/Cloud/panel
npm ci
$env:VITE_API_URL = "https://app-toki-prod-jhzq7n.azurewebsites.net"
npm run build
$env:SWA_CLI_DEPLOYMENT_TOKEN = az staticwebapp secrets list -n stapp-toki-prod -g rg-toki-prod --query properties.apiKey -o tsv
npx @azure/static-web-apps-cli deploy dist --env production
```

El token va en una variable de entorno, nunca en el repositorio. El panel importa el CSS del sistema de diseño desde `src/Client/Toki.Escuadra/wwwroot`, así que si se compila desde una copia (por ejemplo `git archive HEAD src/Cloud/panel src/Client/Toki.Escuadra/wwwroot`) hay que incluir esa carpeta.

### Una sola app: sitio público y panel (TK-228)

La misma Static Web App sirve el sitio de Toki y el panel del dueño, porque son una sola app React:

| Dirección | Qué es | Sesión |
|---|---|---|
| `/`, `/carniceria`, `/colmado`, `/deli`, `/e-cf`, `/certificacion`, `/precios`, `/novedades` | Sitio público (`src/pages/site/`) | No |
| `/c/{tienda}` | Catálogo público de una tienda (TK-195) | No |
| `/invite#token` (y la vieja `/invite/{token}`), `/unsubscribe#token` | Invitación y baja de avisos (enlaces de los correos) | No |
| `/app`, `/app/sales`, `/app/shifts/{id}`, `/app/platform`… | Panel del dueño; sin sesión muestra Entrar / Crear cuenta | Sí |
| `/sales`, `/shifts/…`, `/stores`, `/notifications`, `/members`, `/platform/…`… | Direcciones viejas del panel: redirigen a `/app/…` | — |

`npm run build` compila la app y después **pre-renderiza** las páginas públicas (`src/prerender.tsx` + `scripts/prerender.mjs`): cada una queda en `dist` como HTML de verdad (`index.html`, `precios.html`…, más `404.html`) con su `<title>`, descripción y etiquetas `og:`, para buscadores y vistas previas de enlaces; en el navegador, React toma el control. La app vacía queda en `dist/shell.html`. El script arma `staticwebapp.config.json` (`scripts/swa-config.mjs`, probado en `swa-config.test.mjs`): cada ruta pública sirve su HTML; las direcciones de la app (`/app` y `/app/…`, `/c/…`, `/invite` y `/invite/…`, `/unsubscribe` y las viejas del panel con lo que sigue, como `/shifts/…` o `/platform/…`) van a `shell.html`, así que recargar o abrir un enlace de un correo funciona; cualquier otra dirección responde **404** con `404.html` ("No encontramos esa página."). No hay `navigationFallback`: con él, toda dirección desconocida respondería 200. Una ruta nueva de la app fuera de esas tiene que agregarse en `swa-config.mjs` (o en `movedToApp` de `App.tsx` si es una dirección vieja del panel), si no, al recargarla da 404. `robots.txt` excluye `/app`.

Las rutas viven en `src/App.tsx`; los textos del sitio en `src/pages/site/SitePages.tsx` y sus ajustes (dominio, WhatsApp, precios) en `src/pages/site/config.ts`.

**Pendiente del dueño antes de anunciar el sitio:** los precios y los límites de cada plan (TK-203; hoy dicen "Precio por confirmar"), el precio de la instalación y del acompañamiento en la certificación (TK-219), el dominio y el número de WhatsApp. Todo va en `config.ts` (`url` sin barra final, `whatsapp` solo dígitos con el código de país, `prices` con el texto a mostrar): con `url` salen `canonical` y `og:url`; con `whatsapp`, los botones "Hablemos" abren WhatsApp. Las novedades se escriben a mano en `SitePages.tsx` (en lenguaje del cliente, no los textos del backlog). Que un contador revise "¿Ya te toca el e-CF?" antes de anunciarla (TK-198).

**Dominio propio (después):** un solo dominio para sitio y panel. Se agrega con `az staticwebapp hostname set -n stapp-toki-prod --hostname <dominio>` después de crear el CNAME (o el registro TXT para el dominio raíz) en el DNS; Azure emite el certificado solo. Luego: `url` en `config.ts`, `Panel__Origins__1` en la API (CORS) y `Panel:Url` para los enlaces de los correos.

## Volver a publicar la API

```
dotnet publish src/Hosts/Toki.Host.Cloud -c Release -o publish/cloud
Compress-Archive publish/cloud/* publish/cloud.zip -Force
az webapp deploy -g rg-toki-prod -n app-toki-prod-jhzq7n --src-path publish/cloud.zip --type zip
```

Las migraciones nuevas se aplican solas al arrancar. Después, revisar `/health/ready`. Si cambia la infraestructura, se vuelve a correr el `az deployment group create` de arriba: es idempotente.

Las configuraciones de la Web App están en un recurso aparte (`apiSettings`) que se aplica después del rol de Key Vault: App Service resuelve las referencias `@Microsoft.KeyVault(...)` una vez y guarda el "acceso denegado" hasta un día. Si el asistente responde 503 `Assistant.Unavailable` justo después de crear todo, revisar `az rest --method get --url "https://management.azure.com/subscriptions/<suscripción>/resourceGroups/rg-toki-prod/providers/Microsoft.Web/sites/app-toki-prod-jhzq7n/config/configreferences/appsettings?api-version=2022-03-01"` (tiene que decir `Resolved`) y reiniciar la Web App.

## Borrar todo

```
az group delete -n rg-toki-prod --yes
az keyvault purge -n kv-toki-prod-jhzq7n          # el Key Vault queda 30 días en borrado temporal
az cognitiveservices account purge -g rg-toki-prod -n oai-toki-prod-jhzq7n -l eastus2
```

Borra la base con todos los datos de las tiendas: no tiene vuelta atrás.

## Configuración de la API

| Clave | Qué es |
|---|---|
| `Database:Provider` | `SqlServer` (Azure SQL o SQL Express) o `Sqlite` (pruebas). |
| `Database:ConnectionString` | En Azure, con `Authentication=Active Directory Managed Identity`. En desarrollo, `.\SQLEXPRESS` (`appsettings.Development.json`). |
| `Panel:Origins` | Orígenes del panel permitidos por CORS. |
| `AzureOpenAI:Endpoint`, `Deployment`, `ApiKey`, `ApiVersion` | El asistente. Si falta alguno, `/api/assistant/ask` responde 503 `Assistant.NotConfigured`. |
| `Ingest:ApplyInBackground` | `false` solo en pruebas. |
| `Nodes:RegisterPerMinute` | Intentos de conectar una tienda por minuto y por IP (10). |
| `Nodes:AcceptSharedSecret` | TK-241, ADR-0008: `true` (por defecto) mientras haya tiendas conectadas antes de TK-241 con secreto compartido; `false` cuando todas se reconectaron (firman con su llave). |
| `Email:Provider` | `AzureCommunicationServices` envía correos de verdad (avisos de TK-222 e invitaciones). Sin él, los correos solo quedan en el log y el panel muestra el enlace de la invitación. |
| `Email:Endpoint`, `Email:Sender` | El recurso de Azure Communication Services (`https://<nombre>.communication.azure.com`, con la identidad administrada) y el remitente verificado (`DoNotReply@<dominio>`). |
| `Email:ConnectionString` | Alternativa a `Email:Endpoint` (fuera de Azure): es un secreto, va en user-secrets o en una referencia a Key Vault, nunca en el repositorio. |

### Correo (TK-222), encendido en producción desde TK-241 (2026-10-16)

Los avisos por correo (rechazos de la DGII, certificado por vencer, comprobantes sin enviar por más de 24 horas, avance de la certificación) salen
del latido y de los eventos que las tiendas ya suben: se revisan justo después de aplicar un lote, nunca con un temporizador, para no despertar la
base serverless. Uno por condición, tienda y día. Cada miembro los elige en **Avisos** del panel; el enlace del correo da de baja sin entrar.
También van por correo las invitaciones del Equipo (solo por correo, R8 #1) y los de confirmar la cuenta y cambiar la contraseña (TK-230).

Lo que hay en `rg-toki-prod` (creado con az el 2026-10-16, TK-241):

| Recurso | Qué es |
|---|---|
| `ecs-toki-prod` (Email Communication Service, datos en Estados Unidos) | Con el dominio administrado de Azure `AzureManagedDomain` = `b5097b41-7b79-41ea-92e5-b99f21c07125.azurecomm.net` (gratis, sin DNS propio; seguimiento de lectura apagado). Remitente `DoNotReply`, nombre visible "Toki". |
| `acs-toki-prod` (Communication Services, datos en Estados Unidos) | Enlazado a ese dominio. Endpoint `https://acs-toki-prod.unitedstates.communication.azure.com`. La identidad administrada de `app-toki-prod-jhzq7n` tiene "Communication and Email Service Owner" sobre él. |

Configuración de la Web App: `Email__Provider=AzureCommunicationServices`, `Email__Endpoint=https://acs-toki-prod.unitedstates.communication.azure.com`,
`Email__Sender=DoNotReply@b5097b41-7b79-41ea-92e5-b99f21c07125.azurecomm.net`. Los mismos valores están en `main.parameters.json`
(`emailEndpoint`, `emailSender`, `emailResourceName`); con `emailResourceName` el Bicep también da el rol a la API.

Cómo se creó (por si hay que repetirlo en otro grupo; en PowerShell, porque Git Bash cambia las rutas `/subscriptions/...`):

```powershell
az extension add --name communication
az communication email create -n ecs-toki-prod -g rg-toki-prod --location global --data-location "United States"
az communication email domain create --email-service-name ecs-toki-prod -g rg-toki-prod -n AzureManagedDomain --location global `
  --domain-management AzureManaged --user-engmnt-tracking Disabled
az communication create -n acs-toki-prod -g rg-toki-prod --location global --data-location "United States" `
  --linked-domains $(az communication email domain show --email-service-name ecs-toki-prod -g rg-toki-prod -n AzureManagedDomain --query id -o tsv)
az communication email domain sender-username update --email-service-name ecs-toki-prod -g rg-toki-prod --domain-name AzureManagedDomain -n donotreply --display-name Toki
az role assignment create --assignee-object-id <apiPrincipalId> --assignee-principal-type ServicePrincipal `
  --role "Communication and Email Service Owner" --scope $(az communication show -n acs-toki-prod -g rg-toki-prod --query id -o tsv)
az webapp config appsettings set -n app-toki-prod-jhzq7n -g rg-toki-prod --settings Email__Provider=AzureCommunicationServices `
  Email__Endpoint=https://acs-toki-prod.unitedstates.communication.azure.com Email__Sender=DoNotReply@<dominio>.azurecomm.net
```

Prueba: un correo a la cuenta del dueño con `az communication email send --sender DoNotReply@<dominio> --to <correo> ...` (lee la cadena de
conexión de `AZURE_COMMUNICATION_CONNECTION_STRING`, que se toma con `az communication list-key` solo en esa sesión y se borra después): estado
`Succeeded`. El dominio administrado de Azure tiene un límite bajo de envíos por hora; para volumen real se conecta un dominio propio.

Costo: unos 0.00025 USD por correo; sin tráfico, nada.
WhatsApp queda como complemento pagado futuro (solo la interfaz `IWhatsAppSender`).

### Alertas (TK-241)

El Bicep crea tres alertas de Azure Monitor; con `alertEmail` también un grupo de acción `ag-toki-prod` que avisa por correo (vacío = las alertas
existen, se ven en el portal y no avisan a nadie):

| Alerta | Señal |
|---|---|
| `alert-toki-prod-5xx` | Métrica `Http5xx` de la Web App: más de 5 en 15 minutos. |
| `alert-toki-prod-health` | Métrica `HealthCheckStatus` (el chequeo de `/health` de App Service) por debajo de 100 en 5 minutos. |
| `alert-toki-prod-inbox` | Registro: un evento de una tienda que el inbox no pudo aplicar (`could not be applied`) o una corrida fallida del aplicador. |

A Application Insights solo llegan las solicitudes (Serilog escribe en la consola), así que el Bicep manda los registros de consola de la Web App
(`AppServiceConsoleLogs`) al área de trabajo `log-toki-prod` y la alerta del inbox los consulta. Validado con `az bicep build`; se aplica con el
próximo `az deployment group create`.

### Certificados e-CF en Key Vault: prueba real (TK-151, 2026-10-16)

Con el código de la nube (`KeyVaultFeVault`, el mismo archivo enlazado en un programa de prueba) y la sesión de az del dueño, contra
`kv-toki-prod-jhzq7n`, con un .p12 autofirmado de usar y tirar: importar como no exportable (política `exportable=False`, RSA) ✅, firmar RS256 un
SHA-256 con la llave del vault y verificarla con el certificado ✅, borrar ✅ (después da 404 y la llave ya no firma). El certificado
`tk151-test-20261016042431` quedó en borrado suave hasta el 2026-11-15 (la protección contra purga no deja borrarlo antes). Para la prueba se le
dieron al usuario "Key Vault Certificates Officer" y "Key Vault Crypto User" en el vault y se quitaron al terminar.

## Desarrollo local

`dotnet run --project src/Hosts/Toki.Host.Cloud` levanta la API en `http://localhost:5100` contra `.\SQLEXPRESS` (base `TokiCloud`, se crea sola). El perfil `Sqlite` usa un archivo local. En Development, la referencia de la API está en `/scalar`.

## Pendiente

- Leer Azure OpenAI con la identidad administrada en vez de la clave.
- Un dominio propio para la API y para el sitio con el panel.
- Un dominio propio para el correo (el administrado de Azure limita los envíos por hora).
