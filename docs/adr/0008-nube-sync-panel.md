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

**Réplica (V1)**: ventas (cabecera, líneas, pagos), anulaciones, devoluciones con sus líneas, turnos abiertos y cerrados (Z), movimientos de caja, movimientos de inventario, cambios de precio, documentos fiscales, compras y sus anulaciones, cargos a crédito (fiado) y abonos de clientes, el saldo y el nombre de cada cliente, y los nombres de usuarios y cajas. Toda tabla lleva `TenantId` como primera columna de sus índices. `SupplierPaymentRecordedV1` todavía se guarda sin aplicar.

**Eventos que proyecta la nube** (nombre completo en .NET → tabla de `replica`):

| Evento | Tabla | Notas |
|---|---|---|
| `Toki.Sales.Contracts.SaleRecordedV1` | `Sales`, `SaleLines`, `SalePayments` | Ya no se emite; se sigue aplicando (líneas sin unidad). |
| `Toki.Sales.Contracts.SaleRecordedV2` | `Sales`, `SaleLines`, `SalePayments` | V1 + `unit` en cada línea (`Pound` o `Unit`, como en el recibo; TK-180). Los más vendidos traen `unit` (la de la venta más reciente que la tenga). |
| `Toki.Sales.Contracts.SaleVoidedV1` | `SaleVoids` | |
| `Toki.Sales.Contracts.ReturnRecordedV1` | `Returns` | Sin líneas. Ya no se emite; se sigue aplicando por lo que ya se subió. |
| `Toki.Sales.Contracts.ReturnRecordedV2` | `Returns`, `ReturnLines` | V1 + `lines: [{ productId, presentationId, description, quantity, total, itbis, cost }]` (cantidad en la unidad de la línea vendida, `total` con ITBIS, `cost` unitario como en `SaleLineV1`). Los más vendidos restan lo devuelto el día de la devolución. |
| `Toki.CashRegister.Contracts.ShiftOpenedV1` | `OpenedShifts` | |
| `Toki.CashRegister.Contracts.ShiftClosedV1` | `ClosedShifts` | |
| `Toki.CashRegister.Contracts.CashMovementRecordedV1` | `CashMovements` | Por `EventId`: el movimiento no trae id propio. |
| `Toki.CashRegister.Contracts.RegisterChangedV1` | `Registers` | `{ registerId, name }`. Al crear una caja. |
| `Toki.Identity.Contracts.UserProfileChangedV1` | `StoreUsers` | `{ userId, name, active }`. Al crear o editar un usuario y en el asistente inicial. Nunca lleva PIN, código ni permisos. |
| `Toki.Inventory.Contracts.StockMovementRecordedV1` | `StockMovements` | |
| `Toki.Inventory.Contracts.PriceChangedV1` | `PriceChanges` | Por `EventId`. |
| `Toki.Fiscal.Contracts.FiscalDocumentIssuedV1` | `FiscalDocuments` | |
| `Toki.Purchasing.Contracts.PurchasePostedV1` / `PurchaseVoidedV1` | `Purchases`, `PurchaseVoids` | |
| `Toki.Customers.Contracts.OnAccountChargeRecordedV1` | `CustomerCharges` | Uno por venta. |
| `Toki.Customers.Contracts.CustomerPaymentRecordedV1` | `CustomerPayments` | |
| `Toki.Customers.Contracts.CustomerBalanceChangedV1` | `Customers` | `{ customerId, name, debt, creditBalance }` con las cifras de la propia tienda (TK-174): al crear o editar un cliente, y tras cada fiado, abono, abono anulado, venta anulada, devolución a la cuenta o uso del saldo a favor. Gana el más reciente según `occurredUtc`. También va en la foto inicial. |
| `Toki.Sync.Contracts.NodeHeartbeatV1` | `NodeHeartbeats`, `DailyUsage` | El latido del nodo (TK-190): el más reciente por nodo y por tienda y día. Ver abajo. |

- Los nombres (usuarios y cajas) se quedan con el evento más reciente según `occurredUtc`: uno viejo que llegue tarde no deshace un cambio.
- **Foto inicial**: al conectar la tienda (`/api/sync/connect`), en la misma transacción, Identity, CashRegister y Customers escriben en el outbox un `UserProfileChangedV1` por cada usuario, un `RegisterChangedV1` por cada caja y un `CustomerBalanceChangedV1` por cada cliente (`ICloudSnapshot` en `Toki.Sync.Contracts`). Así una tienda que se conecta tarde también manda los nombres que ya existían. Se repite en cada conexión; no hace daño.
- Un evento publicado no se cambia: si su forma cambia, se crea `V2` y la nube proyecta las dos versiones.

**Panel**: ASP.NET Core Identity con *bearer tokens* (`MapIdentityApi`) para los usuarios del panel. Cada usuario pertenece a un tenant, y todas las consultas filtran por el tenant del token.
- Nombres: `GET /api/panel/sales/by-cashier` trae `cashierName`; `GET /api/panel/shifts` trae `registerName` y `cashierName`; los avisos de `GET /api/panel/summary` (`issues`) de tipo `ShiftDifference` traen `cashierName` y `registerName`, y el mensaje incluye el nombre del cajero. Son `null` mientras la tienda no haya mandado el nombre.
- `GET /api/panel/receivables?storeId=&limit=10` → `{ total, customerCount, topDebtors: [{ customerId, storeId, debt, customerName }] }`: el fiado de hoy. Un cliente con `CustomerBalanceChangedV1` debe la cifra de la tienda y sale con su nombre (TK-174), así que un abono anulado ya cuenta. Para los demás (tiendas con una versión anterior) se reconstruye: cargos de ventas no anuladas, menos devoluciones a la cuenta de esas ventas, menos abonos; `customerName` es `null`. `shortcut:` esa reconstrucción no ve los abonos anulados; desaparece cuando todas las tiendas actualicen.

**Latido del nodo (TK-190)**
- Al arrancar y cada hora (`Sync:HeartbeatMinutes`), mientras la tienda está conectada, Sync escribe en el outbox un `NodeHeartbeatV1`: versión, sistema operativo, minutos encendido, cajas con turnos en 30 días, turnos abiertos, eventos por subir, tipo del último error al subir (`http_503`, `unreachable`, `other`), funciones (fiado, proveedor e-CF; balanza e impresora quedan `null` porque las conoce la caja), puesta en marcha (nombre y RNC guardados como sí/no, productos activos) y los totales de ayer y hoy (facturas, monto, cajeros, anulaciones, devuelto).
- Sube con los demás eventos, así que sin internet llega después. Los datos los lee de los reportes de los otros módulos (`ISalesReports`, `ICashRegisterReports`, `ICustomerReports`, `IInventoryReports`, `ISettingsQueries`).
- **Nunca** lleva nombres, PIN, códigos, clientes, RNC ni productos. Las definiciones de las métricas y las notas de privacidad están en `docs/metricas-producto.md`.

**Plataforma: consola del equipo de Toki (TK-189)**
- Rol de plataforma `PlatformAdmin`: un *claim* `platform_role=admin` en el token. Sale de la lista de correos `Platform:Admins` en la configuración de la nube, o se guarda en la cuenta con el comando de consola `dotnet run --project src/Hosts/Toki.Host.Cloud -- grant-platform-admin <correo>` (y `revoke-platform-admin`). La cuenta debe existir (registrada en el panel) y el cambio cuenta desde su siguiente entrada. Nunca se pide desde el panel.
- `/api/platform/*` exige la política `PlatformAdmin` y lee todos los tenants. Cada llamada (también las que fallan) deja una fila en `cloud.PlatformAudit` con quién, ruta y consulta y cuándo; el comando de consola también.
- Clientes: `GET /tenants?search=` (correo, tienda, caja o id), `GET /tenants/{id}` (tiendas, nodos con versión, último envío, pendientes y fallidos, cajas, usuarios, códigos de conexión sin el código, avisos de sincronización y faltantes de 7 días), `GET /stores/{id}` (lista de puesta en marcha, funciones, nodos y los últimos 14 días). Métricas en `/metrics/*` (`docs/metricas-producto.md`).
- Solo lectura, salvo: `POST /tenants/{id}/disable` y `/enable` (`Tenants.DisabledUtc`), y `POST /nodes/{id}/revoke` (`Nodes.RevokedUtc`). Un tenant desactivado no puede subir (la autenticación del nodo falla con 401 y la tienda guarda sus eventos y reintenta), sus usuarios no pueden entrar (el login responde 401 `NotAllowed`) y un token emitido antes recibe 403 en el panel. Un nodo revocado recibe 401; la tienda se conecta de nuevo con un código.
- El panel muestra el área **Plataforma** solo si `GET /api/me` trae `isPlatformAdmin: true`; la API vuelve a comprobar el rol en cada llamada.

**Asistente**: `POST /api/assistant/ask { question, history }`. El modelo recibe herramientas de solo lectura (ventas por día, productos más vendidos, cuadres de turnos, ventas por cajero, mezcla de pagos, estado de la sincronización y cuentas por cobrar), cada una con un rango de fechas máximo. Las respuestas de las herramientas traen los nombres de cajeros y cajas, así que el asistente dice "Yudelka" y no un id. Contesta en español con cifras en RD$. Sin datos personales en los logs.

> **Reemplazado (15-10-2026, TK-146):** con el camino propio (`docs/ecf-propio.md` §10.4) la PC principal le habla directo a la DGII. El relevo dejó de enviar: `POST /relay/v1/ecf`, `RelayEcfProvider`, `ICloudEcfProvider` y `Fiscal:Provider` = `Relay` se retiraron. En su lugar, `POST /fiscal/v1/archive` (misma identidad de nodo) guarda el XML firmado, el RFCE y la respuesta de la DGII de cada documento con resultado final; un segundo envío del mismo e-NCF actualiza la fila. Contrato en `docs/ecf-propio.md` §10.9. Lo que sigue es el diseño original, como historia.

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
