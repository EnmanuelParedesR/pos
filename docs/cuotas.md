# Cuotas y límites de uso (TK-248)

Pedido del dueño: todo lo que Toki ofrece debe tener cuotas que eviten el abuso y el consumo excesivo de los servicios. Este documento inventaría cada servicio de la nube y de la tienda que cuesta dinero o se presta al abuso, dice qué límite tiene hoy, qué falta y qué límite se propone. Hay dos tipos de límite:

- **Por minuto (anti-abuso):** frena ráfagas (un script, un bucle, un reintento atascado). Usa el limitador de ASP.NET (`AddRateLimiter` en `Program.cs`); al pasarse responde **429**.
- **Por mes (consumo):** lo que un negocio puede gastar en el mes de la tienda (UTC−4) según su plan. Lo lleva `IQuota` (tabla `cloud.TenantUsage`); al pasarse responde **429 `Quota.Exceeded`** con el medidor, lo usado, el límite, la fecha en que se renueva (`resetsOn`, el día 1 del mes siguiente) y un mensaje en español.

## Cómo funciona la cuota mensual

- `IQuota.TryConsumeAsync(negocio, medidor, n)` toma `n` del mes en **una sola sentencia** (`UPDATE … SET Count = Count + n WHERE Count + n <= límite`), así que dos llamadas a la vez nunca toman la última unidad las dos (SQL Server y SQLite). El primer uso del mes inserta la fila; si otra llamada la insertó antes, se cuenta sobre ella.
- `IQuota.AddAsync` suma sin límite: para lo que solo se conoce después (tokens del asistente), para lo que nunca debe negarse (el correo de recuperar la contraseña) o para devolver lo tomado cuando otro límite rechaza la llamada.
- El límite sale de la configuración: `Quotas:<Plan>:<Medidor>`, si no `Quotas:Default:<Medidor>`, si no 0 (no se ofrece). Un servicio adicional comprado (`Quotas:AddOns:<Adicional>:<Medidor>`) sube el límite al suyo si es mayor.
- El plan y los adicionales de cada negocio los pone el personal de Toki en la consola (**Plataforma → cliente → Plan y consumo**; `PUT /api/platform/tenants/{id}/plan`, auditado). Sin plan puesto, el negocio usa `Default`.
- **Atajo documentado:** la nube no lee el plan de la licencia firmada de la tienda. El latido solo *dice* el estado de la licencia y la tienda podría mandar cualquier cosa, así que no se confía en él para dar más cuota. Al emitir una licencia con un adicional, el personal también lo activa en la consola. Si mantener ambos al día se vuelve pesado: mandar la licencia (JWS) en el latido y verificar su firma en la nube con la llave pública.

Planes (códigos de configuración): `Default` = Prueba y **Una caja**; `MultiRegister` = **Varias cajas**; `MultiStore` = **Varias sucursales**. Lo que un plan no define lo toma de `Default`.

## Cuotas mensuales por plan (valores por defecto en `appsettings.json`)

| Medidor | Qué cuenta | Una caja (`Default`) | Varias cajas | Varias sucursales | Costo unitario aprox. | Costo máx. del mes (Una caja) |
|---|---|---|---|---|---|---|
| `AssistantQuestions` | Preguntas al asistente del panel | 300 | 600 | 1,500 | US$0.0005–0.0015 | ≈ US$0.45 |
| `AssistantTokens` | Tokens que Azure OpenAI dice haber gastado (entrada + salida, todas las vueltas) | 3,000,000 | 6,000,000 | 15,000,000 | gpt-5-nano: US$0.05 / 1M entrada, US$0.40 / 1M salida | ≤ US$1.20 (todo salida) |
| `SupplierInvoices` | Facturas de suplidor leídas con IA | **20** | 20 | 20 | US$0.001–0.003 | ≤ US$0.06 |
| — con el adicional `SupplierInvoiceReading` | | **300** | 300 | 300 | | ≤ US$0.90 |
| `Emails` | Correos que manda la nube por el negocio (invitaciones, avisos, confirmar correo, cambiar contraseña) | 300 | 600 | 2,000 | ACS: US$0.00025 por correo + US$0.00012/MB | ≈ US$0.08 |
| `ArchiveRetrievals` | Lecturas de un e-CF que ya está en almacenamiento frío (Blob) | 50 | 100 | 300 | Blob Cool/Archive: lectura ≈ US$0.0005–0.001 + rehidratación estándar ≈ US$0.02/GB | ≈ US$0.05 |
| `AccompanimentRequests` | Solicitudes de acompañamiento a la certificación DGII | 3 | 3 | 10 | Tiempo del personal + 1–2 correos | — |
| `Invites` | Invitaciones al panel | 20 | 30 | 50 | Un correo cada una (a una dirección que elige el negocio) | — |

Notas:

- Los precios de Azure son de lista y aproximados: confirmarlos con la calculadora de Azure antes de fijar precios. El costo máximo es el de un negocio que gasta toda su cuota; casi ninguno lo hará.
- Los correos de **confirmar correo** y **cambiar contraseña** cuentan en `Emails` pero **nunca se niegan** (una recuperación de contraseña debe llegar siempre); los frenan los límites por IP. Los **avisos** se dejan de mandar (queda en el registro) cuando el negocio gastó sus correos del mes. Una **invitación** gasta una invitación y un correo; si no queda alguno, 429.
- Los correos que el personal manda al dueño por el acompañamiento (citas) no cuentan: los dispara Toki, no el negocio.
- `ArchiveRetrievals` cuenta cada intento sobre un documento frío, también mientras vuelve del nivel Archive (horas). Los documentos de menos de 90 días están en SQL y no cuentan.

## Servicio adicional: lectura de facturas con IA

- **Incluido en todos los planes:** 20 facturas al mes (`Quotas:Default:SupplierInvoices`).
- **Adicional pago** `SupplierInvoiceReading`: 300 al mes (`Quotas:AddOns:SupplierInvoiceReading:SupplierInvoices`). Precio: **[PRECIO]** al mes.
- En la licencia de la tienda es la función `SupplierInvoiceReading` (`Feature` en `Toki.BuildingBlocks.Security`); la nube aplica la cuota según lo que el personal puso en la consola (ver el atajo arriba).
- Además del mes siguen: 100 por día por negocio (`Ai:SupplierInvoicesPerDay`, tabla `AiUsage`, R9 #14), 10 por minuto por PC (`Ai:SupplierInvoicesPerMinutePerNode`) y nada con la licencia restringida. Si el día rechaza, la lectura del mes se devuelve.
- La tienda muestra en **Compras** "Lecturas con IA este mes: le quedan X de Y" (`GET /api/purchasing/purchases/read-invoice/quota`, que pregunta a la nube `POST /ai/v1/supplier-invoice/quota`). Agotadas, avisa la fecha en que se renuevan y sugiere el adicional; el XML del e-CF del suplidor se sigue leyendo sin IA y sin gastar lecturas.

## Inventario de servicios

| Servicio | Riesgo | Límite que ya había | Agregado en TK-248 | Falta / propuesta |
|---|---|---|---|---|
| Asistente del panel (`POST /api/assistant/ask`, Azure OpenAI) | Costo por tokens | Pregunta ≤ 1000 letras, historial ≤ 20 turnos, ≤ 5 vueltas de herramientas, rangos ≤ 92 días | 10 por minuto por usuario (`Ai:AssistantQuestionsPerMinutePerUser`); mes: preguntas y tokens por plan | — |
| Lectura de facturas (`POST /ai/v1/supplier-invoice`, TK-224) | Costo por imagen o PDF | 4 MB, PDF/JPG/PNG/WEBP; 100 por día por negocio; 10 por minuto por PC; licencia restringida no | Mes por plan (20) y adicional (300) | — |
| Correo: invitaciones (`POST /api/members/invites`) | Correo a cualquier dirección | Solo Dueños; vence en 7 días; un solo uso | Mes: `Invites` y `Emails`; 120 por minuto por usuario en `/api/members` | — |
| Correo: avisos (TK-222) | Volumen por miembro | Uno por condición, tienda y día (`SentAlerts`); solo a miembros que lo quieren | Mes: `Emails` | — |
| Correo: confirmar correo / reenviar (`/api/auth/resendConfirmationEmail`) | Correo a cualquier dirección | 3 por minuto por IP (TK-231); no dice si la cuenta existe | Cuenta en `Emails` (no niega) | — |
| Correo: cambiar contraseña (`/api/auth/forgotPassword`) | Correo, enumeración | Solo a correos confirmados; misma respuesta exista o no | 10 por minuto por IP (`Auth:RequestsPerMinute`); cuenta en `Emails` | — |
| Correo: acompañamiento (al personal y al dueño) | Correos | Una solicitud abierta por tienda | Mes: `AccompanimentRequests` | — |
| Registro, entrada, cambio de contraseña (`/api/auth/register`, `/login`, `/resetPassword`) | Fuerza bruta, cuentas basura | Bloqueo de Identity: 5 contraseñas malas = 5 minutos por cuenta | 10 por minuto por IP | `/refresh` sin límite (un token de 256 bits no se adivina) |
| Enlaces de invitación (`/api/invites/preview`, `/register`, `/accept`) | Crear cuentas | Token de 256 bits, un solo uso, 7 días | 10 por minuto por IP (mismo límite que la entrada) | — |
| Catálogo público (`/c/{tienda}`) | Raspado | 60 por minuto por IP (R8 #17) | — | — |
| Registrar una PC con código (`/api/nodes/register`) | Adivinar el código de 8 dígitos | 10 por minuto por IP; el código vence en 30 minutos y sirve una vez | — | — |
| Receptor e-CF de la nube (`/{amb}/{rnc}/fe/…`) | Cualquiera lo llama; cada llamada puede firmar en Key Vault | 60 por minuto por IP y 600 por RNC (R9 #2, en `Fe/`) | — | Ver "Firmas" abajo |
| Firmas en Key Vault (aprobaciones comerciales, semilla; TK-151/TK-246) | Key Vault cobra por operación (≈ US$0.03 por 10,000 con llave RSA de software; más con HSM) | `FeSigner` solo firma ARECF/ACECF/SemillaModel y registra cada firma; límites del receptor | — (otro agente trabaja en `Fe/`) | Propuesta: medidor `Signatures` de 2,000 al mes por negocio (Una caja), 10,000 (Varias sucursales) y 30 por minuto por PC en las solicitudes de firma de TK-246; integrarlo cuando TK-246 cierre |
| Subida de eventos (`POST /ingest/v1/events`) | Base de datos serverless despierta, almacenamiento | 1 a 200 eventos por lote; PC firmada (ADR-0008); negocio desactivado no sube | — | Propuesta: 120 lotes por minuto por PC (alcanza para vaciar 24,000 eventos atrasados por minuto). No se aplicó porque la subida no espera un 429 y reintentaría sin pausa: primero hacer que respete `Retry-After` |
| Latido (`NodeHeartbeatV1`) | Ninguno aparte | Viaja con la subida, uno cada 240 minutos (`Sync:HeartbeatMinutes`); el más nuevo gana | — | — |
| Archivo frío de e-CF (`GET /api/fiscal/archive/{encf}`) | Lectura y rehidratación de Blob Archive | Rehidratación estándar (la barata), una sola vez por documento | Mes: `ArchiveRetrievals`; 120 por minuto por usuario | — |
| Solicitudes de acompañamiento (`POST /fiscal/v1/accompaniment`) | Tiempo del personal, correos | Una abierta por tienda | Mes: `AccompanimentRequests` | — |
| Panel (`/api/panel/*`, `/api/members`, `/api/usage`, archivo) | Consultas pesadas a SQL | Rangos ≤ 366 días; solo miembros | 120 por minuto por usuario (`Panel:RequestsPerMinutePerUser`) | `/api/me`, `/api/stores`, `/api/notifications` y `/api/panel/fe` (en `Fe/`) sin límite por minuto: son livianos |
| Consola de la plataforma (`/api/platform/*`) | Datos de todos los negocios | Solo personal de Toki (rol dado por comando), cada llamada auditada | — | — |
| API local de la tienda (LAN, `https://0.0.0.0:5443`) | Fuerza bruta del PIN | Cada caja con su certificado de dispositivo; el PIN se bloquea tras 5 intentos fallidos (5 minutos, el doble cada vez, hasta 60) | — | — |

## Dónde se ve el consumo

- **Panel (Dueños):** tarjeta **Consumo del mes** en Tiendas (`GET /api/usage`): cada medidor con lo usado frente al límite, el plan, los adicionales y la fecha en que se renueva.
- **Consola de la plataforma:** **Plan y consumo** en la página de cada cliente (`GET /api/platform/tenants/{id}/usage`): el mismo consumo y el selector de plan y adicionales.
- **Tienda:** Compras muestra las lecturas con IA que quedan (ver arriba).

## Configuración

| Clave | Por defecto | Qué es |
|---|---|---|
| `Quotas:<Plan>:<Medidor>` | ver la tabla | Cuota mensual de un plan |
| `Quotas:AddOns:<Adicional>:<Medidor>` | `SupplierInvoiceReading:SupplierInvoices` = 300 | Cuota con un adicional |
| `Auth:RequestsPerMinute` | 10 | Registro, entrada, cambio de contraseña y enlaces de invitación, por IP |
| `Auth:ResendConfirmationPerMinute` | 3 | Reenviar la confirmación, por IP |
| `Panel:RequestsPerMinutePerUser` | 120 | API del panel, por usuario |
| `Ai:AssistantQuestionsPerMinutePerUser` | 10 | Preguntas al asistente, por usuario |
| `Ai:SupplierInvoicesPerDay` | 100 | Facturas leídas por negocio y día |
| `Ai:SupplierInvoicesPerMinutePerNode` | 10 | Facturas leídas por PC y minuto |
| `Catalog:RequestsPerMinute` | 60 | Catálogo público, por IP |
| `Nodes:RegisterPerMinute` | 10 | Registrar una PC, por IP |
| `Fe:RequestsPerMinutePerIp` / `PerRnc` | 60 / 600 | Receptor e-CF |
