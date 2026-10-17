# Clientes y cuentas por cobrar (CRM parcial): ¿vale la pena? (TK-312)

Fecha: 17/10/2026. Rutas relativas a la raíz del repo.

## Respuesta corta

Sí, pero como **"Clientes y cuentas por cobrar"**, no como CRM. El 80 % ya está hecho y probado. Lo que falta para que el dueño lo note: **recordar el cobro por WhatsApp (enlace wa.me, gratis)**, **historial de compras del cliente** y **filtrar "quién debe y desde cuándo"**. Puntos, cumpleaños y segmentos: no por ahora. Costo: **4 a 6 días de desarrollo**.

## A. Lo que ya existe

**Ficha del cliente.** Nombre, RNC/cédula, teléfono, dirección, crédito fiscal (31) por defecto, nivel de precio (detalle/mayorista), límite de crédito, activo/inactivo (`src/Modules/Customers/Abastalia.Customers/Domain/Customers.cs:7-23`). RNC único (`:92`). TK-070 ✅ (`backlog/BACKLOG.md:97`).

**Ventas a crédito y cuentas por cobrar.** Cargo por venta, abonos a las facturas más viejas, saldo a favor (devoluciones y depósitos de encargos), anulación de abonos con historial (`Domain/Customers.cs:26-82`; `CustomersModule.cs:48-66`). Límite con supervisor para excederlo y bloqueo por cliente para que dos cajas no pasen el límite (`Contracts/Contracts.cs:17-19`; `Domain/Customers.cs:100-101`). TK-071 ✅ (`BACKLOG.md:98`).

**Antigüedad 0-30/31-60/61-90/+90** por cliente (`Abastalia.Customers.Contracts/Reports.cs:4-10`); reporte exportable `/api/reportes/por-cobrar` TK-084 ✅ (`BACKLOG.md:108`).

**Abono desde la caja** (F9, idempotente, recibo) TK-121 ✅ (`BACKLOG.md:160`) y desde Administración → Clientes (`src/Client/Abastalia.Client.UI/Pages/Admin/Customers.razor:201-285`), con aviso de límite pasado (`:116-118`), KPI Debe/Disponible (`:134-136`), antigüedad (`:149`) y estado de cuenta con balance corrido (`:161-190`).

**Estado de cuenta en una página para PDF**, con logo del negocio (`Customers.razor:200`, `Printables.cs:8-12`; TK-200 y TK-288, `BACKLOG.md:277,344`). Se comparte por WhatsApp **a mano** (descargar, imprimir a PDF, adjuntar). No hay botón de WhatsApp.

**Búsqueda en la caja.** Por nombre, RNC, cédula o teléfono, sin acentos en SQL Server, y alta rápida (`Components/CustomerDialog.razor:31-33,67,132`; `Features/Customers.cs:101-111`; `CustomersModule.cs:40`). RNC escrito para una sola factura y consulta del directorio de la DGII (`Fiscal/Features/TaxpayerRegistry.cs:203`; TK-108). e-NCF de reemplazo en el cargo tras un rechazo (TK-206).

**Panel del dueño (nube).** Tarjeta "Le deben (fiado)" con total, cantidad de clientes y los 5 que más deben (`src/Cloud/panel/src/pages/SummaryPage.tsx:22,153-160`; `Host.Cloud/Panel/PanelQueries.cs:177-183`). El asistente Talia tiene la herramienta `receivables` (`Assistant/Assistant.cs:132,209`). La nube solo recibe nombre y saldo (`Contracts.cs:47`; `LegalPages.tsx:188`). La consola de plataforma ya mide si una tienda usa crédito (`PlatformPages.tsx:41,649`): sirve para medir adopción en el piloto.

**Catálogo público con QR y pedido por WhatsApp** (wa.me, sin costo) TK-195 ✅ (`BACKLOG.md:262`; `CatalogPage.tsx:19,88`). El pedido llega al WhatsApp del negocio, no crea cliente en Abastalia.

**Referidos TK-255**: es "Recomiende Abastalia" entre negocios (descuento en la suscripción, `docs/cobro-paddle.md:148`; `docs/preguntas-abiertas.md:152`). **No es fidelización de clientes de la tienda.**

## A2. Lo que falta

| Falta | Evidencia |
|---|---|
| Historial de compras por cliente (contado y crédito) | `/api/sales` filtra por turno, fecha, número o e-NCF, no por cliente (`Sales/Features/Queries.cs:95-110`). El estado de cuenta solo muestra cargos a crédito y abonos. |
| Botón "Recordar por WhatsApp" / "Enviar estado de cuenta" | El teléfono existe (`Domain/Customers.cs:12`) pero no hay wa.me fuera del catálogo. Planeado como add-on (`docs/funcionalidades-v1.md:130`). |
| Lista "quién debe" ordenada y filtrada (más de 30/60 días, pasó el límite) | La lista muestra deuda y la antigüedad está en el reporte, pero no hay filtro rápido en la pantalla. |
| Detalle de clientes en el panel de la nube | Solo top 5; el estado de cuenta en el panel falta (TK-200, "Falta: el panel de la nube"). Nombres de abonos anulados sin evento (TK-167, `BACKLOG.md:238`). |
| Puntos, cumpleaños, segmentos | Nada en el código. Decidido: puntos V2 (`funcionalidades-v1.md:131`); cumpleaños descartado (`:133`). |
| Fecha de compromiso de pago | No existe en el modelo. |

**Riesgo de lenguaje:** el producto y el sitio dicen "fiado" en muchos lugares: errores del servidor (`Domain/Customers.cs:94,101`), panel (`SummaryPage.tsx:154`, `ShiftReportPage.tsx:14`), sitio (`SitePages.tsx:66,136,142,152,354,666,742,743,788,789,845`; `SiteParts.tsx:367-368`; `LegalPages.tsx:179,188`). Si la regla es no usar "fiado", hay que cambiar el texto (≈0.5 día).

## B. Mercado (resumen de la investigación)

Fuente: subagente de investigación (búsqueda web, 17/10/2026). **Las URL exactas de cada dato no llegaron en el resumen; verificar antes de publicar comparaciones.**

- **Alegra POS (RD):** crédito por cliente con saldo pendiente. https://www.alegra.com/rdominicana/
- **Treinta (LatAm):** libreta de deudas por cliente y **recordatorio de cobro por WhatsApp en la fecha acordada**. Es la referencia que el dueño de colmado conoce. https://treinta.co
- **Loyverse:** base de clientes y **puntos de fidelidad gratis**, pero **sin crédito/cuentas por cobrar**. https://loyverse.com
- **Kyte:** ventas a crédito solo en el plan PRO (de pago). https://www.kyteapp.com
- **Pidely (RD):** pedidos por WhatsApp más crédito a clientes.
- **Poszy (RD, ya analizado):** saldo, historial, límite, abonos en el turno, facturas vencidas; desde RD$950/mes (`docs/competencia-poszy.md:41,62`).
- **WhatsApp:** enlace **wa.me = gratis** (abre el WhatsApp del dueño con el texto listo; él pulsa Enviar). La **API de WhatsApp Business** cobra por mensaje (≈US$0.013 por mensaje de tipo "utility" en LatAm, según la investigación; ver https://developers.facebook.com/docs/whatsapp/pricing), además de proveedor, plantillas aprobadas y número verificado. **No recomendado** en V1.
- **Lo que espera el dueño:** saber quién debe y cuánto, cobrar sin pelear (un recordatorio amable por WhatsApp) y ver qué compra el cliente. **No encontramos datos** de que pidan puntos o cumpleaños en colmados; los puntos aparecen en apps genéricas (Loyverse), no en las de crédito.

**Lectura:** en RD el diferencial no es "CRM", es **crédito a clientes bien llevado + recordatorio por WhatsApp**. Abastalia ya supera a la mayoría en control (límite con supervisor, antigüedad, abono en el cuadre, e-CF de reemplazo). Le falta el gesto que Treinta hizo famoso: el recordatorio.

## C. Recomendación

### Qué sí (V1, 4 a 6 días)

1. **"Recordar por WhatsApp"** en la cuenta del cliente y en la lista de quién debe: enlace `wa.me/<teléfono>?text=…` con negocio, saldo, factura más vieja y días. Reusar `whatsAppLink` y `NormalizeWhatsApp` (`CatalogPage.tsx:19`; `Sales/Features/Catalog.cs:30`). Sin costo, sin API. Registrar en auditoría la fecha del último recordatorio. **1 a 1.5 días.**
2. **"Enviar estado de cuenta"**: el mismo enlace con el resumen en texto; el PDF sigue como adjunto manual (wa.me no adjunta archivos). **0.5 día.**
3. **Historial de compras del cliente**: filtro `customerId` en `/api/sales` (con índice) y una pestaña "Compras" en la ficha: fecha, total, contado/crédito, productos más comprados. **1.5 a 2 días.**
4. **Filtros de quién debe**: "Más de 30 días", "Pasó su límite", ordenar por deuda o antigüedad, en Administración y en el panel (lista completa, no solo el top 5). **1 a 1.5 días.**
5. **Lenguaje**: cambiar "fiado" por "crédito"/"cuentas por cobrar" en sitio, panel y mensajes. **0.5 día.**

### Qué no (por ahora) y por qué

- **Puntos de fidelidad:** sin evidencia de demanda en colmados; complican el e-CF (descuentos) y el cuadre. Ya decidido V2.
- **Cumpleaños, sexo, estado civil, segmentos de marketing:** descartados (`funcionalidades-v1.md:133`); poco valor y más datos personales que proteger (Ley 172-13).
- **Envío automático por la API de WhatsApp Business:** costo por mensaje (≈US$0.013 en LatAm), proveedor y aprobación de plantillas. Reconsiderar como add-on de pago si el piloto lo pide.
- **Campañas masivas o SMS:** riesgo de spam y costo.

Para medir en el piloto: % de tiendas con crédito activo (`features.onAccount`) y cuántos recordatorios se abren por semana.

### Cómo mostrarlo en el sitio

Una sola tarjeta en Colmado, Carnicería y "En todos los planes", más una línea en la página del panel. Reemplaza "Fiado con límite" (`SitePages.tsx:142`) y "Clientes y fiado" (`:666`).

> **Crédito a clientes**
> Límite por cliente, abonos en la caja y antigüedad de cada deuda. Recuérdele el pago por WhatsApp con un toque y vea lo que compra.

Variantes cortas:
- Hero de Colmado: "Las cuentas por cobrar claras y la caja al día."
- FAQ: "¿Las ventas a crédito salen en el cuadre?" → "Sí. Lo vendido a crédito y lo cobrado salen separados en el cierre de cada turno."
- Panel: "Quién le debe, cuánto y desde cuándo, desde el celular."
- Nota honesta: "El recordatorio abre su WhatsApp con el mensaje listo; usted lo envía. Sin costo por mensaje."

No prometer "CRM", puntos ni envíos automáticos hasta que existan.
