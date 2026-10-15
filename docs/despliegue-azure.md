# Despliegue de la nube en Azure

Cómo se crea y se publica la nube de Toki (ADR-0008): la API `Toki.Host.Cloud`, el panel del dueño y lo que necesitan. Todo vive en un solo grupo de recursos por ambiente (`rg-toki-prod`).

## Qué crea `deploy/azure/main.bicep`

| Recurso | Para qué | Costo en reposo |
|---|---|---|
| App Service Linux B1 + Web App .NET 10 | La API (`Toki.Host.Cloud`). Siempre encendida, solo HTTPS, con identidad administrada. | ~13 USD/mes |
| Azure SQL serverless (Gen5, 1 vCore, mínimo 0.5, pausa a los 60 min) | Esquemas `cloud`, `ingest` y `replica`. Solo cuentas de Entra ID: no hay usuario ni contraseña de SQL. | Casi nada mientras está en pausa; se cobra el almacenamiento |
| Key Vault (RBAC) | La clave de Azure OpenAI. La API la lee con su identidad administrada (referencia `@Microsoft.KeyVault(...)`). | Centavos |
| Log Analytics + Application Insights | Logs y trazas de la API (agente automático de App Service). | Según el volumen |
| Static Web App (Free) | El panel del dueño (React). | Gratis |
| Azure OpenAI (S0) + despliegue `chat` | El asistente. El modelo se elige por precio. | Solo por uso |

La API no consulta la base cuando no hay tiendas subiendo datos: el chequeo de salud de App Service (`/health`) no la toca y el aplicador del inbox solo trabaja cuando llega un lote. Así la base se pausa de noche. `/health/ready` sí prueba la base.

## Lo que está desplegado hoy (TK-139, 2026-10-15)

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

### Costo estimado al mes en reposo

| Recurso | USD/mes |
|---|---|
| App Service B1 Linux (0.017/h) | ~12.40 |
| SQL serverless pausada: solo almacenamiento (0.138/GB) | ~0.20–0.50 |
| SQL despierta: 0.626 por vCore-hora, mínimo 0.5 vCore (~0.31/h). Cada arranque de la API la despierta al menos 1 h | ~0.30 por despertar |
| Key Vault, Log Analytics (5 GB/mes gratis), Static Web App Free | ~0 |
| Azure OpenAI | Solo por uso (~0.0005 por pregunta) |
| **Total en reposo** | **~13–15** |

Con tiendas sincronizando todo el día la base no se pausa y sube a ~0.31/h mientras haya actividad (unos 7–8 USD por día con actividad continua). La suscripción MPN trae 150 USD de crédito al mes.

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

## Publicar el panel (Static Web App)

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

`public/staticwebapp.config.json` va dentro de `dist` y manda toda ruta del panel (`/sales`, `/stores`…) a `index.html`, así que recargar la página no da 404.

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

## Desarrollo local

`dotnet run --project src/Hosts/Toki.Host.Cloud` levanta la API en `http://localhost:5100` contra `.\SQLEXPRESS` (base `TokiCloud`, se crea sola). El perfil `Sqlite` usa un archivo local. En Development, la referencia de la API está en `/scalar`.

## Pendiente

- Leer Azure OpenAI con la identidad administrada en vez de la clave.
- Un dominio propio para la API y el panel.
- Alertas de Application Insights (errores 5xx y eventos del inbox que no se pudieron aplicar).
