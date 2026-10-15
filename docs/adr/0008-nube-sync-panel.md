# ADR-0008: Nube V1: sincronización, réplica, panel del dueño, asistente y relevo e-CF

- Fecha: 2026-10-15
- Estado: aceptada

## Contexto

La tienda funciona sola contra su PC principal. El dueño quiere ver su negocio desde fuera (panel web), preguntarle a un asistente ("¿cuánto vendí hoy?", "¿qué cajera tuvo faltante?") y tener una copia de sus datos fuera de la tienda. La arquitectura (`arquitectura-v1-vsa.md` §5) ya preveía un outbox con `UploadedUtc`, un inbox en la nube y una réplica para el panel y la IA. La DGII sigue en espera (ver `pos-ecf-deferred`): el relevo e-CF existe, pero detrás de un proveedor simulado.

## Decisión

**Piezas**

| Pieza | Dónde | Qué hace |
|---|---|---|
| `Toki.Sync` (módulo) | PC principal (host local) | Conecta la tienda a la nube con un código; un `BackgroundService` sube los eventos del outbox por lotes y marca `UploadedUtc` solo con los ids confirmados. Nunca da un evento por perdido. |
| `Toki.Host.Cloud` (API .NET 10) | Azure App Service | Registro de tiendas, ingesta (`/ingest/v1/events`), inbox idempotente, proyectores a la réplica, API del panel, asistente y relevo e-CF. |
| `src/Cloud/panel` (React + Vite + TypeScript) | Azure Static Web Apps | Panel del dueño: resumen, ventas, cuadres, productos más vendidos y el asistente. En español; usa los mismos tokens de color de Escuadra. |
| Azure SQL (base serverless) | Azure | Esquemas `cloud` (tenants, tiendas, usuarios del panel), `ingest` (inbox, versiones) y `replica` (proyecciones por `TenantId`). |
| Azure OpenAI | Azure | El modelo más económico disponible en la región con *function calling*. El asistente solo lee la réplica, siempre filtrada por el tenant del usuario. |

**Conectar una tienda (shortcut del mTLS de §8.1)**
1. El dueño crea su cuenta en el panel y su tienda, y el panel le muestra un **código de conexión** (8 dígitos, 30 minutos, de un solo uso).
2. En la caja, en Configuración → Nube, escribe el código. La PC principal llama a `POST /api/nodes/register { code, nodeName }` y recibe `{ tenantId, storeId, nodeId, nodeSecret }`.
3. El secreto se guarda cifrado con Data Protection en la base local. La nube solo guarda su hash SHA-256.
4. Cada lote va con `Authorization: Node <nodeId>.<nodeSecret>`. El `TenantId` y la tienda salen siempre de esa identidad, nunca del cuerpo.

`shortcut:` secreto compartido por nodo en vez de certificado de cliente o `private_key_jwt`. Se cambia antes de tener más de un cliente pagando.

**Ingesta**
- `POST /ingest/v1/events` con `{ events: [{ id, type, aggregateId, occurredUtc, payload }] }`, hasta 200 por lote. Responde `{ accepted: [ids] }`, incluidos los duplicados ya recibidos. Recibir es barato; aplicar va aparte.
- Un worker de la nube aplica cada evento de `ingest.Inbox` con su proyector en la misma transacción que marca `AppliedUtc`. Un tipo desconocido queda guardado sin aplicar: se proyecta cuando exista su proyector.
- Las proyecciones hacen *upsert* por id; nunca suman sobre lo ya aplicado.
- Lo que manda el nodo: `type` es el nombre completo del evento en .NET (por ejemplo `Toki.Sales.Contracts.SaleRecordedV1`), `payload` es el JSON del evento como objeto (camelCase, enums como texto), no como texto escapado, y `occurredUtc` va en ISO 8601 con `Z`. El cuerpo va comprimido (`Content-Encoding: gzip`), así que la nube lo descomprime (`UseRequestDecompression`). Solo se marca como subido lo que viene en `accepted`; una respuesta que no sea 200 deja todo el lote pendiente.

**Réplica (V1)**: ventas (cabecera, líneas, pagos), anulaciones, devoluciones, turnos cerrados (Z), movimientos de inventario, cambios de precio y documentos fiscales. Toda tabla lleva `TenantId` como primera columna de sus índices.

**Panel**: ASP.NET Core Identity con *bearer tokens* (`MapIdentityApi`) para los usuarios del panel. Cada usuario pertenece a un tenant, y todas las consultas filtran por el tenant del token.

**Asistente**: `POST /api/assistant/ask { question, history }`. El modelo recibe herramientas de solo lectura (ventas por día, productos más vendidos, cuadres de turnos, ventas por cajero, mezcla de pagos y documentos fiscales pendientes), cada una con un rango de fechas máximo. Contesta en español con cifras en RD$. Sin datos personales en los logs.

**Relevo e-CF (DGII)**
- El nodo tiene dos proveedores de `IEcfProvider`: `Simulated` (por defecto) y `Relay`, que manda el documento firmado a `POST /relay/v1/ecf` en la nube.
- La nube lo pasa a su propio `IEcfProvider`, que hoy también es el simulado.
- La API key de un proveedor real vive solo en la nube (Key Vault), nunca en la tienda.
- Cuando el dueño decida el proveedor (TK-055), solo cambia la implementación en la nube.

**Contrato del relevo e-CF**

`POST /relay/v1/ecf` con `Authorization: Node <nodeId>.<nodeSecret>`, la misma identidad que usa la ingesta. El tenant sale de esa identidad.

```
→ { "encf": "E320000000123", "signedXmlGzip": "<base64>" }
← 200 { "status": "Accepted" | "AcceptedConditional" | "Rejected" | "Pending" | "Simulated", "trackId": "…" | null, "message": "…" | null }
```

- `signedXmlGzip` es el XML firmado tal como lo guarda el nodo (comprimido con gzip), en base64.
- La nube deduplica por `encf`: si el mismo e-NCF llega otra vez, responde el estado que ya tiene y no lo vuelve a mandar al proveedor.
- `Rejected` es final: el nodo guarda `message` y no reintenta, porque hace falta una nota de crédito. `Pending` quiere decir que la nube todavía no tiene respuesta; el nodo vuelve a preguntar con el mismo e-NCF en su siguiente vuelta.
- Cualquier otra respuesta (que no sea 200, o un `status` desconocido) es un fallo pasajero: el nodo reintenta con espera creciente (1, 2, 4… hasta 60 minutos) dentro del plazo de 72 horas.
- En el nodo, `Fiscal:Provider` elige el proveedor: `Simulated` (por defecto) o `Relay`. Hoy la nube responde `Simulated`.

**Despliegue**
- `deploy/azure/main.bicep` crea un grupo de recursos `rg-toki-<env>` con App Service Linux (B1), Azure SQL serverless con pausa automática, Static Web App (Free), Key Vault, Application Insights y Azure OpenAI con el despliegue del modelo.
- Los secretos se leen con identidad administrada desde Key Vault.

## Consecuencias

- La tienda sigue vendiendo sin internet. El outbox acumula eventos y los sube cuando vuelve la conexión, y el panel muestra la hora del último lote recibido.
- La réplica no es la fuente de verdad: si se pierde, se reconstruye volviendo a subir el outbox, que se conserva.
- El costo de Azure en reposo es de unas decenas de dólares al mes, y sube con el uso del asistente. El modelo se elige por precio.
