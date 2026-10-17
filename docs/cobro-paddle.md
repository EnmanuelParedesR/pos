# Cobro de Abastalia con Paddle Billing (TK-249)

Cómo cobra Abastalia sus planes en línea: Paddle Billing como **comerciante registrado** (*merchant of record*), el catálogo de planes y servicios adicionales, qué hace la nube con cada aviso de Paddle, cómo se emite sola la licencia de la tienda y qué debe configurar el dueño. El código está en `src/Hosts/Abastalia.Host.Cloud/Billing/`, la página del panel en `src/Cloud/panel/src/pages/BillingPage.tsx`.

**Mientras `Billing:Paddle` esté vacío, el cobro está apagado** (así viene): la página "Plan y facturación" muestra el plan y el enlace para escribirnos, el webhook responde 404 y los planes los sigue poniendo el personal de Abastalia en la consola (TK-248).

## Por qué Paddle

- Paddle vende como comerciante registrado: el cliente le compra a Paddle, que cobra, calcula y paga los impuestos de venta de cada país y le liquida a Abastalia. Abastalia no necesita pasarela local ni registrar impuestos en el exterior ([Paddle: impuestos y monedas](https://developer.paddle.com/concepts/sell/supported-currencies.md)).
- República Dominicana es un país comprador admitido (USD, precios sin impuesto incluido) ([países y monedas](https://developer.paddle.com/concepts/sell/supported-countries-locales.md)).
- Como **vendedor**, Paddle trabaja con empresas de software de cualquier país salvo una lista de excluidos; RD **no** está en esa lista ([países admitidos](https://www.paddle.com/help/start/intro-to-paddle/which-countries-are-supported-by-paddle)). Paddle aprueba cada cuenta caso a caso: **confirmar con Paddle** la cuenta y el cobro a un banco dominicano antes de anunciar el pago en línea.

## Catálogo

**Paddle es la fuente del catálogo.** Cada plan y cada adicional es un **producto** en Paddle con un **precio mensual** y, si se ofrece, uno **anual**. Precios, moneda, forma de pago, cuotas, límites por minuto, cajas y funciones se editan **en Paddle**; el código no tiene montos ni ids de precio. La nube lee el catálogo (`GET /products?include=prices`, todas las páginas) al iniciar, cada `Billing:Paddle:CatalogSyncMinutes` (60) y en cuanto llega un webhook `product.*` o `price.*`; lo guarda en `cloud.PaddleProducts` (la última lectura buena sigue valiendo si Paddle no responde). El panel ("Plan y facturación") y la página pública **/precios** (`GET /api/public/prices`, sin cuenta) muestran los precios reales; con el cobro apagado, /precios y el inicio muestran los provisionales de `site.prices.monthlyUsd` en `src/Cloud/panel/src/pages/site/config.ts`, marcados "Precios provisionales" (TK-293; también van en las `offers` del JSON-LD del inicio, en USD). Al cambiar un precio en Paddle, cambiarlo también ahí.

**Cómo se muestra (TK-313):** el monto grande va en **RD$**, a la tasa `site.prices.dopPerUsd` de `config.ts` (RD$61.5 por US$1; revisarla cada trimestre), redondeado a RD$5 (a RD$10 desde RD$1,000): "RD$925 al mes". Debajo, en pequeño, lo que cobra Paddle: "Se cobra US$15 con tarjeta. Precio final." Igual con los precios de Paddle (`livePrices.ts`) y con los provisionales.

Un producto es de Abastalia solo si su `custom_data` tiene la clave `abastalia` (los demás se ignoran). Los códigos son los de TK-248 (`docs/cuotas.md`). Los precios los decide el dueño (TK-203); los de abajo se aprobaron el 16/10/2026 (TK-258) y se ajustaron el 17/10/2026 (TK-313, decidido por delegación del dueño 17/10/2026).

### Esquema de `custom_data.abastalia`

```json
{
  "abastalia": {
    "plan": "MultiRegister",
    "quotas": { "AssistantQuestions": 600, "AssistantTokens": 6000000, "SupplierInvoices": 20, "Emails": 600, "ArchiveRetrievals": 100, "AccompanimentRequests": 3, "Invites": 5 },
    "rateLimits": { "panelPerMinute": 120, "assistantPerMinute": 10, "supplierInvoicesPerMinute": 10 },
    "registers": 3,
    "stores": 1,
    "features": ["ButcherModule", "WholesaleModule", "CloudBackup", "AiAssistant", "AssistantActions"]
  }
}
```

| Clave | Qué es |
|---|---|
| `plan` **o** `addOn` | El código: `Essential`, `Default`, `MultiRegister`, `MultiStore`; o `SupplierInvoiceReading`, `AssistantPack`, `ExtraRegister`, `ExtraStore`, `ExtraUsers`, `ExtraCashier` |
| `quotas` | Límites del mes por medidor (`AssistantQuestions`, `AssistantTokens`, `SupplierInvoices`, `Emails`, `ArchiveRetrievals`, `AccompanimentRequests`, `Invites`). En un adicional **se suman** al plan, una vez por unidad comprada (TK-258) |
| `rateLimits` | Por minuto: `panelPerMinute` (API del panel, por usuario), `assistantPerMinute` (preguntas al asistente, por usuario), `supplierInvoicesPerMinute` (facturas leídas, por caja). En un adicional también se sumarían: por eso los adicionales no los ponen |
| `registers` | Cajas además de la PC principal que pone la licencia. En un adicional (`ExtraRegister`), se suman por unidad |
| `stores` | Tiendas del negocio que reciben licencia de la nube (1 si nadie lo dice). En un adicional (`ExtraStore`), se suman por unidad |
| `cashiers` | TK-277: cajeros que la tienda puede tener habilitados (el Dueño no cuenta). En un adicional se suman por unidad: `ExtraCashier` 1 (Paddle); `ExtraRegister` 2 y `ExtraStore` 4 salen de `Billing:AddOns` |
| `features` | Funciones de la licencia (`ButcherModule`, `WholesaleModule`, `CloudBackup`, `AiAssistant`, `SupplierInvoiceReading`, `AssistantActions`). Todos los planes llevan todas: un `features` en Paddle sin `AssistantActions` apaga las propuestas de Talia en ese plan (TK-338) |

**Orden:** `custom_data` de Paddle &gt; appsettings (`Quotas:<Plan>`, `Quotas:<Plan>:RateLimits`, `Billing:Plans:<Plan>`, `Quotas:AddOns:<Adicional>`, `Billing:AddOns:<Adicional>`) &gt; lo de `Default` (Paddle, luego appsettings) &gt; el valor global (`Panel:RequestsPerMinutePerUser`, `Ai:AssistantQuestionsPerMinutePerUser`, `Ai:SupplierInvoicesPerMinutePerNode`). Lo que falte en Paddle sale de appsettings: se puede empezar solo con `plan` y completar después. Los nombres de las claves no distinguen mayúsculas. Un cambio en Paddle aplica en la siguiente lectura del catálogo (o al instante con el webhook); los límites por minuto de un negocio siguen a su plan en menos de un minuto.

### Planes y adicionales (precios en USD, TK-313: 17/10/2026)

Los planes son **tamaños**, no tipos de negocio: **todos tienen todas las funciones** (balanza y carnicería, mayoreo, e-CF, inventario, respaldo, asistente). Cada cliente se ajusta con adicionales. Anual = 10 × mensual. Precio final: impuesto incluido (`tax_mode` `internal`).

| Código | Nombre | Qué incluye | Mensual | Anual |
|---|---|---|---|---|
| `Essential` | Esencial | Solo la PC principal (`registers` 0), **2 cajeros**, 2 usuarios del panel (`Invites`), 100 preguntas al asistente, 20 facturas leídas | US$15 | US$150 |
| `Default` | Estándar | PC principal + 1 caja, **4 cajeros**, 3 usuarios, 300 preguntas, 20 facturas | US$19 | US$190 |
| `MultiRegister` | Pro | PC principal + 3 cajas, **8 cajeros**, 5 usuarios, 600 preguntas, 20 facturas, soporte más rápido | US$35 | US$350 |
| `MultiStore` | Varias sucursales | **Ya no se vende** (sus precios están archivados en Paddle). Sigue funcionando para quien lo tenga y en la consola: 3 cajas, 2 tiendas, 12 cajeros, 10 usuarios, 1,500 preguntas | — | — |

| Adicional (con cantidad) | Nombre | Qué suma por unidad | Mensual | Anual |
|---|---|---|---|---|
| `ExtraRegister` | Caja adicional | 1 caja y 2 cajeros en la licencia (`Billing:AddOns:ExtraRegister:Registers` / `:Cashiers`) | US$6 | US$60 |
| `ExtraCashier` | Cajero adicional | 1 cajero en la licencia (`custom_data.abastalia.cashiers`, o `Billing:AddOns:ExtraCashier:Cashiers`) | US$2 | US$20 |
| `ExtraStore` | Sucursal adicional | 1 tienda más con licencia de la nube, con las cajas del plan, y 4 cajeros (`Billing:AddOns:ExtraStore:Stores` / `:Cashiers`). **Solo con Pro** (o Varias sucursales): /precios lo dice y el panel no lo ofrece en otro plan (TK-313) | US$25 | US$250 |
| `SupplierInvoiceReading` | Lectura de facturas | 300 facturas al mes (`Quotas:AddOns`) y la función `SupplierInvoiceReading` en la licencia | US$5 | US$50 |
| `AssistantPack` | Asistente +1,000 preguntas | 1,000 preguntas y 10,000,000 tokens al mes | US$5 | US$50 |
| `ExtraUsers` | Usuarios del panel +5 | 5 invitaciones al mes (`Invites`) | US$3 | US$30 |

- **Cantidades:** cada precio de adicional admite cantidad 1 a 50 en Paddle (los planes, solo 1). El panel ("Plan y facturación") pide un número por adicional; el checkout y el cambio de suscripción (`PATCH /subscriptions/{id}`) llevan `quantity`, y el webhook la lee de `items[].quantity`. En la nube se guarda en `Tenants.AddOns` como `ExtraRegister:2,SupplierInvoiceReading`; la API lo devuelve una vez por unidad (`["ExtraRegister", "ExtraRegister", "SupplierInvoiceReading"]`) y la consola usa lo mismo.
- **Atajo:** una caja adicional suma una caja en **cada** licencia del negocio (las licencias son por tienda). Con varias tiendas, el dueño recibe una caja más en cada una.
- **Tiendas:** la nube emite licencia solo a las tiendas más antiguas del negocio que cubre su plan (1, o 2 con `MultiStore`) más una por `ExtraStore`. Las demás no reciben licencia de la nube (siguen con su prueba o una licencia fuera de línea). Crear tiendas en el panel no tiene límite.
- **Usuarios del panel:** se limitan con la cuota mensual de invitaciones (`Invites`), no con un tope de miembros.
- **Pagos únicos, fuera de Paddle (TK-313):** se cobran por transferencia o enlace de pago y se registran con el acuerdo manual de la consola; no hay producto en Paddle. Instalación por su cuenta y primera instalación remota asistida (hasta 45 min): gratis. Visita a la tienda (Gran Santo Domingo y Santiago, hasta 2 h): US$45. Sesión remota de soporte extra: US$15 por 30 min. Acompañamiento en la certificación: US$49, gratis con plan anual; **promoción de lanzamiento:** gratis con cualquier plan pagado antes del 31/12/2026 (`site.prices.launchPromo`). Textos en `site.prices.installation` y `site.prices.certificationSupport`.
- **Cajeros (TK-277, D20):** la licencia lleva `Cashiers` = los del plan + 1 por `ExtraCashier` + 2 por `ExtraRegister` + 4 por `ExtraStore`. Un cajero es un usuario **activo** cuyo rol puede **vender o abrir turno** (`sales.create` o `register.open`); el Administrador (el Dueño) no cuenta, ni quien no vende (Contador, Inventario). La tienda solo frena **habilitar** a uno más: crear un usuario, activarlo, darle un rol que vende o hacer que un rol venda (`Identity.CashierLimitReached`, con cómo agregar más). **Nunca** frena entrar, vender, cuadrar ni el e-CF. Desactivar a alguien (Configuración → Usuarios) libera su lugar y guarda su historial. Sin licencia (prueba): sin tope. Restringido: 2 (el Esencial). Una licencia sin `Cashiers` (emitida antes) no limita. Se cobra por cantidad adelantada, no por "los que entraron en el mes".
- **Señal de PIN compartido (TK-277):** el mismo usuario trabajando en dos cajas a la vez (entró en una mientras seguía usando la otra; un cajero no puede tener dos turnos abiertos, así que se miran las sesiones). El Resumen de la administración lo avisa (últimos 7 días) y Salud queda "Degradado" (`shared-pin`, último día; llega a la nube con el resumen de salud). No bloquea nada.
- **Mensual o anual:** el dueño elige al pagar (el panel muestra "Mensual / Anual" solo si hay algún precio anual). Una suscripción existente conserva su forma de pago; cambiar de plan, agregar un adicional o cambiar su cantidad se **prorratea al momento** (`proration_billing_mode: prorated_immediately`).
- Un plan o adicional sin precio activo en Paddle no se vende de esa forma (el panel no lo muestra; /precios dice "Precio por confirmar").

### Catálogo en sandbox (actualizado por API el 16/10/2026, TK-258, y el 17/10/2026, TK-313)

Cuenta **sandbox** solamente (la cuenta en vivo no se tocó). Los nombres ya no llevan `[SANDBOX]`; la descripción de cada producto dice que es de prueba. Mensual (`month`/1) y anual (`year`/1), USD, sin período de prueba, `tax_category` `standard`. Los precios de US$1 anteriores están archivados. Los ids no son secretos.

**TK-313 (17/10/2026):** `PATCH /prices/{id}` cambió el `unit_price` en el mismo precio (Paddle lo permite; los ids **no cambian**): Esencial 1200 → **1500** y 12000 → **15000**; Lectura de facturas 700 → **500** y 7000 → **5000**. Los 18 precios activos pasaron de `tax_mode` `location` a **`internal`** (precio con impuesto incluido). Todos respondieron 200. `custom_data` no cambió.

| Producto | `pro_…` | Mensual `pri_…` | Anual `pri_…` |
|---|---|---|---|
| Abastalia Esencial (`Essential`) | `pro_01m5323y460bqh5tb49p4g1srm` | `pri_01m5323yc3g0yz6r26gdskc9xd` | `pri_01m5323yhzv42f88hqff8zg3v3` |
| Abastalia Estándar (`Default`) | `pro_01m52qsss2vbns44aq8tpq86db` | `pri_01m5323z2vtsnwkt0anan2gxkr` | `pri_01m5323z7hr2jjptq2a2jqz5mk` |
| Abastalia Pro (`MultiRegister`) | `pro_01m52qstn30dkwfbw2bgfqht0q` | `pri_01m5323zqzh3nq4zbae9nsdhcq` | `pri_01m5323zynej7c3pn3cqhycfyf` |
| Abastalia Varias sucursales, ya no se vende (`MultiStore`) | `pro_01m52qsvck6hgsdcs0v684t8wk` | — (archivado) | — |
| Lectura de facturas (`SupplierInvoiceReading`) | `pro_01m52qsw71pk44xk5sbg0w2acw` | `pri_01m53240ke8sbngg3pmn1y4wba` | `pri_01m53240q53sbrfhj3a6ct507s` |
| Asistente +1,000 preguntas (`AssistantPack`) | `pro_01m53240wym7nx482yy7bhqz7c` | `pri_01m532410jyx8r99dh4zzqgz13` | `pri_01m532414n7dvc5g7nnwqjsehp` |
| Caja adicional (`ExtraRegister`) | `pro_01m53241a7he8pdafkha0f5bf5` | `pri_01m53241dzbemwa2cqm1q6pjqr` | `pri_01m53241hvz9m9bq9vrgc43w3m` |
| Sucursal adicional (`ExtraStore`) | `pro_01m53241pvk1e98w36hfn4vm2w` | `pri_01m53241t4d2ttqc7sbd4xxjn8` | `pri_01m53241y6rpq9garjwdtgxmdt` |
| Usuarios del panel +5 (`ExtraUsers`) | `pro_01m532423p3mm7nqxskg3ypgah` | `pri_01m532427ffcj1t2070763nzzc` | `pri_01m53242b3g7nyrfx038gg2c60` |
| Cajero adicional (`ExtraCashier`, TK-277) | `pro_01m53czk4tsmp3jekgrjqgrb5w` | `pri_01m53czkd29gwfrj0mxw2wmr73` | `pri_01m53czkk7275mxgd5k7b1zn4v` |

El de cajero se creó por API el 16/10/2026 con `custom_data` `{"abastalia":{"addOn":"ExtraCashier","cashiers":1}}`, US$2 / US$20, cantidad 1 a 50 (`tax_mode` `internal` desde TK-313). **Falta en vivo:** crear el mismo producto y precios en la cuenta en vivo.

Archivados (los de US$1): `pri_01m52qst9fkm4a8q7gxapvsky5`, `pri_01m52qsv39f4v4zb47dq40pbtm`, `pri_01m52qsvtt6fkzgkd5v02xazw9`, `pri_01m52qswns3mx99nfsb6nqn2we`.

## Prueba de 14 días

La prueba de Abastalia **no** es de Paddle: la tienda empieza sus 14 días con la **primera venta** real (TK-196) y no pide tarjeta. Si el negocio llegó recomendado, son **30 días** (TK-276, ver Recomendaciones). Por eso los precios de Paddle se crean **sin** `trial_period`. Si algún día se quiere cobrar con prueba en Paddle (`trial_period` con `requires_payment_method`, [crear precio](https://developer.paddle.com/api-reference/prices/create-price.md)), la nube ya entiende el estado `trialing`: la licencia cubre hasta el fin de la prueba.

## Impuestos, facturas y reembolsos

- **Impuestos:** los calcula y paga Paddle; el cliente recibe la factura de Paddle. Cada precio tiene `tax_mode` (`account_setting` por defecto; `internal` = precio con impuesto incluido; `external` = impuesto aparte). Abastalia usa **`internal`** (TK-313): el precio publicado es el final.
- **Verificación para RD (TK-313, sandbox, 17/10/2026):** `POST /pricing-preview` con `address.country_code` `DO` y `currency_code` `USD`, un artículo por vez. Paddle respondió **`tax_rate` 0 e impuesto US$0.00**: Esencial mensual US$15.00 (subtotal 1500, tax 0, total 1500), Esencial anual US$150.00, Estándar mensual US$19.00 y Lectura de facturas US$5.00, todos sin impuesto. Para comparar, el mismo Esencial con país `GB` dio `tax_rate` 0.2: subtotal US$12.50 + impuesto US$2.50 = **US$15.00**, o sea que `internal` sí incluye el impuesto donde Paddle lo cobra. Conclusión: hoy Paddle **no agrega ITBIS** a un cliente de RD y Abastalia recibe el precio completo (menos la comisión de Paddle). **Repetir la vista previa en la cuenta en vivo** antes de anunciar: si Paddle empieza a cobrar impuesto en RD, el cliente sigue pagando lo mismo y baja el neto.
- **Reembolsos:** desde el panel de Paddle (o `POST /adjustments` con `action: refund`); en vivo, Paddle los aprueba antes de devolver ([ajustes](https://developer.paddle.com/api-reference/adjustments/overview)). Abastalia no reembolsa desde su panel. Un reembolso no quita días pagados por sí solo: si corresponde, cancelar la suscripción en Paddle (la nube la verá como `canceled`).

## Pagos atrasados: pendiente → gracia → restringido → bloqueado (TK-279)

1. Paddle no logra cobrar la renovación: la suscripción pasa a `past_due` y Paddle **reintenta** (por defecto hasta 7 veces en 30 días, [cambio de 2025](https://developer.paddle.com/changelog/2025/default-dunning-payment-recovery); otra página habla de una semana, [renovación y cobro](https://developer.paddle.com/build/lifecycle/subscription-renewal-dunning): revisarlo en la cuenta).
2. En la nube: estado **Pago pendiente**; el plan y sus cuotas **se mantienen** mientras Paddle reintenta. "Pagado hasta" **no** avanza (solo avanza con un pago o con la suscripción `active`).
3. En la tienda: su licencia vence en "pagado hasta" + 3 días (`Billing:License:MarginDays`), luego **15 días de gracia** con todo encendido, después **Restringido** (funciones pagas apagadas, una caja además de la PC principal; todavía vende) y, a los `Billing:License:BlockAfterDays` (30) del vencimiento, **Bloqueada**: no vende, no abre turnos ni emite e-CF nuevos, pero cierra el turno abierto, envía a la DGII lo ya emitido, exporta, reporta y respalda (`docs/licencias.md`). Esto **cambia** la regla de TK-220 ("Restringido vende para siempre"). Quedarse sin internet no sirve para seguir gratis: la licencia firmada vence sola y atrasar el reloj de la PC no la estira.
4. Si un reintento cobra: `transaction.completed` y `subscription.updated` (activa) → la nube extiende "pagado hasta" y la tienda se levanta en su próxima consulta.
5. Si todos fallan: Paddle cancela (`subscription.canceled`) → el negocio vuelve al plan `Default` sin adicionales en la nube, salvo que el personal tenga un acuerdo manual vigente. Sin tiempo pagado la nube deja de entregar licencias, así que la tienda llega sola a Bloqueada por vencimiento.
6. Al pagar (o con una licencia fuera de línea), la licencia nueva la desbloquea al momento, con todas sus ventas.

## Qué hace la nube

### Webhook `POST /billing/paddle/webhook`

- **Firma:** encabezado `Paddle-Signature: ts=…;h1=…`; se calcula HMAC SHA-256 de `ts:cuerpo` con el secreto del destino y se compara en tiempo constante con cada `h1` ([verificación de firma](https://developer.paddle.com/webhooks/signature-verification)). Se acepta un `ts` dentro de **5 minutos** (los SDK de Paddle usan 5 segundos; aquí se tolera un reintento lento y la hora desfasada, y la repetición la frena el `event_id`). Firma mala, ausente o vieja: **401**.
- **Una sola vez por `event_id`** (tabla `cloud.BillingEvents`): Paddle reintenta hasta recibir 2xx (en vivo 60 veces en 3 días) y un reenvío se contesta 200 sin repetir nada ([responder a webhooks](https://developer.paddle.com/webhooks/about/respond-to-webhooks)).
- **Orden:** Paddle no garantiza el orden; un evento de suscripción con `occurred_at` anterior al último aplicado se registra pero no se aplica.
- **A qué negocio:** primero el id de suscripción ya conocido; si es nueva, `custom_data.tenantId`, que pone nuestro checkout y Paddle copia a la suscripción, **solo con su `tenantSig`** (HMAC-SHA256 del id con el secreto del webhook, R12 I): el navegador entrega `customData` a Paddle.js y podría cambiarlo.
- `subscription.created/updated/activated/trialing/past_due/paused/resumed/canceled` → estado, cliente, forma de pago, próximo cobro, "pagado hasta" (solo si está activa o en prueba) y **el plan y los adicionales del negocio** (los mismos campos que pone la consola en TK-248) según los precios de `items[].price.id`.
- `transaction.completed` → "pagado hasta" = fin de `billing_period` si es mayor, y una suscripción pendiente vuelve a activa.
- `product.*` y `price.*` → el catálogo se relee enseguida (precios y límites nuevos).
- Tablas: `cloud.TenantSubscriptions` (una por negocio) y `cloud.BillingEvents` (migración `Billing`).

### API del panel (solo Dueños)

| Ruta | Qué hace |
|---|---|
| `GET /api/billing` | Plan, adicionales, estado, pagado hasta, próximo cobro, acuerdo manual, si hay cliente de Paddle, y los planes y adicionales que tienen precio |
| `POST /api/billing/checkout` | Sin suscripción: devuelve lo que Paddle.js necesita (`environment`, `clientToken`, `items` con `priceId`, correo del dueño y `customData.tenantId` con `tenantSig`). Con suscripción viva: cambia sus `items` en Paddle (`PATCH /subscriptions/{id}`, prorrateado) y responde `{ changed: true }` |
| `POST /api/billing/portal` | Crea una sesión del portal del cliente (`POST /customers/{id}/portal-sessions`) y devuelve el enlace (tarjeta, facturas, cancelar). El enlace lleva un token temporal: no se guarda ([portal](https://developer.paddle.com/api-reference/customer-portals/create-customer-portal-session)) |

### Página "Plan y facturación" (`/app/billing`, Dueños)

Plan, estado ("Al día", "Pago pendiente", …), pagado hasta, próximo cobro, enlace a "Consumo del mes" (TK-248) y los botones **Elegir plan**, **Agregar lectura de facturas** y **Administrar pago**. Paddle.js se carga **solo en esta página**, desde `https://cdn.paddle.com/paddle/v2/paddle.js` (Paddle pide cargarlo siempre de su CDN; la versión la fija la ruta `/v2/`, [incluir Paddle.js](https://developer.paddle.com/paddlejs/include-paddlejs)). Con `Environment = sandbox` llama `Paddle.Environment.set("sandbox")` antes de `Paddle.Initialize({ token })` ([entorno](https://developer.paddle.com/paddlejs/methods/paddle-environment-set)) y abre `Paddle.Checkout.open({ items, customer: { email }, customData })` en modo superpuesto ([abrir checkout](https://developer.paddle.com/paddlejs/methods/paddle-checkout-open)).

La página pública **/precios** lleva a "Crear su cuenta y elegir plan" (`/app/billing`: si no hay sesión, primero pide crear la cuenta o entrar).

### Consola de la plataforma

En el cliente: tarjeta **Suscripción** con estado, ids de Paddle, pagado hasta, próximo cobro, pendiente desde y los últimos eventos; y el **acuerdo manual**: "pagado hasta" con motivo obligatorio (ofertas, pagos por transferencia). Cada cambio queda en `PlatformAudit` como toda llamada de la plataforma. El plan y los adicionales del acuerdo se ponen en "Plan y consumo" (TK-248).

**Bloquear negocio (TK-279):** en la misma tarjeta, con motivo obligatorio (`PUT /api/platform/tenants/{id}/block`, `{ blocked, reason }`). Todas sus tiendas reciben en su próxima consulta (TK-287: con su próxima subida de datos; una bloqueada pregunta cada 5 minutos) una licencia con `blocked: true` y quedan Bloqueadas. **Desbloquear negocio** (con motivo) vuelve a la licencia de la suscripción; un pago en Paddle no quita un bloqueo puesto a mano. Sin tiempo pagado, ponga también un acuerdo al desbloquear: si no, la tienda sigue bloqueada (la tarjeta lo avisa antes y después de desbloquear). La IA de facturas se niega a un negocio bloqueado.

## Recomendaciones: "Recomiende Abastalia" (TK-255)

**Regla del dueño:** recomendar da un **descuento en el próximo pago de quien recomienda**, y solo cuando el negocio recomendado **paga** un plan que califica (crear la cuenta no da nada). Apagado mientras `Billing:Referrals:Amount` esté vacío o el cobro con Paddle esté apagado.

**Valores aprobados (D19, 16/10/2026, TK-276):** califican `Essential`, `Default` y `MultiRegister`; descuento fijo de **US$15** (`flat`, `1500`, `USD`; TK-313: un mes gratis de Esencial; antes US$12); hasta **3 por mes** por negocio. Quien llega recomendado tiene **30 días** de prueba (`ReferredTrialDays`). Con plan anual, las recompensas se juntan al renovar, hasta el **50 %** del plan (`AnnualCapPercent`). Están en `appsettings.json`; en producción siguen apagadas mientras `Billing:Paddle` no esté configurado.

- **Prueba de 30 días (TK-276):** al entrar al panel, el navegador envía el código guardado de `/precios?ref=` (`POST /api/billing/referrals/claim`, Dueños) y la nube lo guarda en el negocio, solo en sus primeros 14 días y con las mismas reglas que el checkout (nunca el propio, nunca después del primer pago, nunca otro). La tienda conectada lo sabe por el canal de su licencia: `POST /api/nodes/license` responde también `trialDays: 30`, y la tienda guarda ese largo (`settings.Business.TrialDays`) y alarga su prueba; nunca la acorta. Una tienda sin conexión a la nube se queda con 14.
- **Plan anual de quien recomienda (TK-276):** el siguiente pago es la renovación, así que en vez de una recompensa por ciclo se **juntan**: cada recompensa nueva reemplaza el descuento ya puesto para la renovación por uno solo con todas (`Abastalia: N recomendaciones`), hasta el 50 % del precio del plan (o del por ciento, si el tipo es `percentage`); las que no caben esperan a la siguiente renovación. Si se revierte una, el descuento se quita y las demás se vuelven a juntar sin ella.

- **Código y enlace:** cada negocio tiene un código de 8 letras y números (sin 0/O, 1/I/L), creado la primera vez que el Dueño abre "Plan y facturación". Enlace: `https://<sitio>/precios?ref=CÓDIGO`. /precios comprueba el código (`GET /api/public/referrals/{código}`, sin cuenta, `Billing:Referrals:LookupsPerMinute` por IP contra quien adivina códigos) y lo guarda en el navegador; el primer checkout lo envía (`referralCode`) y la nube lo **guarda en el negocio** (`TenantSubscriptions.ReferredByCode`; nunca su propio código). El checkout lo pasa a Paddle en `customData`: `{ tenantId, referral_code, referrer_tenant_id }`.
- **Cuándo cuenta:** en `transaction.completed`, solo el **primer pago con monto** del negocio (`details.totals.grand_total` > 0; el checkout de una prueba en 0 no cuenta), y solo si algún precio comprado es de un plan que califica: su código o su `pri_…` en `Billing:Referrals:QualifyingPlans`, o el producto con `custom_data.abastalia.referral: true`. Si ese primer pago no califica, ese negocio ya no da recompensa. **Una recompensa por negocio recomendado, para siempre** (índice único) y cada evento una sola vez (`event_id`).
- **Tabla `cloud.Referrals`** (migración `Referrals`): quién recomendó, a quién, el pago (`TransactionId`), el descuento, y el estado **Pending** (pagó, falta aplicar), **Applied** (descuento puesto), **Reverted** (reembolso o ajuste) o **Rejected** (abuso o límite, con motivo).
- **Aplicar:** la recompensa pendiente más vieja se pone en la suscripción de quien recomienda: `POST /discounts` (`mode: custom`, `recur: false`, `usage_limit: 1`, `enabled_for_checkout: false`, `restrict_to` = los precios de su plan, no los adicionales), `PATCH /subscriptions/{id}/preview` y luego `PATCH /subscriptions/{id}` con `discount: { id, effective_from: "next_billing_period" }` ([descuentos](https://developer.paddle.com/api-reference/discounts/create-discount), [suscripción](https://developer.paddle.com/api-reference/subscriptions/update-subscription)). **Espera** (queda Pending) si su suscripción no está activa (pago pendiente, en pausa, cancelada), si Paddle ya tiene un descuento en ella, si ese próximo pago ya lleva uno nuestro (**uno por ciclo**) o si Paddle falla; se intenta de nuevo con cada evento `subscription.*` o `transaction.completed` de quien recomienda (o desde la consola).
- **Revertir:** `adjustment.created`/`adjustment.updated` con `action` `refund` o `chargeback` y `status: approved` sobre el pago que dio la recompensa: si estaba pendiente, Reverted; si estaba aplicada y quien recomienda **todavía no pagó** ese ciclo, se quita el descuento (`PATCH … { discount: null }`) y Reverted. Si Paddle falla, el webhook contesta 503 y Paddle reintenta. Si ya pagó con el descuento, queda para la consola. Incluir los eventos `adjustment.*` en el destino de notificaciones.
- **Abuso:** se rechaza (Rejected) recomendarse a sí mismo (mismo negocio), el **mismo pagador** (mismo cliente de Paddle, que es uno por correo, o un Dueño en ambos negocios) y el **mismo RNC** (el del receptor e-CF, ahora o antes); y más de `MonthlyLimitPerReferrer` recompensas por mes (UTC).
- **Panel** (Dueños, "Plan y facturación"): tarjeta **Recomiende Abastalia** con el enlace para copiar, cuántos pagaron y esperan su descuento, cuántos descuentos se aplicaron y el próximo descuento (en qué pago, o "en espera"). `GET /api/billing/referrals`.
- **Consola:** tarjeta **Recomendaciones** en el cliente (las que hizo y la que recibió) con **Revertir** y **Restablecer** (motivo obligatorio) y **Aplicar ahora**; `GET/POST /api/platform/tenants/{id}/referrals[/{referralId}]`, auditado en `PlatformAudit`.

## Licencias emitidas por la nube

Hasta ahora las licencias se firman fuera de línea con la llave del dueño (`docs/licencias.md`). Con TK-249 la nube también puede emitirlas, con **otra llave**:

- Una llave **EC P-256 en Azure Key Vault, no exportable**: la API firma con ella por Key Vault ("Key Vault Crypto User", ya asignado en `deploy/azure/main.bicep`) y la parte privada nunca sale de la bóveda.
- La tienda acepta licencias firmadas por **cualquiera de las dos** llaves públicas: la de siempre y la de la nube (`AbastaliaCloudPublicKey` en `src/Modules/Settings/Abastalia.Settings/License.cs`, vacía hasta crear la llave).
- La tienda conectada pide su licencia por el canal del nodo (`POST /api/nodes/license`, firmado con la llave de la caja) en cada latido (al iniciar y cada 4 horas), cuando la respuesta de una subida de datos trae un `licenseUtc` (último cambio de la suscripción) distinto del último que vio, y cada 5 minutos mientras está Bloqueada (TK-287), y la **instala sola** si es válida y **más nueva** (fecha de emisión) que la instalada. La fecha de emisión es la del último cambio de la suscripción, así que la misma licencia no se reinstala en cada consulta.
- Contenido: tienda, nombre del plan, funciones, cajas y cajeros de `Billing:Plans:<Plan>` (si un plan no lo define, toma lo de `Default`), más una caja por cada `ExtraRegister`, los cajeros de cada adicional (TK-277) y la función del adicional `SupplierInvoiceReading`. Solo la reciben las tiendas que cubre el plan más las `ExtraStore` (las más antiguas primero; TK-258). **Vence** en "pagado hasta" (Paddle o acuerdo manual) + 3 días; luego la gracia de 15 días de la tienda, Restringido y, a los 30 días del vencimiento, Bloqueada (TK-279).
- Sin tiempo pagado la nube no entrega licencia (una vencida reemplazaría una válida emitida fuera de línea).
- **Emisión fuera de línea:** sigue igual (tiendas sin internet, acuerdos especiales). Gana la licencia emitida más tarde, la firme quien la firme.

Comandos para crear la llave (los ejecuta el dueño; **no** se crearon recursos en Azure):

```powershell
# Bóveda del despliegue (salida keyVault de main.bicep)
$kv = "kv-abastalia-prod-xxxxxx"
# Quien la crea necesita "Key Vault Crypto Officer" en la bóveda (RBAC)
# Por defecto la llave no es exportable (exportarla exige una política de liberación que aquí no se pone)
az keyvault key create --vault-name $kv --name abastalia-license --kty EC --curve P-256 --ops sign verify
# La parte pública, en PEM, para pegarla en AbastaliaCloudPublicKey (License.cs) y publicar una actualización de la tienda
az keyvault key download --vault-name $kv --name abastalia-license --encoding PEM --file abastalia-license-public.pem
```

Luego desplegar con `licenseKeyName = abastalia-license` (pone `Billing__License__KeyId`). Rotar esta llave exige publicar antes una tienda con la nueva parte pública (la vieja sigue valiendo para las licencias ya emitidas hasta que venzan).

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
| `Billing:License:BlockAfterDays` | TK-279: días tras el vencimiento hasta Bloqueada; va firmado en la licencia (`blockAfterDays`). Vacío = 30 (lo que usa la tienda) |
| `Billing:Plans:<Plan>:Name` / `:Registers` / `:Features` | Lo que la licencia dice de cada plan |
| `Billing:Referrals:QualifyingPlans` | TK-255: códigos de plan o `pri_…` cuyo primer pago da la recompensa (además de `custom_data.abastalia.referral: true`). D19: `Essential`, `Default`, `MultiRegister` |
| `Billing:Referrals:DiscountType` / `:Amount` / `:Currency` | `percentage` (`Amount` = por ciento) o `flat` (`Amount` en la unidad mínima, `500` = US$5.00, con `Currency`). `Amount` vacío = recomendaciones apagadas. D19, TK-313: `flat`, `1500`, `USD` |
| `Billing:Referrals:MonthlyLimitPerReferrer` | Recompensas por negocio por mes (D19: 3) |
| `Billing:Referrals:ReferredTrialDays` | TK-276: días de prueba de la tienda de un negocio recomendado (30) |
| `Billing:Referrals:AnnualCapPercent` | TK-276: tope del descuento junto de un plan anual, en % del precio del plan (50) |
| `Billing:Plans:<Plan>:Cashiers` / `Billing:AddOns:<Adicional>:Cashiers` | TK-277: cajeros del plan (2 / 4 / 8 / 12) y por unidad de adicional (`ExtraCashier` 1, `ExtraRegister` 2, `ExtraStore` 4) |
| `Billing:Referrals:LookupsPerMinute` | Consultas públicas de código por IP por minuto (20) |

Sandbox y en vivo son **cuentas separadas** con sus propias llaves, tokens e ids de precio ([sandbox](https://developer.paddle.com/build/tools/sandbox)); las llaves de una no sirven en la otra.

## Lo que debe hacer el dueño, paso a paso

1. **Cuenta de Paddle:** crear la cuenta de sandbox (`sandbox-vendors.paddle.com`) para probar y solicitar la cuenta en vivo (`vendors.paddle.com`); completar la verificación del negocio y la cuenta de cobro. Confirmar con Paddle el cobro a un banco dominicano.
2. **Dominio:** en Paddle → Checkout → configuración del checkout, poner el enlace de pago por defecto en el dominio del panel (`https://<panel>/app/billing`) y, en vivo, pedir la aprobación del dominio.
3. **Productos y precios** (Paddle → Catálogo): un producto por plan (Esencial, Estándar, Pro) y uno por adicional (Caja adicional, Sucursal adicional, Lectura de facturas, Asistente +1,000 preguntas, Usuarios del panel +5), cada uno con su `custom_data.abastalia` (esquema arriba). En cada uno, un precio **mensual** (`month`/1) y, si se ofrece, uno **anual** (`year`/1), en USD, **sin** período de prueba, con el `tax_mode` elegido. En los adicionales, cantidad de 1 a 50. Los precios provisionales y las cuotas están en las tablas de arriba; ponerlos **en Paddle** (en sandbox ya están). No hace falta copiar ningún `pri_…` a la configuración.
4. **Token del cliente** (Paddle → Herramientas para desarrolladores → Autenticación): crear un *client-side token* (`test_…` en sandbox).
5. **Llave de API** (misma pantalla): crear una con permisos de lectura de productos y precios (`product.read`, `price.read`: la nube lee el catálogo con `GET /products?include=prices`), lectura y escritura de suscripciones, escritura de descuentos (TK-255) y `customer_portal_session.write`. Copiarla una vez.
6. **Destino de notificaciones** (Paddle → Eventos/Notificaciones → Nuevo destino): URL `https://<api>/billing/paddle/webhook` (salida `apiUrl` del despliegue), tipo webhook, eventos `subscription.*`, `transaction.completed`, `product.*` y `price.*` (catálogo al momento) y `adjustment.created`/`adjustment.updated` (reembolsos de recomendaciones, TK-255). **Copiar el secreto (`pdl_ntfset_…`) al crearlo**: Paddle puede no volver a mostrarlo.
7. **Secretos en Key Vault** (nunca en el repositorio ni en los parámetros):
   ```powershell
   az keyvault secret set --vault-name $kv --name Billing--Paddle--ApiKey --value "<llave de API>"
   az keyvault secret set --vault-name $kv --name Billing--Paddle--WebhookSecret --value "<secreto del destino>"
   ```
8. **Llave de licencias:** los comandos de la sección anterior; pegar el PEM en `AbastaliaCloudPublicKey` y publicar la actualización de la tienda.
9. **Desplegar** con los parámetros nuevos de `deploy/azure/main.parameters.json`: `paddleEnvironment` (`sandbox` primero), `paddleClientToken` y `licenseKeyName`.

**Hecho ya en sandbox (por API):** los nueve productos con sus precios provisionales y su `custom_data` (TK-258). **Configurado en sandbox el 17/10/2026:** token del lado del cliente "Panel Abastalia" (en `paddleClientToken` de `main.parameters.json`; no es secreto); destino de notificaciones `ntfset_01m55w7tmhe55ve4sqf6yzk6rk` a `https://api.abastalia.com/billing/paddle/webhook` con los 18 eventos del paso 6 y tráfico real y de simulación; enlace de pago por defecto `https://abastalia.com/app/billing` (dominio aceptado); `Billing--Paddle--ApiKey` y `Billing--Paddle--WebhookSecret` en `kv-abastalia-prod-5qsy5n`; los cuatro `Billing__Paddle__*` puestos en la API con `az webapp config appsettings set` (el despliegue completo de `main.bicep` quitaría categorías de registro agregadas a mano en `console-to-log-analytics`). La llave de API de sandbox tiene todos los permisos y **vence el 14/01/2027**: crear otra y cambiar el secreto antes. Para probar sin pagar: simulaciones de Paddle (Desarrollador → Simulaciones) contra ese destino. Para desarrollo local, la llave de API de sandbox va en los *user-secrets* de `src/Hosts/Abastalia.Host.Cloud` (nunca en el repositorio). **Falta:** la llave de licencias (paso 8) y la prueba de pago (paso 10).
10. **Probar en sandbox:** crear una cuenta en el panel, "Plan y facturación" → Elegir plan, pagar con la tarjeta de prueba `4242 4242 4242 4242` (rechazo: `4000 0000 0000 0002`); ver el estado "Al día", el plan en la consola y la licencia en la tienda conectada (Configuración → Licencia). Probar "Administrar pago" y una cancelación.
11. **Pasar a vivo:** repetir 3 a 7 en la cuenta en vivo y desplegar con `paddleEnvironment = live`.
