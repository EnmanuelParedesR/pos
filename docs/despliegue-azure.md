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

## Antes de desplegar

1. Instalar la CLI de Azure y entrar: `az login`.
2. Elegir el modelo más barato con *function calling* en la región:

   ```
   az cognitiveservices model list -l eastus2 --query "[?kind=='OpenAI'].{modelo:model.name, version:model.version, skus:model.skus[].name}" -o table
   ```

   Hoy el parámetro trae `gpt-4.1-nano` (`2025-04-14`) con SKU `GlobalStandard`. Si ya no está o hay uno más barato, se cambia en `main.parameters.json`. El código no usa `temperature` ni `max_tokens`, así que también sirve un modelo de razonamiento.
3. En `main.parameters.json`, poner el usuario o grupo de Entra que administra SQL (`sqlAdminLogin`) y su *object id* (`az ad signed-in-user show --query id -o tsv`).

## Desplegar

```
az group create -n rg-toki-prod -l eastus2
az deployment group create -g rg-toki-prod -f deploy/azure/main.bicep -p deploy/azure/main.parameters.json
```

Las salidas dan el nombre de la Web App, su identidad, el servidor SQL y la URL del panel.

## Después del primer despliegue (una sola vez)

1. **Dar acceso a la API en la base.** Conectado a la base `toki` como el administrador de Entra (por ejemplo, desde el editor de consultas del portal):

   ```sql
   CREATE USER [app-toki-prod-xxxxxx] FROM EXTERNAL PROVIDER;  -- el nombre de la Web App
   ALTER ROLE db_owner ADD MEMBER [app-toki-prod-xxxxxx];
   ```

   `db_owner` porque la API aplica sus migraciones al arrancar (`InitialCloud` y las siguientes).
2. **Publicar la API:**

   ```
   dotnet publish src/Hosts/Toki.Host.Cloud -c Release -o publish/cloud
   Compress-Archive publish/cloud/* publish/cloud.zip -Force
   az webapp deploy -g rg-toki-prod -n <nombre de la Web App> --src-path publish/cloud.zip --type zip
   ```

   Al arrancar crea las tablas. Si la base estaba en pausa, el primer arranque tarda hasta un minuto más.
3. **Publicar el panel** en la Static Web App con su token de despliegue (`az staticwebapp secrets list -n <nombre del panel>`). El origen del panel ya está permitido en CORS (`Panel__Origins__0`). Si se le pone un dominio propio, se agrega como `Panel__Origins__1`.
4. Revisar `https://<api>/health/ready`: tiene que responder `Healthy`.

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
