# Competencia: Poszy frente a Toki

> Ticket TK-188 (#157). 15 de octubre de 2026. Solo investigación: no cambia código.
> Fuentes: el sitio público de Poszy (cada afirmación lleva su URL, leída el 15/10/2026) y los documentos de este repositorio (`docs/funcionalidades-v1.md`, `backlog/BACKLOG.md`, `docs/ecf-propio.md`, ADRs).
> No se creó cuenta, no se llenaron formularios y no se descargó la app. Lo que Poszy **no dice** en su sitio se marca como "no se menciona"; eso no prueba que no lo tenga.

## 1. Quién es Poszy

| | |
|---|---|
| Empresa | Poszy EIRL, empresa individual de responsabilidad limitada dominicana ([términos](https://poszy.com/terms)). |
| Producto | Punto de venta y facturación electrónica **en la nube**, que se usa desde la computadora (web) o el celular. Eslogan: "El punto de venta que impulsa tu negocio" ([inicio](https://poszy.com/)). |
| País | Solo República Dominicana ([inicio](https://poszy.com/)). |
| Segmentos | Colmados y bodegas, supermercados, farmacias, boutiques, librerías, panaderías, cafeterías y restaurantes, licorerías y colmadones ([inicio](https://poszy.com/)), cada uno con su página ([colmados](https://poszy.com/pos-para-colmados), [supermercados](https://poszy.com/punto-de-venta-republica-dominicana), [farmacias](https://poszy.com/pos-para-farmacias), [tiendas](https://poszy.com/pos-para-tiendas), [panaderías](https://poszy.com/pos-para-panaderias), [restaurantes](https://poszy.com/pos-para-restaurantes), [licorerías](https://poszy.com/pos-para-licoreras)). Hay también páginas por ciudad (Navarrete), pensadas para SEO ([colmados](https://poszy.com/pos-para-colmados)). |
| Posicionamiento | Horizontal: un solo producto para todo el comercio pequeño, barato y sin instalación. El mensaje fuerte es el **e-CF** y la urgencia del calendario de la DGII ([blog](https://poszy.com/blog)). |
| Madurez | Parece reciente: los términos ofrecen un mes gratis a "los primeros 100 negocios" ([términos](https://poszy.com/terms)), el blog empieza en julio de 2026 ([blog](https://poszy.com/blog)), no hay testimonios ([inicio](https://poszy.com/)) y no encontramos la app en Google Play ni redes sociales indexadas por buscadores (búsqueda propia, 15/10/2026). La app Android se descarga desde su propio sitio ([descargar](https://poszy.com/descargar)). |
| Contacto y venta | Todo pasa por WhatsApp: demo, ventas, soporte, planes a medida y afiliados ([inicio](https://poszy.com/), [afiliados](https://poszy.com/afiliados)). |

## 2. Comparación por funcionalidad

Leyenda de Toki: ✅ hecho · 🔨 en curso · ⬜ planeado · — no previsto. "Toki hoy" es lo que está en `backlog/BACKLOG.md`; el e-CF real con la DGII está en construcción (M13) y hoy es simulado.

| Área | Poszy (según su sitio) | Toki hoy | Toki planeado |
|---|---|---|---|
| **Despliegue** | Nube; web y app Android, también en terminales Elo ([inicio](https://poszy.com/), [descargar](https://poszy.com/descargar)) | ✅ Local-primero: PC principal con la API + cajas Windows; nube para panel y respaldo | Modalidad "en línea" sin PC principal (fase 2) |
| **Sin internet** | No se menciona; el e-CF tiene un estado "No procesado (sin conexión)" ([planes](https://poszy.com/#pricing)). Su propio blog dice que el sistema debe funcionar "aunque la conexión fluctúe" ([blog](https://poszy.com/blog/como-elegir-sistema-pos-en-republica-dominicana)) | ✅ La tienda vende, cobra y cuadra sin internet; e-CF en cola de 72 h | ✅ |
| **Venta en mostrador** | Búsqueda por nombre o código, lector de barras, efectivo/tarjeta/transferencia, pago mixto ([colmados](https://poszy.com/pos-para-colmados), [supermercados](https://poszy.com/punto-de-venta-republica-dominicana)) | ✅ Escáner, mosaicos, teclas F, pago mixto, devuelta en pesos enteros, ventas en espera | ✅ |
| **Venta por peso / balanza** | No se menciona en ninguna página de segmento ([colmados](https://poszy.com/pos-para-colmados), [panaderías](https://poszy.com/pos-para-panaderias), [supermercados](https://poszy.com/punto-de-venta-republica-dominicana)) | ✅ Productos por libra con 3 decimales, etiquetas de balanza EAN-13 (prefijos 20–29, peso o precio); 🔨 lectura en vivo (TK-117) | Módulo Carnicería: rendimiento de cortes y merma |
| **Presentaciones / variantes** | Variantes con precio, código y existencia propios (talla, color, capacidad) ([tiendas](https://poszy.com/pos-para-tiendas), [licorerías](https://poszy.com/pos-para-licoreras)) | ✅ Presentaciones (libra, paquete, caja) con factor, precio y código | — (talla/color no aplica a alimentos) |
| **Notas por línea** | No se menciona | ✅ "Molida", "sin grasa" por categoría | ✅ |
| **Niveles de precio (mayorista)** | No se menciona | ✅ Detalle y mayorista por cliente | ✅ |
| **Descuentos y aprobaciones** | Permisos por rol ([inicio](https://poszy.com/)) | ✅ Tope por rol y autorización de supervisor de un solo uso por acción, auditada | ✅ |
| **Anular / devolver** | Anulaciones, reembolsos y notas de crédito ([supermercados](https://poszy.com/punto-de-venta-republica-dominicana)) | ✅ Anular en el turno con supervisor; devolución parcial con nota de crédito e-CF 34 | ✅ |
| **Caja y turnos** | Turno por cajero; cierre con ventas por medio, cobros de fiado, salidas y diferencia esperado/contado; cierre en PDF de una página ([colmados](https://poszy.com/pos-para-colmados), [supermercados](https://poszy.com/punto-de-venta-republica-dominicana)) | ✅ Turno por caja física; **cuadre ciego** por denominaciones, aprobación de supervisor, reporte Z | PDF del cierre (TK-200) |
| **e-CF** | Se emite al cobrar; Poszy "verifica y firma"; envío por Web Service; aprobado/rechazado/pendiente/no procesado; aviso de vencimiento del certificado y de los números; 10 años de archivo ([inicio](https://poszy.com/), [planes](https://poszy.com/#pricing)). No describe contingencia | 🔨 Camino propio con la DGII: firma en la PC de la tienda (✅ firmador y XML validado contra XSD), RFCE, cola, rechazos, anulación (M13); ✅ alertas de rango al 80 % y por vencimiento | ⬜ Aviso de vencimiento del certificado (TK-176), contingencia serie B (TK-157), receptor en la nube (TK-152) |
| **Cupo de e-CF** | 1,500 / 5,000 / 15,000 al mes según plan; excedente RD$1.20 c/u ([planes](https://poszy.com/#pricing)) | — | Uso justo; pasarse nunca detiene la facturación |
| **Reportes fiscales** | 606, 607 y 608 en todos los planes ([planes](https://poszy.com/#pricing)) | ✅ 606 y 607 (CSV/Excel) | ⬜ 608 (TK-192), TXT oficial |
| **Inventario** | Entradas, salidas y ajustes en todos los planes; almacenes, traslados, conteos y reabastecimiento solo en Profesional y Empresarial ([planes](https://poszy.com/#pricing)); kardex con saldo antes y después ([licorerías](https://poszy.com/pos-para-licoreras)) | ✅ Kardex que no se borra, costo promedio ponderado, ajustes con motivo, conteo físico **ciego**, almacenes (tienda, cuarto frío) y transferencias; 🔨 pantallas de administración (TK-184) | Lotes y vencimiento (módulo) |
| **Mínimos y reposición** | Mínimo por producto con alerta, sugerencia de cuánto pedir hasta un máximo, con el último suplidor ([colmados](https://poszy.com/pos-para-colmados)) | 🔨 Existencias bajo el mínimo (con un bug abierto, TK-178) | ⬜ Pedido sugerido (TK-191) |
| **Compras y suplidores** | Compras y proveedores en todos los planes; cuentas por pagar desde Profesional ([planes](https://poszy.com/#pricing)) | ✅ Compras borrador → aplicada → anulada, a crédito, pagos a suplidores, gastos con NCF | ✅ |
| **Fiado** | Saldo, historial, límite opcional (límite 0 bloquea), abonos en el turno, facturas vencidas ([colmados](https://poszy.com/pos-para-colmados), [licorerías](https://poszy.com/pos-para-licoreras)) | ✅ Cargos, abonos a las facturas más viejas, límite con supervisor para excederlo, antigüedad 30/60/90, estado de cuenta; 🔨 abono desde la caja (TK-121) | Recordatorio de cobro por WhatsApp (add-on) |
| **Cotizaciones** | En todos los planes ([planes](https://poszy.com/#pricing)), "cotizar ahora, cobrar después" ([tiendas](https://poszy.com/pos-para-tiendas)) | — | Módulo Mayorista (V2); TK-194 |
| **Encargos / agenda** | Agenda de encargos con fecha, hora y recordatorio ([panaderías](https://poszy.com/pos-para-panaderias)) | — | ⬜ TK-193 |
| **Catálogo / menú digital** | Vitrina con enlace fijo y QR; botón de WhatsApp con el producto; precios e imágenes del inventario; sin carrito ni pago ([blog](https://poszy.com/blog/menu-digital-catalogo-digital-qr-negocio-republica-dominicana)) | — | ⬜ TK-195 |
| **Delivery** | No se menciona; la entrega se coordina por WhatsApp fuera de Poszy ([blog](https://poszy.com/blog/menu-digital-catalogo-digital-qr-negocio-republica-dominicana)) | — | — |
| **Fidelidad / puntos** | No se menciona | — | Módulo Fidelidad (V2) |
| **Multisucursal** | Hasta 3 o 5 sucursales y 3 u 8 cajas según plan, inventario por sucursal ([planes](https://poszy.com/#pricing), [supermercados](https://poszy.com/punto-de-venta-republica-dominicana)) | Varias cajas en una tienda ✅; una sucursal por nodo | Plan Multisucursal (fase 2, en línea) |
| **Celular del dueño** | La misma app web/Android | ✅ Panel web del dueño (resumen, ventas, cuadres, por cobrar) y asistente IA; 🟡 falta publicarlo (TK-137, TK-139) | ✅ |
| **Asistente IA** | No se menciona ([privacidad](https://poszy.com/privacy) no menciona IA) | ✅ Preguntas sobre ventas, cuadres y por cobrar (falta probar con el modelo real) | Factura del suplidor por foto → compra |
| **Usuarios y auditoría** | Permisos por rol, actividad por empleado ([inicio](https://poszy.com/)); costos y utilidades solo con permiso ([licorerías](https://poszy.com/pos-para-licoreras)) | ✅ Roles editables con permisos por acción validados en el servidor; **auditoría encadenada por hash** verificada cada noche | ⬜ Revisar visibilidad del costo (TK-201) |
| **Hardware** | Lector de barras "como un teclado"; impresión nativa en Android y Elo ([colmados](https://poszy.com/pos-para-colmados), [inicio](https://poszy.com/)). No nombra impresoras, gaveta ni balanza | ✅ Lector; 🔨 impresora ESC/POS 58/80 mm y gaveta (TK-118), balanza en vivo (TK-117) | Kit Carnicería / Colmado |
| **Otros verticales** | Seriales e IMEI, propina legal 10 %, nómina, despacho con PIN ([tiendas](https://poszy.com/pos-para-tiendas), [restaurantes](https://poszy.com/pos-para-restaurantes), [planes](https://poszy.com/#pricing)). Sin comandas a cocina ni recetas ([restaurantes](https://poszy.com/pos-para-restaurantes), [panaderías](https://poszy.com/pos-para-panaderias)) | — | Módulos vendibles (restaurante, mayorista…) después de V1 |
| **Respaldo y datos** | Exportación CSV/JSON al cancelar; datos borrados 30 días después; sin SLA ("tal cual"); responsabilidad máxima: lo pagado en 12 meses, RD$0 en prueba ([términos](https://poszy.com/terms)). Datos fuera de RD, sin nombrar proveedor ([privacidad](https://poszy.com/privacy)) | ✅ Respaldo cifrado nocturno en la tienda + copia opcional; exportar a CSV/Excel siempre | Regla pública: nunca se retiene la venta, el e-CF ni los datos |
| **Prueba y alta** | Cuenta gratis sin tarjeta, sin instalación, sin permanencia ([planes](https://poszy.com/#pricing)); 30 días con código de afiliado ([afiliados](https://poszy.com/afiliados)) | Licencia en estado Prueba (2 cajas) ✅; instalación con script en la PC principal | ⬜ TK-196 |
| **Soporte** | WhatsApp (Básico), + correo (Profesional), + teléfono y reunión mensual (Empresarial) ([planes](https://poszy.com/#pricing)) | — | WhatsApp vía revendedor; restauración remota de la PC |
| **Canal** | Afiliados con comisión recurrente de 5 % a 15 % según clientes activos ([afiliados](https://poszy.com/afiliados)) | — | Revendedores; ⬜ TK-199 |

## 3. Precios (alto nivel)

| | Poszy | Toki |
|---|---|---|
| Planes | Básico RD$950, Profesional RD$1,850, Empresarial RD$3,000 al mes; 5 % menos al año ([planes](https://poszy.com/#pricing)) | Tres planes por tamaño (cajas y sucursales), propuesta en revisión |
| Qué limita el plan | Usuarios (3/5/12), sucursales, cajas, almacenes y e-CF al mes; módulos (traslados, conteos, cuentas por pagar, reportes avanzados) solo en los planes altos ([planes](https://poszy.com/#pricing)) | Cajas, sucursales y el celular del dueño; lo básico (balanza, e-CF, inventario completo, cuadre ciego, fiado, 606/607, respaldo) en todos |
| e-CF | Cupo por plan y RD$1.20 por excedente ([planes](https://poszy.com/#pricing)) | Uso justo, sin cobro por documento publicado |
| Instalación | Sin costo ([planes](https://poszy.com/#pricing)) | Cobro único de instalación y alta e-CF; kit de equipos cotizado |
| Al pasarse del cupo | No se pueden crear más elementos hasta subir de plan ([planes](https://poszy.com/#pricing)) | Nunca se detiene la venta ni el e-CF |

**Lectura:** Poszy pone el piso del mercado en precio. El plan de entrada de Toki, tal como está propuesto, cuesta varias veces el Básico de Poszy. Toki se justifica en una tienda con balanza, varias cajas y mala conexión (carnicería), pero en el colmado de una caja el dueño va a comparar contra RD$950 sin instalación. Ver TK-203.

## 4. Dónde Toki es más fuerte

1. **Sigue vendiendo sin internet.** La tienda entera (varias cajas, inventario y caja compartidos) funciona contra la PC principal, y el e-CF se firma en la tienda con cola de 72 h. Poszy no menciona modo sin conexión ni contingencia ([planes](https://poszy.com/#pricing)), aunque su propio blog lo pone como requisito ([blog](https://poszy.com/blog/como-elegir-sistema-pos-en-republica-dominicana)).
2. **Hecho para carnicería.** Venta por peso con 3 decimales, etiquetas de balanza EAN-13 con peso o precio, presentaciones por libra/paquete/caja, notas de corte, cuarto frío como almacén, precio mayorista para quien surte colmados y restaurantes. Poszy no menciona peso ni balanza en ninguna página.
3. **Control del efectivo.** Cuadre ciego por denominaciones con aprobación de supervisor y reporte Z; autorizaciones de supervisor de un solo uso, ligadas a la acción y al equipo; auditoría encadenada por hash que se verifica sola. Poszy muestra la diferencia esperado/contado, pero no dice que el conteo sea ciego ([colmados](https://poszy.com/pos-para-colmados)).
4. **Números en los que se puede confiar.** Una transacción por venta, kardex que no se borra, costo promedio ponderado, utilidad con el costo histórico y conteo físico ciego. Además, todo el inventario (almacenes, transferencias y conteos) va en todos los planes. En Poszy, almacenes, traslados y conteos son de los planes altos ([planes](https://poszy.com/#pricing)).
5. **El e-CF y los datos no son rehenes.** No hay cupo de documentos que frene la facturación, la tienda tiene su respaldo cifrado y la PC principal habla directo con la DGII (sin intermediario que pueda caerse o subir precios). El dueño también tiene un asistente IA para preguntar por su negocio, algo que Poszy no ofrece.

## 5. Dónde Poszy va delante

1. **Cero fricción para empezar.** Crear cuenta gratis, sin tarjeta, sin instalar nada, desde el celular ([planes](https://poszy.com/#pricing)). Toki pide una PC principal y un script de instalación.
2. **Precio de entrada y página de precios pública** con límites claros ([planes](https://poszy.com/#pricing)).
3. **Reposición:** mínimos, máximos y pedido sugerido con el último suplidor ([colmados](https://poszy.com/pos-para-colmados)).
4. **Funciones de venta fuera del mostrador:** catálogo/menú con QR y WhatsApp, cotizaciones y encargos con recordatorio ([blog](https://poszy.com/blog/menu-digital-catalogo-digital-qr-negocio-republica-dominicana), [tiendas](https://poszy.com/pos-para-tiendas), [panaderías](https://poszy.com/pos-para-panaderias)).
5. **608 incluido** junto al 606 y 607 ([planes](https://poszy.com/#pricing)).
6. **Corre en Android** (teléfono, tablet, terminal Elo) ([descargar](https://poszy.com/descargar), [inicio](https://poszy.com/)).
7. **Marketing y canal:** una página por segmento y por ciudad, un blog que explota la urgencia del e-CF ([blog](https://poszy.com/blog)) y un programa de afiliados con comisión recurrente ([afiliados](https://poszy.com/afiliados)).

## 6. Lecciones de UX y marketing

- **Una página por segmento con el dolor en el título.** Poszy escribe "El fiao deja de vivir en una libreta" y "Cuadrar la caja sin discusión" ([colmados](https://poszy.com/pos-para-colmados)). Toki debería tener "Carnicería", "Colmado" y "Deli" con el dolor de cada una: la balanza, el cuadre y el internet que se va.
- **La urgencia fiscal vende.** El blog de Poszy de octubre habla del calendario obligatorio y de las sanciones ([blog](https://poszy.com/blog)). El plazo de los pequeños contribuyentes (15/11/2026, `docs/ecf-propio.md`) es la ventana de compra de este trimestre.
- **Atacar el punto débil del otro sin nombrarlo:** "Si se va el internet, sigues vendiendo y facturando". Poszy no puede decirlo hoy.
- **Precios visibles y límites en lenguaje simple.** Poszy lo hace bien; Toki debe publicar los suyos con la regla de "nunca se detiene la venta".
- **WhatsApp como canal único** (demo, ventas, soporte) es lo que espera el dueño dominicano; Toki ya lo prevé vía revendedores.
- **Prueba social:** Poszy no tiene testimonios. Toki puede ganar ese espacio con el piloto (fotos de la tienda, el dueño hablando, cifras de cuadre).
- **Responder al buscador:** Poszy tiene una FAQ "¿Poszy es lo mismo que pooszy?" para captar errores de escritura ([colmados](https://poszy.com/pos-para-colmados)). Toki debe reservar sus variantes de nombre.

## 7. Brechas priorizadas (impacto × esfuerzo)

Impacto en ventas o retención para carnicerías y colmados; esfuerzo S (≤ 2 días), M (≤ 1 semana), L (más).

| Prioridad | Ticket | Brecha | Impacto | Esfuerzo |
|---|---|---|---|---|
| 1 | TK-197 | Sitio con precios públicos y páginas por segmento | Alto | S–M |
| 2 | TK-198 | Contenido e-CF: "¿te toca?" y plazo del 15/11 | Alto | S |
| 3 | TK-203 | Revisar el plan de entrada frente a Poszy | Alto | S |
| 4 | TK-192 | Reporte 608 | Medio | S |
| 5 | TK-200 | Cierre de turno y estado de cuenta en PDF | Medio | S |
| 6 | TK-201 | Costo y utilidad visibles solo con permiso | Medio | S |
| 7 | TK-191 | Mínimos, máximos y pedido sugerido | Alto | M |
| 8 | TK-196 | Prueba gratis autoservicio | Alto | M |
| 9 | TK-193 | Encargos con fecha de entrega, abono y recordatorio | Alto (carnicería) | M |
| 10 | TK-199 | Programa de referidos para revendedores y contadores | Medio | S (definición) |
| 11 | TK-194 | Cotizaciones que se convierten en venta | Medio | M |
| 12 | TK-195 | Catálogo con QR y pedido por WhatsApp | Medio | M |
| 13 | TK-202 | Investigación: caja o toma de pedidos en tablet Android | Medio | M (spike) |

**No copiar:** seriales/IMEI, nómina, menú de restaurante, variantes de talla y color. Son de otros segmentos y van contra el principio de "núcleo pequeño para alimentos" (`docs/funcionalidades-v1.md` §4, dolor 9).

## 8. Tickets propuestos

### TK-191 · Mínimos, máximos y pedido sugerido (M)
- **Alcance:** mínimo y máximo por producto y almacén; lista "Para reponer" en administración y en el panel; cantidad sugerida = máximo − existencia, en la presentación de compra, con el último suplidor y su último costo; botón "Crear compra en borrador" por suplidor. Depende de TK-178.
- **Aceptación:** un producto bajo el mínimo aparece en la lista con la cantidad sugerida y el último suplidor; crear la compra deja un borrador por suplidor con esas líneas; los productos por peso sugieren en libras; prueba de integración del cálculo.

### TK-192 · Reporte 608 de comprobantes anulados (S)
- **Alcance:** el 608 con el formato de la DGII (e-NCF anulados por ANECF y anulaciones dentro del turno que consumieron secuencia), en CSV/Excel y TXT oficial, junto al 606 y 607.
- **Aceptación:** un mes con anulaciones produce un 608 que coincide con los e-NCF anulados; prueba e2e que cuadra 607 + 608 con las secuencias usadas.

### TK-193 · Encargos (M)
- **Alcance:** encargo con cliente, productos (peso estimado), fecha y hora de entrega, abono opcional (entra a la caja como anticipo), estado (pendiente, listo, entregado, cancelado); al entregar se convierte en venta con el peso real y descuenta el abono; lista del día en la caja; recordatorio el día antes (en la caja y, con el add-on, por WhatsApp).
- **Aceptación:** crear, cobrar abono, convertir en venta con otro peso y cuadrar el Z con el anticipo; cancelar devuelve el abono con supervisor; auditoría en cada paso.

### TK-194 · Cotizaciones (M)
- **Alcance:** cotización con cliente, líneas, precios del nivel del cliente y vigencia; imprimir o PDF; convertir en venta (re-precia si venció). Primer paso del módulo Mayorista.
- **Aceptación:** la cotización no mueve inventario ni caja; al convertirla la venta sale con las mismas líneas; vencida pide confirmar los precios nuevos.

### TK-195 · Catálogo con QR y pedido por WhatsApp (M)
- **Alcance:** página pública por tienda en la nube (desde la réplica de productos y precios), enlace fijo y QR para imprimir, botón de WhatsApp con el producto y la cantidad; el dueño elige qué productos mostrar. Sin carrito ni pago. Necesita un evento de catálogo hacia la nube.
- **Aceptación:** un cambio de precio en la tienda se ve en el catálogo en menos de 5 minutos con internet; el QR no cambia al editar; no se muestran costos ni existencias.

### TK-196 · Prueba gratis autoservicio (M)
- **Alcance:** descargar el instalador desde la web; asistente de un solo equipo (la PC es principal y caja) sin pasos técnicos; datos de ejemplo opcionales (carnicería o colmado) que se borran con un clic; licencia de prueba de 30 días; registro en el panel en el mismo flujo.
- **Aceptación:** una persona sin ayuda pasa de la descarga a la primera venta en menos de 15 minutos en una PC Windows limpia (con TK-160); borrar los datos de ejemplo deja la base sin ventas ni movimientos.

### TK-197 · Sitio: precios públicos y páginas por segmento (S–M)
- **Alcance:** página de precios con límites en lenguaje simple y las reglas de "nunca se detiene la venta"; páginas Carnicería, Colmado y Deli con su dolor principal; FAQ (sin internet, balanza, e-CF, cambiar de sistema); WhatsApp como llamada a la acción.
- **Aceptación:** las cuatro páginas publicadas, con los precios aprobados (TK-203), rápidas en el celular y con metadatos para buscadores.

### TK-198 · Contenido e-CF y plazo del 15 de noviembre (S)
- **Alcance:** guía corta "¿Ya te toca emitir e-CF?", qué pasa si no se cumple y cómo Toki factura sin internet; basada en `docs/ecf-propio.md` y fuentes de la DGII.
- **Aceptación:** artículo publicado y revisado por un contador, con enlaces a la DGII y sin afirmaciones legales sin fuente.

### TK-199 · Programa de referidos (S, definición)
- **Alcance:** definir el incentivo para revendedores, contadores y clientes que refieren (comisión recurrente o meses gratis), el código de referido en el alta y el reporte de pagos. La decisión es del dueño.
- **Aceptación:** documento con reglas y montos aprobado; el alta de la nube guarda el código de referido.

### TK-200 · Cierre de turno y estado de cuenta en PDF (S)
- **Alcance:** PDF de una página del Z (y del estado de cuenta de fiado) desde la administración y el panel, para mandarlo por WhatsApp o guardarlo.
- **Aceptación:** el PDF muestra lo mismo que el Z impreso (medios, cobros de fiado, salidas, diferencia, supervisor) y el estado de cuenta cuadra con el saldo.

### TK-201 · Costo y utilidad solo con permiso (S)
- **Alcance:** revisar que la caja, la administración, el panel y las exportaciones no muestren costo, utilidad ni valor de inventario a quien no tenga un permiso `inventory.costs.view` (o similar).
- **Aceptación:** un cajero no ve el costo en ninguna pantalla ni respuesta de la API (prueba de integración por endpoint); el dueño sí.

### TK-202 · Investigación: caja o toma de pedidos en tablet Android (M, spike)
- **Alcance:** evaluar una pantalla ligera en tablet o celular Android conectada a la PC principal por la red local (vendedor de piso, toma de pedidos que se cobran en la caja, consulta de precio). Entregable: documento con opciones (web en el navegador del equipo, MAUI), seguridad del emparejamiento e impacto en el diseño.
- **Aceptación:** ADR propuesta con recomendación y estimado; sin código de producción.

### TK-203 · Revisar el plan de entrada frente a Poszy (S)
- **Alcance:** con este análisis, decidir si hace falta un plan para colmado de una caja (precio, límites, sin PC principal o con instalación simplificada) y cómo comunicar el valor del modo sin internet frente a un competidor más barato. Decisión del dueño; se actualiza el documento interno de planes.
- **Aceptación:** decisión escrita y precios aprobados para TK-197.

## 9. Fuentes (Poszy)

- https://poszy.com/ (inicio, funciones, facturación electrónica, planes)
- https://poszy.com/#pricing
- https://poszy.com/pos-para-colmados
- https://poszy.com/punto-de-venta-republica-dominicana
- https://poszy.com/pos-para-farmacias
- https://poszy.com/pos-para-tiendas
- https://poszy.com/pos-para-panaderias
- https://poszy.com/pos-para-restaurantes
- https://poszy.com/pos-para-licoreras
- https://poszy.com/descargar
- https://poszy.com/afiliados
- https://poszy.com/terms
- https://poszy.com/privacy
- https://poszy.com/blog
- https://poszy.com/blog/como-elegir-sistema-pos-en-republica-dominicana
- https://poszy.com/blog/menu-digital-catalogo-digital-qr-negocio-republica-dominicana
