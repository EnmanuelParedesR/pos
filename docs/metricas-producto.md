# Métricas de producto (TK-190)

Qué mide el equipo de Toki sobre el uso del producto, de dónde sale cada número y qué datos **no** se recogen. Las ve solo el equipo de Toki en el área **Plataforma** del panel (TK-189, ADR-0008). El código está en `src/Hosts/Toki.Host.Cloud/Platform/ProductMetrics.cs` (cálculo) y `PlatformQueries.cs` (lecturas); las pruebas con datos de ejemplo, en `tests/Toki.Cloud.Tests/ProductMetricsTests.cs`.

## Fuentes

| Fuente | Qué trae | Tabla en la nube |
|---|---|---|
| Cuentas y tiendas del panel | Cuándo se registró cada cliente (tenant), sus tiendas y sus PC conectadas (nodos) | `cloud.Tenants`, `cloud.Stores`, `cloud.Nodes` |
| Réplica | Cada venta, anulación, cierre, etc. que sube la tienda | `replica.Sales`, `replica.SaleVoids`, … |
| Latido del nodo (`NodeHeartbeatV1`) | Estado y uso de la PC principal: al arrancar y cada hora mientras está conectada | `replica.NodeHeartbeats` (el más reciente por nodo) y `replica.DailyUsage` (por tienda y día) |

### El latido (`Toki.Sync.Contracts.NodeHeartbeatV1`)

Lo escribe el módulo Sync en el outbox (`SendHeartbeat`, `HeartbeatWorker`, `Sync:HeartbeatMinutes`, 240 por defecto: cada 4 horas, para que el SQL serverless de la nube se pueda pausar, R8 #21) y sube con los demás eventos, así que si no hay internet llega después y nunca se pierde. Si la tienda no está conectada a la nube, no se escribe. La nube se queda con el más reciente por `occurredUtc`: uno viejo que llega tarde no deshace uno nuevo.

| Campo | Qué es |
|---|---|
| `appVersion`, `os`, `uptimeMinutes` | Versión del servidor de la tienda, sistema operativo y minutos desde que arrancó el servicio |
| `activeRegisters` | Cajas con algún turno abierto en los últimos 30 días |
| `openShifts` | Turnos abiertos ahora (de los últimos 30 días) |
| `outboxBacklog` | Eventos que la nube todavía no confirmó |
| `lastErrorCode` | Tipo del último error al subir: `http_503`, `unreachable` u `other`; nunca el texto. `null` después de una subida buena |
| `features.onAccount` | Algún cliente debe fiado |
| `features.scaleConfigured`, `features.printerConfigured` | Alguna caja activa tiene balanza o impresora. Cada caja informa al servidor de la tienda al entrar y cuando cambia (`PUT /api/identity/devices/current/peripherals`, solo el tipo: protocolo de la balanza y ancho del papel, nunca el puerto ni el nombre). `null` mientras ninguna caja lo haya informado |
| `features.fiscalProvider` | `Fiscal:Provider`: `Simulated` o `Relay` (e-CF por la nube) |
| `setup.businessNameSet`, `setup.rncSet` | Si el negocio tiene nombre y RNC guardados (solo sí/no; el RNC no viaja) |
| `setup.activeProducts` | Cantidad de productos activos |
| `days[]` | Ayer y hoy (días de la tienda, UTC−4): facturas, monto vendido con ITBIS, cajeros que vendieron, anulaciones y monto devuelto |

## Definiciones

- **Día de la tienda**: día en hora de República Dominicana (UTC−4), como en los reportes.
- **Tienda activa en un día**: tiene al menos una venta completada (no anulada) ese día en la réplica, **o** su latido contó ventas ese día. Lo segundo cubre ventas que todavía no se aplicaron en la nube.
- **Tiendas activas hoy / 7 días / 30 días**: tiendas activas en algún día del periodo, contando hoy.
- **Ventas en 30 días**: facturas completadas (no anuladas) de la réplica en los últimos 30 días de la tienda y su monto con ITBIS. No resta devoluciones.
- **Embudo de activación** (por cliente, acumulado: cada etapa incluye a los que pasaron a la siguiente):
  1. Cuenta creada.
  2. Tienda creada en el panel.
  3. Caja conectada (alguna vez registró un nodo, aunque luego se revocara).
  4. Primera venta (algún día activo).
  5. 7 días con ventas (7 días de la tienda distintos con actividad, no necesariamente seguidos).
- **Retención por semana de registro**: los clientes se agrupan por la semana (lunes a domingo, días de la tienda) en que crearon la cuenta. La columna `+k` es el porcentaje de ese grupo con alguna tienda activa en la semana `k` después del registro.
- **Uso de funciones**: entre las tiendas con latido en los últimos 7 días (se toma el latido más reciente de la tienda), cuántas usan fiado, más de una caja, e-CF por la nube, balanza e impresora. El denominador es cuántas tiendas reportan el dato; balanza e impresora salen "sin datos" en las tiendas donde ninguna caja lo ha informado.
- **Salud**:
  - *Cajas en línea*: nodos no revocados de clientes activos que subieron algo en las últimas 24 horas. *Cajas sin datos en 24 h* es el resto.
  - *Cajas con error al subir*: su latido más reciente (últimos 7 días) trae `lastErrorCode`.
  - *Cajas con más de 500 datos por subir*: su latido más reciente trae `outboxBacklog` > 500.
  - *Datos recibidos que fallaron*: eventos de la bandeja de entrada que la nube no pudo aplicar después de 10 intentos.
  - *Versiones en las tiendas*: cuántos nodos reportan cada versión (latido de los últimos 7 días).
- **Señales de abandono** (tiendas de clientes no desactivados):
  - *La caja no envía datos*: ninguna de sus PC subió nada en 3 días (una PC recién conectada que todavía no sube tiene esos 3 días desde que se registró).
  - *Dejó de vender*: vendió alguna vez y no ha tenido un día activo en los últimos 7 días.
- **Por cliente** (`/api/platform/metrics/tenants/{id}`): etapa del embudo, días con ventas en los últimos 30, facturas y monto en 30 días, primer y último día activo.

## API

Todo bajo `/api/platform`, con la política `PlatformAdmin` y auditado:

| Ruta | Qué devuelve |
|---|---|
| `GET /metrics/overview` | Indicadores, embudo, uso de funciones, salud y señales de abandono |
| `GET /metrics/daily?days=30` | Por día: tiendas activas, facturas y monto (1 a 120 días) |
| `GET /metrics/retention?weeks=8` | Cohortes por semana de registro (1 a 26 semanas) |
| `GET /metrics/tenants/{tenantId}` | Métricas de un cliente |

## Privacidad

- El latido lleva **solo conteos, sí/no y totales**. Nunca nombres de personas, PIN, códigos de usuario, permisos, clientes, cédulas, RNC, nombres de productos ni montos por persona. Una prueba de integración (`HeartbeatTests` en `tests/Toki.IntegrationTests/Sync`) revisa que el evento solo traiga los campos de esta tabla y que no contenga el nombre del negocio, el RNC, los nombres, códigos ni PIN de los usuarios ni los nombres de los productos.
- El error de subida viaja como tipo (`http_503`), no como texto: el texto puede llevar una dirección o un mensaje del sistema.
- Las métricas las ve solo el equipo de Toki (`PlatformAdmin`). Cada consulta a `/api/platform` queda en `cloud.PlatformAudit` con quién, qué (ruta y consulta) y cuándo.
- Los correos de los dueños aparecen en la consola porque son las cuentas del panel (para poder darles soporte); los nombres de cajeros solo aparecen en los avisos de cuadre, igual que en el panel del dueño.
- Los datos de ventas que usan las métricas son los mismos que el dueño ya sube para su panel (ADR-0008); no se suben datos nuevos para medir, salvo el latido.

## Pendiente

- Plan o licencia del cliente: la nube no la conoce todavía (la licencia vive en la tienda).
- Cálculo en memoria sobre todas las ventas: está bien para las primeras decenas de tiendas; con más hace falta un resumen diario en SQL (`shortcut:` en `ProductMetrics`).
