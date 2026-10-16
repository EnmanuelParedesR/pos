# Cobro de Toki con Paddle Billing (TK-249)

Cómo cobra Toki sus planes en línea: Paddle Billing como **comerciante registrado** (*merchant of record*), el catálogo de planes y servicios adicionales, qué hace la nube con cada aviso de Paddle, cómo se emite sola la licencia de la tienda y qué debe configurar el dueño. El código está en `src/Hosts/Toki.Host.Cloud/Billing/`, la página del panel en `src/Cloud/panel/src/pages/BillingPage.tsx`.

**Mientras `Billing:Paddle` esté vacío, el cobro está apagado** (así viene): la página "Plan y facturación" muestra el plan y el enlace para escribirnos, el webhook responde 404 y los planes los sigue poniendo el personal de Toki en la consola (TK-248).

## Por qué Paddle

- Paddle vende como comerciante registrado: el cliente le compra a Paddle, que cobra, calcula y paga los impuestos de venta de cada país y le liquida a Toki. Toki no necesita pasarela local ni registrar impuestos en el exterior ([Paddle: impuestos y monedas](https://developer.paddle.com/concepts/sell/supported-currencies.md)).
- República Dominicana es un país comprador admitido (USD, precios sin impuesto incluido) ([países y monedas](https://developer.paddle.com/concepts/sell/supported-countries-locales.md)).
- Como **vendedor**, Paddle trabaja con empresas de software de cualquier país salvo una lista de excluidos; RD **no** está en esa lista ([países admitidos](https://www.paddle.com/help/start/intro-to-paddle/which-countries-are-supported-by-paddle)). Paddle aprueba cada cuenta caso a caso: **confirmar con Paddle** la cuenta y el cobro a un banco dominicano antes de anunciar el pago en línea.

## Catálogo

Los códigos son los de `Quotas:<Plan>` en `src/Hosts/Toki.Host.Cloud/appsettings.json` (TK-248, `docs/cuotas.md`). Cada plan y cada adicional es un **producto** en Paddle con un **precio mensual** y, si se ofrece, uno **anual**. Los precios los decide el dueño (TK-203); aquí van como `[PRECIO]`.

| Código | Nombre | Qué incluye en la licencia (`Billing:Plans`) | Mensual | Anual |
|---|---|---|---|---|
| `Default` | Una caja | 1 caja además de la PC principal; carnicería, mayoreo, respaldo en la nube, asistente | [PRECIO] | [PRECIO] |
| `MultiRegister` | Varias cajas | Lo mismo con `Registers` = 3 (**provisional**, [CANTIDAD] por confirmar en TK-203) | [PRECIO] | [PRECIO] |
| `MultiStore` | Varias sucursales | Lo mismo con `Registers` = 3 por tienda (**provisional**); cada tienda tiene su licencia | [PRECIO] | [PRECIO] |
| `SupplierInvoiceReading` (adicional) | Lectura de facturas con IA | 300 facturas al mes (`Quotas:AddOns`) y la función `SupplierInvoiceReading` en la licencia | [PRECIO] | [PRECIO] |

- **Cajas adicionales** como adicional (por ejemplo "caja extra" con cantidad): no se construye ahora. Los planes ya fijan las cajas; si el dueño lo quiere, sería un precio con `quantity` y sumar `quantity` a `Registers` al emitir la licencia.
- **Mensual o anual:** el dueño elige al pagar (el panel muestra "Mensual / Anual" solo si hay algún precio anual). Una suscripción existente conserva su forma de pago; cambiar de plan o agregar un adicional se **prorratea al momento** (`proration_billing_mode: prorated_immediately`).
- Un precio sin id en `Billing:Paddle:Prices` no se vende de esa forma (el panel no lo muestra).

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
| `Billing:Paddle:Prices:<Código>:Monthly` / `:Annual` | Id del precio (`pri_…`) de cada plan o adicional |
| `Billing:License:KeyId` | URI de la llave de licencias en Key Vault; vacío = la nube no emite licencias |
| `Billing:License:MarginDays` | Días de margen tras "pagado hasta" (3) |
| `Billing:Plans:<Plan>:Name` / `:Registers` / `:Features` | Lo que la licencia dice de cada plan |

Sandbox y en vivo son **cuentas separadas** con sus propias llaves, tokens e ids de precio ([sandbox](https://developer.paddle.com/build/tools/sandbox)); las llaves de una no sirven en la otra.

## Lo que debe hacer el dueño, paso a paso

1. **Cuenta de Paddle:** crear la cuenta de sandbox (`sandbox-vendors.paddle.com`) para probar y solicitar la cuenta en vivo (`vendors.paddle.com`); completar la verificación del negocio y la cuenta de cobro. Confirmar con Paddle el cobro a un banco dominicano.
2. **Dominio:** en Paddle → Checkout → configuración del checkout, poner el enlace de pago por defecto en el dominio del panel (`https://<panel>/app/billing`) y, en vivo, pedir la aprobación del dominio.
3. **Productos y precios** (Paddle → Catálogo): un producto por plan (Una caja, Varias cajas, Varias sucursales) y uno por adicional (Lectura de facturas con IA). En cada uno, un precio **mensual** (`month`/1) y, si se ofrece, uno **anual** (`year`/1), en USD, **sin** período de prueba, con el `tax_mode` elegido. Anotar cada `pri_…`. Decidir los precios ([PRECIO], TK-203) y confirmar las cajas de "Varias cajas" y "Varias sucursales" (`Billing:Plans`).
4. **Token del cliente** (Paddle → Herramientas para desarrolladores → Autenticación): crear un *client-side token* (`test_…` en sandbox).
5. **Llave de API** (misma pantalla): crear una con permisos de lectura y escritura de suscripciones y `customer_portal_session.write`. Copiarla una vez.
6. **Destino de notificaciones** (Paddle → Eventos/Notificaciones → Nuevo destino): URL `https://<api>/billing/paddle/webhook` (salida `apiUrl` del despliegue), tipo webhook, eventos `subscription.*` y `transaction.completed`. **Copiar el secreto (`pdl_ntfset_…`) al crearlo**: Paddle puede no volver a mostrarlo.
7. **Secretos en Key Vault** (nunca en el repositorio ni en los parámetros):
   ```powershell
   az keyvault secret set --vault-name $kv --name Billing--Paddle--ApiKey --value "<llave de API>"
   az keyvault secret set --vault-name $kv --name Billing--Paddle--WebhookSecret --value "<secreto del destino>"
   ```
8. **Llave de licencias:** los comandos de la sección anterior; pegar el PEM en `TokiCloudPublicKey` y publicar la actualización de la tienda.
9. **Desplegar** con los parámetros nuevos de `deploy/azure/main.parameters.json`: `paddleEnvironment` (`sandbox` primero), `paddleClientToken`, `paddlePrices` (`{ "Default": { "Monthly": "pri_…", "Annual": "pri_…" }, … }`) y `licenseKeyName`.
10. **Probar en sandbox:** crear una cuenta en el panel, "Plan y facturación" → Elegir plan, pagar con la tarjeta de prueba `4242 4242 4242 4242` (rechazo: `4000 0000 0000 0002`); ver el estado "Al día", el plan en la consola y la licencia en la tienda conectada (Configuración → Licencia). Probar "Administrar pago" y una cancelación.
11. **Pasar a vivo:** repetir 3 a 7 en la cuenta en vivo y desplegar con `paddleEnvironment = live`.
