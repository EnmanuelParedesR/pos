# Cobro de Toki con Paddle Billing (TK-249)

Cómo cobra Toki sus planes en línea: Paddle Billing como **comerciante registrado** (*merchant of record*), el catálogo de planes y servicios adicionales, qué hace la nube con cada aviso de Paddle, cómo se emite sola la licencia de la tienda y qué debe configurar el dueño. El código está en `src/Hosts/Toki.Host.Cloud/Billing/`, la página del panel en `src/Cloud/panel/src/pages/BillingPage.tsx`.

**Mientras `Billing:Paddle` esté vacío, el cobro está apagado** (así viene): la página "Plan y facturación" muestra el plan y el enlace para escribirnos, el webhook responde 404 y los planes los sigue poniendo el personal de Toki en la consola (TK-248).

## Por qué Paddle

- Paddle vende como comerciante registrado: el cliente le compra a Paddle, que cobra, calcula y paga los impuestos de venta de cada país y le liquida a Toki. Toki no necesita pasarela local ni registrar impuestos en el exterior ([Paddle: impuestos y monedas](https://developer.paddle.com/concepts/sell/supported-currencies.md)).
- República Dominicana es un país comprador admitido (USD, precios sin impuesto incluido) ([países y monedas](https://developer.paddle.com/concepts/sell/supported-countries-locales.md)).
- Como **vendedor**, Paddle trabaja con empresas de software de cualquier país salvo una lista de excluidos; RD **no** está en esa lista ([países admitidos](https://www.paddle.com/help/start/intro-to-paddle/which-countries-are-supported-by-paddle)). Paddle aprueba cada cuenta caso a caso: **confirmar con Paddle** la cuenta y el cobro a un banco dominicano antes de anunciar el pago en línea.

## Catálogo

**Paddle es la fuente del catálogo.** Cada plan y cada adicional es un **producto** en Paddle con un **precio mensual** y, si se ofrece, uno **anual**. Precios, moneda, forma de pago, cuotas, límites por minuto, cajas y funciones se editan **en Paddle**; el código no tiene montos ni ids de precio. La nube lee el catálogo (`GET /products?include=prices`, todas las páginas) al iniciar, cada `Billing:Paddle:CatalogSyncMinutes` (60) y en cuanto llega un webhook `product.*` o `price.*`; lo guarda en `cloud.PaddleProducts` (la última lectura buena sigue valiendo si Paddle no responde). El panel ("Plan y facturación") y la página pública **/precios** (`GET /api/public/prices`, sin cuenta) muestran los precios reales; con el cobro apagado, /precios dice "Precio por confirmar".

Un producto es de Toki solo si su `custom_data` tiene la clave `toki` (los demás se ignoran). Los códigos son los de TK-248 (`docs/cuotas.md`). Los precios los decide el dueño (TK-203); aquí van como `[PRECIO]`.

### Esquema de `custom_data.toki`

```json
{
  "toki": {
    "plan": "MultiRegister",
    "quotas": { "AssistantQuestions": 600, "AssistantTokens": 6000000, "Emails": 600, "ArchiveRetrievals": 100, "Invites": 30 },
    "rateLimits": { "panelPerMinute": 120, "assistantPerMinute": 10, "supplierInvoicesPerMinute": 10 },
    "registers": 3,
    "features": ["ButcherModule", "WholesaleModule", "CloudBackup", "AiAssistant"]
  }
}
```

| Clave | Qué es |
|---|---|
| `plan` **o** `addOn` | El código: `Default`, `MultiRegister`, `MultiStore`; o `SupplierInvoiceReading` |
| `quotas` | Límites del mes por medidor (`AssistantQuestions`, `AssistantTokens`, `SupplierInvoices`, `Emails`, `ArchiveRetrievals`, `AccompanimentRequests`, `Invites`). En un adicional, suben el límite si es mayor |
| `rateLimits` | Por minuto: `panelPerMinute` (API del panel, por usuario), `assistantPerMinute` (preguntas al asistente, por usuario), `supplierInvoicesPerMinute` (facturas leídas, por caja) |
| `registers` | Cajas además de la PC principal que pone la licencia |
| `features` | Funciones de la licencia (`ButcherModule`, `WholesaleModule`, `CloudBackup`, `AiAssistant`, `SupplierInvoiceReading`) |

**Orden:** `custom_data` de Paddle &gt; appsettings (`Quotas:<Plan>`, `Quotas:<Plan>:RateLimits`, `Billing:Plans:<Plan>`) &gt; lo de `Default` (Paddle, luego appsettings) &gt; el valor global (`Panel:RequestsPerMinutePerUser`, `Ai:AssistantQuestionsPerMinutePerUser`, `Ai:SupplierInvoicesPerMinutePerNode`). Lo que falte en Paddle sale de appsettings: se puede empezar solo con `plan` y completar después. Los nombres de las claves no distinguen mayúsculas. Un cambio en Paddle aplica en la siguiente lectura del catálogo (o al instante con el webhook); los límites por minuto de un negocio siguen a su plan en menos de un minuto.

### Catálogo de prueba en sandbox (creado por API el 16/10/2026)

Precios **de mentira** (US$1.00 al mes, nombres con `[SANDBOX]`) para probar el flujo; el `custom_data` copia los valores de appsettings. Los ids no son secretos.

| Producto | `pro_…` | Precio mensual `pri_…` |
|---|---|---|
| [SANDBOX] Una caja (`Default`) | `pro_01m52qsss2vbns44aq8tpq86db` | `pri_01m52qst9fkm4a8q7gxapvsky5` |
| [SANDBOX] Varias cajas (`MultiRegister`) | `pro_01m52qstn30dkwfbw2bgfqht0q` | `pri_01m52qsv39f4v4zb47dq40pbtm` |
| [SANDBOX] Varias sucursales (`MultiStore`) | `pro_01m52qsvck6hgsdcs0v684t8wk` | `pri_01m52qsvtt6fkzgkd5v02xazw9` |
| [SANDBOX] Lectura de facturas (`SupplierInvoiceReading`) | `pro_01m52qsw71pk44xk5sbg0w2acw` | `pri_01m52qswns3mx99nfsb6nqn2we` |

### Planes

| Código | Nombre | Qué incluye en la licencia (`custom_data.toki`, si no `Billing:Plans`) | Mensual | Anual |
|---|---|---|---|---|
| `Default` | Una caja | 1 caja además de la PC principal; carnicería, mayoreo, respaldo en la nube, asistente | [PRECIO] | [PRECIO] |
| `MultiRegister` | Varias cajas | Lo mismo con `Registers` = 3 (**provisional**, [CANTIDAD] por confirmar en TK-203) | [PRECIO] | [PRECIO] |
| `MultiStore` | Varias sucursales | Lo mismo con `Registers` = 3 por tienda (**provisional**); cada tienda tiene su licencia | [PRECIO] | [PRECIO] |
| `SupplierInvoiceReading` (adicional) | Lectura de facturas con IA | 300 facturas al mes (`Quotas:AddOns`) y la función `SupplierInvoiceReading` en la licencia | [PRECIO] | [PRECIO] |

- **Cajas adicionales** como adicional (por ejemplo "caja extra" con cantidad): no se construye ahora. Los planes ya fijan las cajas; si el dueño lo quiere, sería un precio con `quantity` y sumar `quantity` a `Registers` al emitir la licencia.
- **Mensual o anual:** el dueño elige al pagar (el panel muestra "Mensual / Anual" solo si hay algún precio anual). Una suscripción existente conserva su forma de pago; cambiar de plan o agregar un adicional se **prorratea al momento** (`proration_billing_mode: prorated_immediately`).
- Un plan o adicional sin precio activo en Paddle no se vende de esa forma (el panel no lo muestra; /precios dice "Precio por confirmar").

## Prueba de 14 días

La prueba de Toki **no** es de Paddle: la tienda empieza sus 14 días con la **primera venta** real (TK-196) y no pide tarjeta. Por eso los precios de Paddle se crean **sin** `trial_period`. Si algún día se quiere cobrar con prueba en Paddle (`trial_period` con `requires_payment_method`, [crear precio](https://developer.paddle.com/api-reference/prices/create-price.md)), la nube ya entiende el estado `trialing`: la licencia cubre hasta el fin de la prueba.

## Impuestos, facturas y reembolsos

- **Impuestos:** los calcula y paga Paddle; el cliente recibe la factura de Paddle. Elegir en cada precio `tax_mode` (`account_setting` por defecto; `internal` = precio con impuesto incluido; `external` = impuesto aparte).
- **Reembolsos:** desde el panel de Paddle (o `POST /adjustments` con `action: refund`); en vivo, Paddle los aprueba antes de devolver ([ajustes](https://developer.paddle.com/api-reference/adjustments/overview)). Toki no reembolsa desde su panel. Un reembolso no quita días pagados por sí solo: si corresponde, cancelar la suscripción en Paddle (la nube la verá como `canceled`).

## Pagos atrasados: pendiente → gracia → restringido (nunca solo lectura)

1. Paddle no logra cobrar la renovación: la suscripción pasa a `past_due` y Paddle **reintenta** (por defecto hasta 7 veces en 30 días, [cambio de 2025](https://developer.paddle.com/changelog/2025/default-dunning-payment-recovery); otra página habla de una semana, [renovación y cobro](https://developer.paddle.com/build/lifecycle/subscription-renewal-dunning): revisarlo en la cuenta).
2. En la nube: estado **Pago pendiente**; el plan y sus cuotas **se mantienen** mientras Paddle reintenta. "Pagado hasta" **no** avanza (solo avanza con un pago o con la suscripción `active`).
3. En la tienda: su licencia vence en "pagado hasta" + 3 días (`Billing:License:MarginDays`), luego **15 días de gracia** con todo encendido y después **Restringido**: funciones pagas apagadas y una caja además de la PC principal, pero **vender, facturar e-CF, exportar y respaldar nunca se detienen** (TK-220).
4. Si un reintento cobra: `transaction.completed` y `subscription.updated` (activa) → la nube extiende "pagado hasta" y la tienda se levanta en su próxima consulta.
5. Si todos fallan: Paddle cancela (`subscription.canceled`) → el negocio vuelve al plan `Default` sin adicionales en la nube, salvo que el personal tenga un acuerdo manual vigente.

## Qué hace la nube

### Webhook `POST /billing/paddle/webhook`

- **Firma:** encabezado `Paddle-Signature: ts=…;h1=…`; se calcula HMAC SHA-256 de `ts:cuerpo` con el secreto del destino y se compara en tiempo constante con cada `h1` ([verificación de firma](https://developer.paddle.com/webhooks/signature-verification)). Se acepta un `ts` dentro de **5 minutos** (los SDK de Paddle usan 5 segundos; aquí se tolera un reintento lento y la hora desfasada, y la repetición la frena el `event_id`). Firma mala, ausente o vieja: **401**.
- **Una sola vez por `event_id`** (tabla `cloud.BillingEvents`): Paddle reintenta hasta recibir 2xx (en vivo 60 veces en 3 días) y un reenvío se contesta 200 sin repetir nada ([responder a webhooks](https://developer.paddle.com/webhooks/about/respond-to-webhooks)).
- **Orden:** Paddle no garantiza el orden; un evento de suscripción con `occurred_at` anterior al último aplicado se registra pero no se aplica.
- **A qué negocio:** `custom_data.tenantId`, que pone nuestro checkout y Paddle copia a la suscripción; si no viene, el id de suscripción ya conocido.
- `subscription.created/updated/activated/trialing/past_due/paused/resumed/canceled` → estado, cliente, forma de pago, próximo cobro, "pagado hasta" (solo si está activa o en prueba) y **el plan y los adicionales del negocio** (los mismos campos que pone la consola en TK-248) según los precios de `items[].price.id`.
- `transaction.completed` → "pagado hasta" = fin de `billing_period` si es mayor, y una suscripción pendiente vuelve a activa.
- `product.*` y `price.*` → el catálogo se relee enseguida (precios y límites nuevos).
- Tablas: `cloud.TenantSubscriptions` (una por negocio) y `cloud.BillingEvents` (migración `Billing`).

### API del panel (solo Dueños)

| Ruta | Qué hace |
|---|---|
| `GET /api/billing` | Plan, adicionales, estado, pagado hasta, próximo cobro, acuerdo manual, si hay cliente de Paddle, y los planes y adicionales que tienen precio |
| `POST /api/billing/checkout` | Sin suscripción: devuelve lo que Paddle.js necesita (`environment`, `clientToken`, `items` con `priceId`, correo del dueño y `customData.tenantId`). Con suscripción viva: cambia sus `items` en Paddle (`PATCH /subscriptions/{id}`, prorrateado) y responde `{ changed: true }` |
| `POST /api/billing/portal` | Crea una sesión del portal del cliente (`POST /customers/{id}/portal-sessions`) y devuelve el enlace (tarjeta, facturas, cancelar). El enlace lleva un token temporal: no se guarda ([portal](https://developer.paddle.com/api-reference/customer-portals/create-customer-portal-session)) |

### Página "Plan y facturación" (`/app/billing`, Dueños)

Plan, estado ("Al día", "Pago pendiente", …), pagado hasta, próximo cobro, enlace a "Consumo del mes" (TK-248) y los botones **Elegir plan**, **Agregar lectura de facturas** y **Administrar pago**. Paddle.js se carga **solo en esta página**, desde `https://cdn.paddle.com/paddle/v2/paddle.js` (Paddle pide cargarlo siempre de su CDN; la versión la fija la ruta `/v2/`, [incluir Paddle.js](https://developer.paddle.com/paddlejs/include-paddlejs)). Con `Environment = sandbox` llama `Paddle.Environment.set("sandbox")` antes de `Paddle.Initialize({ token })` ([entorno](https://developer.paddle.com/paddlejs/methods/paddle-environment-set)) y abre `Paddle.Checkout.open({ items, customer: { email }, customData })` en modo superpuesto ([abrir checkout](https://developer.paddle.com/paddlejs/methods/paddle-checkout-open)).

La página pública **/precios** lleva a "Crear su cuenta y elegir plan" (`/app/billing`: si no hay sesión, primero pide crear la cuenta o entrar).

### Consola de la plataforma

En el cliente: tarjeta **Suscripción** con estado, ids de Paddle, pagado hasta, próximo cobro, pendiente desde y los últimos eventos; y el **acuerdo manual**: "pagado hasta" con motivo obligatorio (ofertas, pagos por transferencia). Cada cambio queda en `PlatformAudit` como toda llamada de la plataforma. El plan y los adicionales del acuerdo se ponen en "Plan y consumo" (TK-248).

## Recomendaciones: "Recomiende Toki" (TK-255)

**Regla del dueño:** recomendar da un **descuento en el próximo pago de quien recomienda**, y solo cuando el negocio recomendado **paga** un plan que califica (crear la cuenta no da nada). Apagado mientras `Billing:Referrals:Amount` esté vacío; los valores los decide el dueño.

- **Código y enlace:** cada negocio tiene un código de 8 letras y números (sin 0/O, 1/I/L), creado la primera vez que el Dueño abre "Plan y facturación". Enlace: `https://<sitio>/precios?ref=CÓDIGO`. /precios comprueba el código (`GET /api/public/referrals/{código}`, sin cuenta, `Billing:Referrals:LookupsPerMinute` por IP contra quien adivina códigos) y lo guarda en el navegador; el primer checkout lo envía (`referralCode`) y la nube lo **guarda en el negocio** (`TenantSubscriptions.ReferredByCode`; nunca su propio código). El checkout lo pasa a Paddle en `customData`: `{ tenantId, referral_code, referrer_tenant_id }`.
- **Cuándo cuenta:** en `transaction.completed`, solo el **primer pago con monto** del negocio (`details.totals.grand_total` > 0; el checkout de una prueba en 0 no cuenta), y solo si algún precio comprado es de un plan que califica: su código o su `pri_…` en `Billing:Referrals:QualifyingPlans`, o el producto con `custom_data.toki.referral: true`. Si ese primer pago no califica, ese negocio ya no da recompensa. **Una recompensa por negocio recomendado, para siempre** (índice único) y cada evento una sola vez (`event_id`).
- **Tabla `cloud.Referrals`** (migración `Referrals`): quién recomendó, a quién, el pago (`TransactionId`), el descuento, y el estado **Pending** (pagó, falta aplicar), **Applied** (descuento puesto), **Reverted** (reembolso o ajuste) o **Rejected** (abuso o límite, con motivo).
- **Aplicar:** la recompensa pendiente más vieja se pone en la suscripción de quien recomienda: `POST /discounts` (`mode: custom`, `recur: false`, `usage_limit: 1`, `enabled_for_checkout: false`, `restrict_to` = los precios de su plan, no los adicionales), `PATCH /subscriptions/{id}/preview` y luego `PATCH /subscriptions/{id}` con `discount: { id, effective_from: "next_billing_period" }` ([descuentos](https://developer.paddle.com/api-reference/discounts/create-discount), [suscripción](https://developer.paddle.com/api-reference/subscriptions/update-subscription)). **Espera** (queda Pending) si su suscripción no está activa (pago pendiente, en pausa, cancelada), si Paddle ya tiene un descuento en ella, si ese próximo pago ya lleva uno nuestro (**uno por ciclo**) o si Paddle falla; se intenta de nuevo con cada evento `subscription.*` o `transaction.completed` de quien recomienda (o desde la consola).
- **Revertir:** `adjustment.created`/`adjustment.updated` con `action` `refund` o `chargeback` y `status: approved` sobre el pago que dio la recompensa: si estaba pendiente, Reverted; si estaba aplicada y quien recomienda **todavía no pagó** ese ciclo, se quita el descuento (`PATCH … { discount: null }`) y Reverted. Si Paddle falla, el webhook contesta 503 y Paddle reintenta. Si ya pagó con el descuento, queda para la consola. Incluir los eventos `adjustment.*` en el destino de notificaciones.
- **Abuso:** se rechaza (Rejected) recomendarse a sí mismo (mismo negocio), el **mismo pagador** (mismo cliente de Paddle, que es uno por correo, o un Dueño en ambos negocios) y el **mismo RNC** (el del receptor e-CF, ahora o antes); y más de `MonthlyLimitPerReferrer` recompensas por mes (UTC).
- **Panel** (Dueños, "Plan y facturación"): tarjeta **Recomiende Toki** con el enlace para copiar, cuántos pagaron y esperan su descuento, cuántos descuentos se aplicaron y el próximo descuento (en qué pago, o "en espera"). `GET /api/billing/referrals`.
- **Consola:** tarjeta **Recomendaciones** en el cliente (las que hizo y la que recibió) con **Revertir** y **Restablecer** (motivo obligatorio) y **Aplicar ahora**; `GET/POST /api/platform/tenants/{id}/referrals[/{referralId}]`, auditado en `PlatformAudit`.

## Licencias emitidas por la nube

Hasta ahora las licencias se firman fuera de línea con la llave del dueño (`docs/licencias.md`). Con TK-249 la nube también puede emitirlas, con **otra llave**:

- Una llave **EC P-256 en Azure Key Vault, no exportable**: la API firma con ella por Key Vault ("Key Vault Crypto User", ya asignado en `deploy/azure/main.bicep`) y la parte privada nunca sale de la bóveda.
- La tienda acepta licencias firmadas por **cualquiera de las dos** llaves públicas: la de siempre y la de la nube (`TokiCloudPublicKey` en `src/Modules/Settings/Toki.Settings/License.cs`, vacía hasta crear la llave).
- La tienda conectada pide su licencia por el canal del nodo (`POST /api/nodes/license`, firmado con la llave de la caja) en cada latido (al iniciar y cada 4 horas) y la **instala sola** si es válida y **más nueva** (fecha de emisión) que la instalada. La fecha de emisión es la del último cambio de la suscripción, así que la misma licencia no se reinstala en cada consulta.
- Contenido: tienda, nombre del plan, funciones y cajas de `Billing:Plans:<Plan>` (si un plan no lo define, toma lo de `Default`), más la función del adicional `SupplierInvoiceReading`. **Vence** en "pagado hasta" (Paddle o acuerdo manual) + 3 días; luego la gracia de 15 días de la tienda.
- Sin tiempo pagado la nube no entrega licencia (una vencida reemplazaría una válida emitida fuera de línea).
- **Emisión fuera de línea:** sigue igual (tiendas sin internet, acuerdos especiales). Gana la licencia emitida más tarde, la firme quien la firme.

Comandos para crear la llave (los ejecuta el dueño; **no** se crearon recursos en Azure):

```powershell
# Bóveda del despliegue (salida keyVault de main.bicep)
$kv = "kv-toki-prod-xxxxxx"
# Quien la crea necesita "Key Vault Crypto Officer" en la bóveda (RBAC)
# Por defecto la llave no es exportable (exportarla exige una política de liberación que aquí no se pone)
az keyvault key create --vault-name $kv --name toki-license --kty EC --curve P-256 --ops sign verify
# La parte pública, en PEM, para pegarla en TokiCloudPublicKey (License.cs) y publicar una actualización de la tienda
az keyvault key download --vault-name $kv --name toki-license --encoding PEM --file toki-license-public.pem
```

Luego desplegar con `licenseKeyName = toki-license` (pone `Billing__License__KeyId`). Rotar esta llave exige publicar antes una tienda con la nueva parte pública (la vieja sigue valiendo para las licencias ya emitidas hasta que venzan).

## Configuración (`Billing` en appsettings)

| Clave | Qué es |
|---|---|
| `Billing:Paddle:Environment` | `sandbox` o `live`; vacío = cobro apagado |
| `Billing:Paddle:ApiKey` | Llave de API del servidor (`pdl_sdbx_apikey_…` / `pdl_live_apikey_…`). **Secreto: Key Vault** |
| `Billing:Paddle:WebhookSecret` | Secreto del destino de notificaciones (`pdl_ntfset_…`). **Secreto: Key Vault** |
| `Billing:Paddle:ClientToken` | Token del lado del cliente para Paddle.js (`test_…` / `live_…`); no es secreto |
| `Billing:Paddle:CatalogSyncMinutes` | Cada cuánto se relee el catálogo de Paddle (60); un webhook `product.*`/`price.*` lo adelanta |
| `Billing:Paddle:SyncInBackground` | `false` solo en pruebas (sincronizan ellas mismas) |
| `Billing:License:KeyId` | URI de la llave de licencias en Key Vault; vacío = la nube no emite licencias |
| `Billing:License:MarginDays` | Días de margen tras "pagado hasta" (3) |
| `Billing:Plans:<Plan>:Name` / `:Registers` / `:Features` | Lo que la licencia dice de cada plan |
| `Billing:Referrals:QualifyingPlans` | TK-255: códigos de plan o `pri_…` cuyo primer pago da la recompensa (además de `custom_data.toki.referral: true`). **[POR DECIDIR]** |
| `Billing:Referrals:DiscountType` / `:Amount` / `:Currency` | `percentage` (`Amount` = por ciento) o `flat` (`Amount` en la unidad mínima, `500` = US$5.00, con `Currency`). `Amount` vacío = recomendaciones apagadas. **[POR DECIDIR]** |
| `Billing:Referrals:MonthlyLimitPerReferrer` | Recompensas por negocio por mes (5) |
| `Billing:Referrals:LookupsPerMinute` | Consultas públicas de código por IP por minuto (20) |

Sandbox y en vivo son **cuentas separadas** con sus propias llaves, tokens e ids de precio ([sandbox](https://developer.paddle.com/build/tools/sandbox)); las llaves de una no sirven en la otra.

## Lo que debe hacer el dueño, paso a paso

1. **Cuenta de Paddle:** crear la cuenta de sandbox (`sandbox-vendors.paddle.com`) para probar y solicitar la cuenta en vivo (`vendors.paddle.com`); completar la verificación del negocio y la cuenta de cobro. Confirmar con Paddle el cobro a un banco dominicano.
2. **Dominio:** en Paddle → Checkout → configuración del checkout, poner el enlace de pago por defecto en el dominio del panel (`https://<panel>/app/billing`) y, en vivo, pedir la aprobación del dominio.
3. **Productos y precios** (Paddle → Catálogo): un producto por plan (Una caja, Varias cajas, Varias sucursales) y uno por adicional (Lectura de facturas con IA), cada uno con su `custom_data.toki` (esquema arriba). En cada uno, un precio **mensual** (`month`/1) y, si se ofrece, uno **anual** (`year`/1), en USD, **sin** período de prueba, con el `tax_mode` elegido. Decidir los precios ([PRECIO], TK-203), las cajas y las cuotas, y ponerlos **en Paddle** (en sandbox ya están los productos de prueba de arriba: basta cambiar sus precios y su `custom_data`). No hace falta copiar ningún `pri_…` a la configuración.
4. **Token del cliente** (Paddle → Herramientas para desarrolladores → Autenticación): crear un *client-side token* (`test_…` en sandbox).
5. **Llave de API** (misma pantalla): crear una con permisos de lectura y escritura de suscripciones, escritura de descuentos (TK-255) y `customer_portal_session.write`. Copiarla una vez.
6. **Destino de notificaciones** (Paddle → Eventos/Notificaciones → Nuevo destino): URL `https://<api>/billing/paddle/webhook` (salida `apiUrl` del despliegue), tipo webhook, eventos `subscription.*` y `transaction.completed`. **Copiar el secreto (`pdl_ntfset_…`) al crearlo**: Paddle puede no volver a mostrarlo.
7. **Secretos en Key Vault** (nunca en el repositorio ni en los parámetros):
   ```powershell
   az keyvault secret set --vault-name $kv --name Billing--Paddle--ApiKey --value "<llave de API>"
   az keyvault secret set --vault-name $kv --name Billing--Paddle--WebhookSecret --value "<secreto del destino>"
   ```
8. **Llave de licencias:** los comandos de la sección anterior; pegar el PEM en `TokiCloudPublicKey` y publicar la actualización de la tienda.
9. **Desplegar** con los parámetros nuevos de `deploy/azure/main.parameters.json`: `paddleEnvironment` (`sandbox` primero), `paddleClientToken` y `licenseKeyName`.

**Hecho ya en sandbox (por API):** los cuatro productos con precio de prueba y su `custom_data`. **Falta en el panel de Paddle (sandbox):** el token del lado del cliente (paso 4), el destino de notificaciones con la URL real de la API y copiar su secreto (paso 6; incluir también `product.*` y `price.*`), el enlace de pago por defecto (paso 2) y guardar los secretos en Key Vault (paso 7). Para desarrollo local, la llave de API de sandbox está en los *user-secrets* de `src/Hosts/Toki.Host.Cloud` de la PC del dueño (nunca en el repositorio).
10. **Probar en sandbox:** crear una cuenta en el panel, "Plan y facturación" → Elegir plan, pagar con la tarjeta de prueba `4242 4242 4242 4242` (rechazo: `4000 0000 0000 0002`); ver el estado "Al día", el plan en la consola y la licencia en la tienda conectada (Configuración → Licencia). Probar "Administrar pago" y una cancelación.
11. **Pasar a vivo:** repetir 3 a 7 en la cuenta en vivo y desplegar con `paddleEnvironment = live`.
