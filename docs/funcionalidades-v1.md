# Funcionalidades: qué copiamos del sistema viejo, qué va en V1 y qué se vende aparte

> 15 de octubre de 2026. Documento maestro que resume los cinco análisis del sistema viejo y la arquitectura nueva.
> Nombre del producto aún en evaluación (Cuadi / Cuadrino); aquí se dice "el producto".
>
> **Principio:** el sistema viejo es la base de ideas, no de código. Nos quedamos con **qué hace** cada función y **cómo resuelve casos específicos** del negocio, y lo construimos mejor. No se copia código, procedimientos ni estructura de tablas.

**Documentos de detalle**
- `legacy/01-ventas-caja-fiscal.md`: venta, cobro, caja, NCF/e-CF, reportes (34 funcionalidades).
- `legacy/02-inventario-compras-otros-modulos.md`: inventario, compras, clientes, préstamos, otros verticales, plataforma (77 funcionalidades).
- `legacy/03-hardware-integraciones.md`: balanza, impresoras, gaveta, códigos de barras, servicios en línea.
- `legacy/04-base-de-datos.md`: esquema, triggers, procedimientos, modelo nuevo, migración.
- `arquitectura-v1.md`: híbrido local-primero (nodo en la tienda + Azure).
- `arquitectura-v1-vsa.md`: Vertical Slice Architecture, outbox, módulos y orden de construcción.

---

## 1. Qué es el sistema viejo

- **SystemPrest.net**: aplicación WinForms en VB.NET (≈280,000 líneas descompiladas), tres capas (Datos / Lógica / Entidades), Dapper, Crystal Reports.
- **Nació como sistema de préstamos** y le fueron agregando punto de venta, restaurante, taller, citas, membresías de gimnasio y contabilidad simple.
- **Usa la base `BdPrestamosSQL`**: 108 tablas, 166 procedimientos (141 en uso), 12 vistas, 8 triggers (solo auditoría).
- **El archivo incluía otra base, `dvsystemdb` ("DVSystem")**: 196 tablas, 806 procedimientos, 106 triggers. Es **otro punto de venta** más completo cuyo código no vino. La usamos como segunda referencia, marcada **(DVS)** en los documentos.

### Lo que no vamos a heredar

| Problema | Ejemplo |
|---|---|
| La venta no es atómica | 8–10 llamadas separadas sin transacción; una venta puede quedar sin su movimiento de inventario. |
| Saldos guardados en vez de calculados | Existencia en dos lugares que no coinciden; saldos de clientes y suplidores mantenidos a mano o por triggers. |
| Cálculos fiscales incorrectos | ITBIS siempre ÷ 1.18 (mal para 16% y exento); ITBIS en 0 si la venta no lleva NCF; 606 vacío, 607 con columnas corridas. |
| Seguridad | 222 sentencias SQL armadas con texto (inyección), contraseñas descifrables, permisos solo en la pantalla, generador de licencias dentro del programa, consola SQL en el menú, PIN de tarjeta bancaria guardado. |
| Pantallas que no hacen nada | Devoluciones no guarda; notas del POS vacía; nota de crédito sin NCF. |
| Hardware frágil | No lee la balanza; el envío directo a la impresora probablemente falla en silencio; texto fijo para 80 mm. |
| e-CF dependiente de internet | Un servicio externo firma; sin internet no hay e-CF ni contingencia. |
| Datos sensibles | Copia del padrón de cédulas, nombre de un cliente real dentro de vistas. |

---

## 2. Alcance por módulo

Leyenda: **V1** = va en la primera versión · **V1-R** = va en V1 pero rediseñada · **NUEVO** = no existe en el viejo, lo creamos · **V2** = después · **Módulo** = se vende aparte · **Descartar** = no se copia.

### Ventas (mostrador)

| Funcionalidad | Estado | Nota |
|---|---|---|
| Búsqueda por código, código de barras o nombre | V1-R | Consultas seguras; buscar también por el código de cada presentación. |
| Etiqueta de balanza (prefijo 2, peso o precio) | V1-R | Corregir el dígito verificador y aceptar etiquetas con precio. |
| Lectura de la balanza en vivo | NUEVO | `IScale` por puerto serie, peso estable. |
| Presentaciones (libra, paquete, caja) con precio y código propios | V1-R | Factor decimal y una sola regla de precio. |
| Cantidad con 3 decimales | V1-R | El viejo trunca a enteros en varios lugares. |
| Niveles de precio por cliente (detalle / mayorista) | V1 | Con permiso para cambiar precio y alerta si queda bajo el costo. |
| Descuento por línea y global, % o monto | V1-R | Tope por rol; PIN de supervisor por encima. |
| Motor de ITBIS (18 / 16 / 0 / exento) | V1-R | Uno solo, en el servidor, siempre calculado. |
| Formas de pago con "tipo base" | V1-R | Sin IDs fijos en el código. |
| Pago mixto y devuelta | V1 | Solo el efectivo da devuelta; la suma debe cuadrar. |
| DOP / USD | NUEVO | Pospuesto en diseño (oculto por ahora). |
| Ventas en espera | V1-R | Varias por caja, guardadas en el nodo local. |
| Anular venta | V1-R | Solo dentro del turno, con PIN y motivo; fuera del turno → nota de crédito. |
| Devolución parcial contra la factura | V1-R | Por línea, regresa al inventario, efectivo o saldo a favor. |
| Notas predefinidas por categoría ("molida", "sin grasa") | V1 | Barata y útil en carnicería. |
| Cotizaciones y pedidos (vendedor → cajero) | V2 / Módulo | Ver "Mayorista". |
| Conduce | V2 / Módulo | Ver "Mayorista". |
| Propina legal | Módulo | Ver "Restaurante". |
| Recargo por pagar con tarjeta | Descartar | Riesgo comercial y legal. |
| Plantillas de factura por cliente | Descartar | Se reemplaza por una plantilla configurable (logo, encabezado, pie, ancho). |

### Caja

| Funcionalidad | Estado | Nota |
|---|---|---|
| Entrar con PIN | V1-R | PIN con bloqueo, hash seguro, validado en el servidor. |
| Abrir turno (caja física + cajero) | V1-R | El viejo ligaba el turno al usuario, no a la caja. |
| Entradas, retiros y gastos menores | V1-R | Motivo de catálogo, PIN en salidas, sin editar (solo reversar). |
| Cuadre ciego con aprobación de supervisor y reporte Z | V1-R | El viejo mostraba el esperado. Se conserva su fórmula del esperado. |
| Conteo por denominaciones | V1 | Denominaciones DOP precargadas. |
| Autorización de supervisor | V1-R | Por acción, queda en la auditoría (quién, qué, cuándo, monto). |

### Fiscal

| Funcionalidad | Estado | Nota |
|---|---|---|
| Rangos e-NCF por tipo | V1-R | Asignación atómica dentro de la venta, sin huecos. |
| e-CF firmado localmente + cola de contingencia 72 h | V1-R / NUEVO | El viejo dependía de internet. `IEcfProvider`. |
| Nota de crédito e-CF 34 | V1-R | |
| Nota de débito e-CF 33 | V2 | |
| Búsqueda de razón social por RNC (padrón DGII) | V1 | Sin inventar datos al crear el cliente. |
| Reportes 606 / 607 | V1 | *planes* (doc. privado) los incluye en todos los planes (corregidos). |
| Impresora fiscal | Descartar | El e-CF la reemplaza. |

### Inventario

| Funcionalidad | Estado | Nota |
|---|---|---|
| Ficha de producto y categorías (con color) | V1 | Sin editar existencia desde la ficha. |
| Kardex (movimientos que no se borran) | NUEVO | En el viejo era código muerto. |
| Costo promedio ponderado | NUEVO | Las compras no actualizaban el costo. |
| Compras, entradas, salidas y ajustes con motivo | V1-R | Tipos cerrados, borrador → aplicado → anulado. |
| Conteo físico | NUEVO | Solo existía en DVSystem. Diseñado ciego. |
| Vender sin existencia (marcado para contar) | V1 | El indicador del viejo funcionaba al revés. |
| Ubicaciones (cuarto frío / mostrador) | V1 en modelo, V2 en pantalla | |
| Transferencia entre almacenes | V1 | |
| Valor de inventario | V1 | Con costo promedio. |
| Etiqueta EAN-13 de pesado | V1-R | La del viejo era Code 128 genérico. |
| Combos / paquetes | V2 | "Combo parrillero"; el viejo también descontaba el combo. |
| Lotes y vencimiento | Módulo | Add-on ya previsto en *planes* (doc. privado). |
| Transferencias entre sucursales | V2 | Plan Multisucursal. |
| Rendimiento de cortes y merma (despiece) | NUEVO, Módulo Carnicería | No existe en el viejo. |

### Compras, suplidores y cuentas por pagar

| Funcionalidad | Estado | Nota |
|---|---|---|
| Suplidores (RNC) | V1 | Ficha propia, no copiada de clientes. |
| Compra a crédito → cuenta por pagar | V1 | Validar en entrevistas. |
| Gasto con NCF sin productos | V1 | Alimenta el 606. |
| Pagos a suplidores (reparte a facturas más viejas) | V1 | Un solo mecanismo de saldo (el viejo tenía dos). |
| Estado de cuenta de suplidor | V1 | |
| Impresión de cheques | Descartar | |
| Gastos fijos / gastos por producto | Descartar | |

### Clientes y fiado

| Funcionalidad | Estado | Nota |
|---|---|---|
| Ficha reducida (nombre, RNC/cédula, teléfono, tipo de comprobante, límite) | V1 | |
| Alta rápida desde el POS | V1 | |
| Fiado: cargos y abonos aplicados a facturas, límite, antigüedad 30/60/90, estado de cuenta | V1-R | Oculto en diseño por ahora. El viejo lo hacía con el motor de préstamos. |
| Recordatorio de cobro por WhatsApp | Módulo / add-on | Junto con el asistente. |
| Puntos de fidelidad | V2 / Módulo | |
| Padrón de cédulas local | Descartar | Riesgo legal. |
| Documentos digitales, cumpleaños, reportes por sexo/estado civil | Descartar | |

### Plataforma

| Funcionalidad | Estado | Nota |
|---|---|---|
| Usuarios, roles y permisos por acción | V1-R | Validados en el servidor. |
| Auditoría que no se puede alterar (hash encadenado) | V1-R | |
| Licencia firmada con gracia de 7–15 días | V1-R | Nunca un generador en el cliente. |
| Respaldo local programado + nube cifrada | V1-R | Sin congelar la pantalla. |
| Exportar a Excel/CSV sin Office | V1-R | |
| Configuración tipada por módulo | V1-R | El viejo tenía ~80 indicadores en una tabla. |
| Registro de errores estructurado | V1-R | |
| Emparejar cajas con certificado | NUEVO | |
| Resumen del día, celular del dueño, asistente | NUEVO | Plan Negocio. |
| Sincronización con la nube (outbox) | NUEVO | El viejo marcaba "sincronizado" a toda la tabla. |
| Consola SQL, configurar base de datos desde el cliente, MEGAsync | Descartar | |

### Reportes V1

Ventas por día, categoría, producto y cajero; utilidad con el **costo histórico** de cada venta (el viejo usaba el costo de hoy e incluía anuladas); resumen de turnos y cuadres; valor de inventario; cuentas por cobrar y por pagar. Reporte ABC: V2. Cómo se generan, sin pagar licencias: sección 3.

---

## 3. Reportes sin costo de licencia

El sistema viejo usa **Crystal Reports**: hay que instalar su motor en cada PC, cada cliente terminó con su copia de reporte y no se puede ver en el celular. Lo reemplazamos con reportes que **son pantallas de la propia aplicación**.

| Necesidad | Solución | Licencia |
|---|---|---|
| Ver el reporte | Página Blazor (HTML/CSS) con filtros arriba, tabla y totales. Es el mismo componente en la caja, en la administración y en el celular del dueño. | Propio |
| Imprimir en hoja carta o guardar PDF | CSS de impresión + `CoreWebView2.PrintToPdfAsync` / diálogo de impresión de WebView2, que ya viene con la app (Blazor Hybrid). Sin librerías extra. | Gratis (incluido en Windows) |
| Exportar a Excel | ClosedXML (o MiniExcel para archivos grandes). | MIT / Apache 2.0 |
| CSV para el contador, 606 / 607 | Generados en C# con el formato de la DGII. | Propio |
| Gráficas (ventas por hora, etc.) | Gráficas en HTML/SVG propias o una librería JS gratuita en el WebView; reglas del lienzo (sin animaciones, legibles). | Propio / MIT |
| Ticket, Z, comprobante de cuadre (80/58 mm) | Plantillas ESC/POS propias (ver sección 6). | Propio |
| PDF generado en el servidor sin pantalla (p. ej. estado de cuenta enviado por WhatsApp) | PDFsharp/MigraDoc (MIT). QuestPDF solo si se acepta su licencia: gratis para empresas con menos de US$1M de ingresos al año, de pago por encima [verificar antes de usar]. | MIT / condicionada |

**Reglas para los reportes**
- Los datos salen de **consultas de lectura** (vistas o consultas EF), nunca de procedimientos almacenados.
- Un reporte = un "slice" de lectura en el módulo Reportes: consulta + página + exportación. Agregar uno nuevo no toca los demás.
- Nada de reportes por cliente: el revendedor cambia logo, colores y encabezado, no la estructura.
- Los totales del reporte deben coincidir con el cuadre y con el celular del dueño, porque salen de los mismos movimientos.

---

## 4. Los dolores que se repiten en estos sistemas (y cómo no cometerlos)

Salen del sistema viejo, de DVSystem y de lo que se sabe de los POS de escritorio locales. El dolor de fondo es uno: **el dueño no puede confiar en los números**.

| # | Dolor | Cómo pasa en el sistema viejo | Cómo lo evitamos |
|---|---|---|---|
| 1 | **"Los números no cuadran"** entre caja, inventario y reportes | La venta se guarda en 8–10 pasos sueltos; saldos guardados a mano; utilidad con el costo de hoy | Una transacción por venta; inventario, caja y saldos calculados de movimientos que no se borran; una sola fuente para todos los reportes |
| 2 | **Una copia del sistema por cliente** | Reportes y procedimientos duplicados por cliente; nombre de un cliente real en las vistas; arreglos "de gimnasio" en el código | Marca blanca solo por configuración (logo, colores, plantilla); nada de código por cliente; lo distinto se vuelve opción o módulo |
| 3 | **Configuración que nadie entiende** | ~80 indicadores en una tabla, algunos funcionan al revés | Configuración por módulo, con nombres claros, valores por defecto sensatos y pocas opciones |
| 4 | **La misma regla escrita en varios lugares** | El ITBIS calculado en 6 sitios con resultados distintos | Cada regla vive en un solo slice del servidor; las pantallas no calculan |
| 5 | **El hardware falla en silencio** | Comandos a la impresora que no llegan; balanza no integrada; cola que reintenta para siempre | Estado visible de impresora y balanza en la barra; errores claros; cola con límite y alerta; probar con el hardware real |
| 6 | **Sin internet, sin factura** | e-CF firmado por un servicio externo | Firma local + contingencia de 72 h; internet no es requisito para vender |
| 7 | **Seguridad como parche** | Permisos solo en la pantalla, SQL armado con texto, claves descifrables, consola SQL | Permisos en el servidor, consultas parametrizadas, hash de contraseñas, auditoría encadenada, sin puertas traseras |
| 8 | **Perder los datos** | Respaldo que congela la pantalla y depende de MEGA | Respaldo programado, verificado y cifrado en la nube, en todos los planes |
| 9 | **Querer servirle a todos** | Préstamos + POS + restaurante + gimnasio + taller en un solo programa; formularios de 11,000–14,000 líneas | Núcleo pequeño para comercio de alimentos; lo demás como módulos separados que se encienden por licencia |
| 10 | **Pantallas a medias en producción** | Devoluciones no guarda; notas vacías; 606 no escribe nada | Pruebas automáticas por slice; nada se publica sin probar el flujo completo |
| 11 | **Instalaciones frágiles** | Motor de Crystal aparte, base de datos abierta en la red, credenciales en cada PC | Instalador con asistente, base de datos solo en la PC principal, actualizaciones automáticas firmadas |
| 12 | **El cajero aprende a la fuerza** | Pantallas densas, sin atajos visibles, modales para todo | Diseño Escuadra: teclas visibles, avisos en línea, escáner nunca se pierde, mismas posiciones siempre |

---

## 5. Módulos que se pueden vender por separado

Es factible conservarlos todos: con Vertical Slices cada módulo es independiente y se enciende por licencia (`IFeatureService`). **No van en V1**: se construyen después, en el orden en que los pidan los clientes.

| Módulo | Qué incluye (del sistema viejo + mejoras) | Para quién | Esfuerzo | Precio propuesto (RD$/mes, ITBIS incl.) |
|---|---|---|---|---|
| **Carnicería** | Rendimiento de cortes, merma, etiquetas de pesado, notas de corte | Carnicerías | Medio | Incluido (1 vertical por plan) |
| **Lotes y vencimiento** | Lote por compra, consumo FEFO, alertas | Deli, colmado, farmacia | Medio | 590 |
| **Mayorista** | Cotizaciones, pedidos vendedor → caja, conduces, niveles de precio extra | Carnicerías que surten a colmados y restaurantes | Medio | 790 |
| **Vendedores y comisiones** | Comisión por producto y vendedor, sobre el neto | Tiendas con vendedores | Bajo | 590 |
| **Fidelidad** | Puntos por compra, canje en caja, reverso al anular | Minimarkets | Bajo | 590 |
| **Restaurante** | Mesas, comandas a cocina por estación, dividir cuenta, propina legal | Cafeterías, comedores, restaurantes | Alto | 1,490 (o plan vertical propio) |
| **Servicios / taller** | Órdenes de servicio, estado, etiqueta | Talleres pequeños | Medio | 790 |
| **Citas y membresías** | Citas, planes con vencimiento, asistencia | Gimnasios, barberías | Medio | 990 |
| **Préstamos / financiamiento** | Solicitudes, amortización, mora, cobradores, rutas | Financieras pequeñas | **Muy alto** | **Producto aparte**, no add-on (≥ 2,990) |
| ~~Contabilidad~~ | — | — | — | Descartar; exportar 606/607 y datos para el contador |

**Advertencias**
- **Préstamos** es otro negocio: la ley de protección de datos (Ley 172-13) y el manejo de información de crédito exigen otro nivel de cuidado (el viejo guardaba PIN de tarjetas y una copia del padrón). Recomiendo tratarlo como producto separado, si alguna vez se hace.
- **Restaurante y gimnasios** cambian el cliente objetivo; cada uno pide su propio diseño de pantallas.
- Los precios son una propuesta para validar con la tabla de *planes* (doc. privado) (add-ons actuales: caja adicional RD$690, sucursal RD$3,490, celular RD$990, IA RD$990).

---

## 6. Hardware: lo que hay y lo que haremos

| Dispositivo | Sistema viejo | V1 |
|---|---|---|
| Balanza | No la lee; solo etiquetas (con error de dígito verificador) o peso tecleado | `IScale` por puerto serie, un driver por marca (CAS / Toledo a confirmar), peso estable; lector correcto de etiquetas de peso y de precio (prefijos 20–29) |
| Impresora de tickets | Dibujo con el driver de Windows, ancho fijo 80 mm; Crystal Reports para facturas | Generador ESC/POS propio, modo RAW o TCP 9100, imagen como respaldo para clones baratos, plantillas configurables, 58 y 80 mm |
| Corte y gaveta | `GS V 0` y `ESC p 0 25 250`, probablemente fallando en silencio | Los mismos comandos bien enviados; gaveta manual con PIN y registro |
| Cola de impresión | Reintenta cada 5 s sin límite, no detecta impresora apagada | Cola en base de datos con reintentos, límite y alertas visibles |
| Códigos de barras | Code 128 con ZXing | EAN-13 de pesado; lector en modo teclado o serie |
| e-CF | Servicio externo firma; sin contingencia | Firma local + cola 72 h |
| Respaldo | Al iniciar sesión, congela la pantalla; MEGAsync | Programado, verificado, cifrado a Azure |

Hay **12 preguntas** que solo se responden con una instalación real y su hardware (sección 9 de `03-hardware-integraciones.md`).

---

## 7. Arquitectura V1 en una página

- **Despliegue:** híbrido local-primero. Una PC principal con la API (servicio de Windows), SQL Server Express 2025 y el certificado .p12; las cajas se conectan por la red local. Azure recibe los eventos (outbox), da el celular del dueño, los respaldos, el envío de e-CF y la IA.
- **Código:** monolito modular con **Vertical Slices**, .NET 10. Módulos: Ventas, Caja, Inventario, Compras, Clientes/Fiado, Fiscal, Hardware, Identidad, Configuración/Licencia, Sync y Reportes. Cada uno con su esquema; sin llaves foráneas entre módulos.
- **Reglas:** cero procedimientos almacenados y cero triggers (una prueba de arquitectura lo hace cumplir); toda la lógica en C#; EF Core; inventario y caja como movimientos.
- **Sin MediatR** (licencia comercial desde 2025): interfaces propias de comandos y consultas con decoradores (registro → validación → autorización → transacción).
- **Una sola transacción por venta:** venta + movimiento de inventario + movimiento de caja + e-NCF firmado + fila de outbox.

---

## 8. Decisiones que le tocan al dueño del producto

1. **Alcance V1:** aprobar las tablas de la sección 2 (o mover filas).
2. **Módulos vendibles:** cuáles y en qué orden (sección 5).
3. **Certificado que falla:** ¿se sigue vendiendo con comprobante "pendiente de firma" o se detiene? Confirmar con el proveedor e-CF y la DGII.
4. **Sin la PC principal:** ¿la caja sigue vendiendo con ticket provisional? (diseñado así en el lienzo).
5. **DVSystem:** ¿de dónde salió y tenemos acceso a esa aplicación? Tiene conteo físico, lotes y fidelidad mejor resueltos.
6. **Nombre:** Cuadi (recomendado) vs. Cuadrino.
7. **Proveedor e-CF del sistema viejo:** es un intermediario externo (no es del autor), configurado por instalación (`HostUrlWebpos` + `companyLicCod`). La URL viene vacía en el material recibido, así que no se sabe cuál es. Averiguarlo en una tienda que lo use: si está autorizado por la DGII y acepta XML firmado por nosotros, podría ser una implementación más de `IEcfProvider` (útil para migrar clientes sin recertificar); si firma en su nube, no sirve para el modo sin internet.

---

## 9. Próximos pasos

1. **Aprobar el alcance V1** y la lista de módulos vendibles (una reunión con este documento).
2. **Proveedor e-CF (bloqueante):** confirmar por escrito con ECF SSD que acepta XML **firmado por nosotros**, el precio de integrador y la contingencia. Sin eso no hay firma local ni modo offline real. Tener un segundo candidato (Grodtech, GAE).
3. **Visita a una tienda con el sistema viejo instalado:** probar las 12 preguntas de hardware, identificar las marcas y modelos de balanza e impresora que usan, y tomar fotos de etiquetas de pesado reales.
4. **Una copia real de una base de datos** (anonimizada) para probar la migración; los scripts solo traen el esquema.
5. **Entrevistas a 5–10 carnicerías y colmados** con las preguntas abiertas de los documentos (costo, crédito de suplidores, niveles de precio, fiado, cortes).
6. **Sistema de diseño Escuadra** (`sistema-de-diseno.md`): componentes propios en lugar de MudBlazor/Fluent UI, tokens con marca blanca validada, galería y pruebas visuales. Su fase 0 (tokens, marco, botones, campo de escaneo, avisos) se construye **junto al esqueleto, antes del primer slice**.
7. **Actualizar el diseño** en el lienzo con lo que salió del análisis: devolución parcial, presentaciones, niveles de precio, notas por categoría, ventas en espera, conteo físico ciego. Revisarlo con los agentes de UX.
8. **Preparar el repositorio** (cuando se apruebe construir): git con `legacy/` **excluido** (contiene contraseñas y datos del sistema viejo), CI, ADRs con las decisiones de este documento, y el esqueleto según el orden de `arquitectura-v1-vsa.md` (esqueleto → emparejar y PIN → productos → abrir turno → venta → cuadre → anulación → compras → e-CF y nube).
9. **Legal:** búsqueda ONAPI del nombre; revisión de privacidad para el celular del dueño y la IA.

**Seguridad del material recibido:** la carpeta `legacy/` contiene `clave.txt` y archivos de configuración con credenciales del sistema viejo. No se copiaron a ningún documento. No la suba a ningún repositorio ni la comparta; si esas credenciales siguen en uso en alguna tienda, conviene cambiarlas.
