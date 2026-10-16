# Dolores de los dueños y ángulos para anuncios

> Ticket TK-272 (#242). 16 de octubre de 2026. Solo preparación: no se lanzó ninguna campaña, no se creó cuenta de anuncios y no se gastó dinero.
> Fuentes: prensa dominicana, DGII y sitios de proveedores (cada dato lleva su enlace y fecha, leídos el 16/10/2026), y lo que ya existe en este repositorio: `docs/competencia-poszy.md`, `docs/competencia-glecf.md`, `docs/funcionalidades-v1.md` §4, `docs/metricas-producto.md` y las páginas de `src/Cloud/panel/src/pages/site/`.
> Para no repetir: las lecciones de marketing de Poszy (página por segmento, urgencia del e-CF, WhatsApp, prueba social) ya están en `docs/competencia-poszy.md` §6. Aquí se usan, no se vuelven a explicar.

## 0. Lo que hay que saber antes de leer

- **Hay poca voz directa del dueño en internet.** El dueño de colmado o carnicería dominicano casi no escribe reseñas de software ni hilos en Reddit o Facebook (las búsquedas no dieron nada útil). La única voz directa encontrada es el **foro de ayuda de la DGII**, donde colmaderos preguntan por el e-CF; lo demás son notas de prensa (gremios, DGII) y textos de proveedores que describen sus dolores para venderles. Por eso el ranking de abajo es una **hipótesis con fuentes**, no un resultado. Antes de poner dinero hay que validarlo con dueños reales (§5): una semana de entrevistas cambia más el anuncio que cualquier búsqueda.
- **Las frases "en palabras del cliente"** salen de prensa y de lo que escriben los competidores para el mismo cliente (Alegra, Poszy). Cuando la frase es nuestra, se dice.
- **El sitio trata de "usted"** (TK-245). Los anuncios también, y con nombres neutros, sin jerga (`fiado`, no `fiao`).

## 1. Datos de contexto (con fuente)

| Dato | Fuente |
|---|---|
| Hay más de 65,000 colmados; 3 a 4 empleados en promedio; para el 84 % de dueños y empleados es el sustento de la familia; márgenes estrechos | Estudio de Emevenca citado por [elDinero, 12/09/2024](https://eldinero.com.do/294598/estudios-confirman-impacto-economico-de-los-colmados/) |
| Pequeños, micro y no clasificados deben emitir e-CF desde el **15/11/2026** (prórroga de 6 meses anunciada el 06/05/2026; antes era el 15/05) | [EY, alerta del 06/05/2026](https://www.ey.com/es_ce/technical/tax/tax-alerts/republica-dominicana-dgii-otorga-prorroga-de-seis-meses-a-contribuyentes-identificados-como-pequenos-micros-y-no-clasificados-para-la-implementacion-de-fe); `docs/ecf-propio.md` |
| Sanción por no emitir e-CF (Ley 32-23, art. 27): multa de 5 a 30 salarios mínimos y 0.25 % de los ingresos declarados del periodo anterior | [EY, 06/05/2026](https://www.ey.com/es_ce/technical/tax/tax-alerts/republica-dominicana-dgii-otorga-prorroga-de-seis-meses-a-contribuyentes-identificados-como-pequenos-micros-y-no-clasificados-para-la-implementacion-de-fe). Confirmar con un contador antes de usarla en un anuncio |
| La DGII dice que **no habrá otra prórroga**; ~87 mil incorporados de más de 200 mil contribuyentes; 84,003 emisores autorizados en septiembre | [Diario Libre, 10/09/2026](https://www.diariolibre.com/economia/negocios/2026/09/10/dgii-advierte-no-habra-prorroga-para-la-factura-electronica/3655048) |
| Colmaderos en el foro de la DGII: el Facturador Gratuito "NO es aplicable para ellos"; "un colmado con ventas dentro de lo normal hace mucho más de 150 ventas"; "algunas no tienen computadora ni internet"; "nosotros no emitimos facturas" (RST). La DGII responde que no hay trato especial y que se factura cada venta a consumidor final | Foro DGII: [hilo de 2025](https://ayuda.dgii.gov.do/conversations/discusiones/la-facturacion-electronica-y-los-colmaderos/682b4e5dd8e46434703ff86b), [hilo de 2026](https://ayuda.dgii.gov.do/conversations/discusiones/facturacion-electronica-para-colmados/6993b00d1ef1280491e88040) |
| FENACODEP (gremio de colmados) pide posponer el e-CF obligatorio hasta 2028 por falta de recursos técnicos y presupuesto; se queja de apagones que "dañan inventarios perecederos" y de la competencia de hipermercados | [El Seis, 03/10/2026](https://elseis.do/noticias/fenacodep-exige-medidas-urgentes-para-proteger-los-colmados-en-su-50-aniversario) |
| La DGII fiscaliza comercios (unos 80 negocios chinos auditados, cierres por fallas de facturación) y prepara un registro móvil de 4 pasos para pequeños negocios | [Diario Libre, 29/09/2026](https://www.diariolibre.com/economia/negocios/2026/09/29/comercios-chinos-dgii-determina-mas-de-rd1500-mm-fiscalizados/3673636) |
| Proveedores de e-CF cobran entre RD$890 y RD$4,500 al mes; el certificado privado cuesta unos RD$2,360 (blog, no verificado) | [Bolsillo Práctico, abr. 2026](https://bolsillopractico.com/?p=1331) |
| Apagón nacional del 23/02/2026 (~US$200 MM de pérdidas); hasta 20 interrupciones al mes en 2026; RD$141.8 MM perdidos por colmados y minimarkets por la cadena de frío (ene–oct 2024) | [El Caribe, 26/02/2026](https://www.elcaribe.com.do/panorama/dinero/estiman-us200-mm-perdidas-por-apagon/), [Revista Mercado, 09/06/2026](https://revistamercado.do/actualidad/apagones-en-republica-dominicana-empeoran-con-el-calor-interrupciones-aumentaron-hasta-20-veces-al-mes-en-2026/), [El Día, 02/12/2024](https://eldia.com.do/comerciantes-perdieron-141-millones-de-pesos-por-apagones/) |
| Pro Consumidor: el pesaje incorrecto en comercios de alimentos es "un reclamo permanente" (dato de 2021) | [El Caribe, 07/07/2021](https://www.elcaribe.com.do/actualidad/verificaran-balanzas-e-instrumentos-de-pesajes-en-comercios-de-alimentos/) |
| Fenacerd lanzó su propia app "Colmado Digital" con fiado, suplidores y préstamos: señal de qué piden sus miembros | [Saturno, 21/08/2025](https://saturno.com.do/nacionales/2025/fenacerd-lanzo-una-plataforma-digital-para-fortalecer-ecosistema-de-colmados/) |
| El certificado digital es "uno de los obstáculos que más ha frenado la adopción"; quienes esperaron al final tuvieron cuellos de botella y demoras en la habilitación | [elDinero, 21/05/2026](https://eldinero.com.do/365402/pymes-que-celebran-el-e-cf-extendido-podrian-estar-cometiendo-el-error-mas-caro-del-ano/) (cita a Alegra) |
| Más de 80,000 emisores electrónicos autorizados; el 54 % usa el Facturador Gratuito | DGII vía [beancount.io, 28/09/2026](https://beancount.io/es/blog/2026/09/28/dominican-republic-e-cf-electronic-invoicing-november-2026-deadline-guide) y [DGII](https://dgii.gov.do/noticias/Paginas/DGII-logra-recepcion-mas-2000-millones-Comprobantes-Fiscales-Electronicos.aspx) |
| El Facturador Gratuito tiene un tope de 150 facturas al mes (dicho por una funcionaria, no en una norma) y es "una herramienta simple" sin inventario | [El Día, 12/05/2026](https://eldia.com.do/quienes-pueden-usar-el-facturador-electronico-gratuito-de-la-dgii/) |
| Apagones de más de 10 horas seguidas en Santo Domingo Este; colmados con pérdidas de refrigerados | [Prensa Latina, 26/05/2026](https://www.prensa-latina.cu/2026/05/26/apagones-prolongados-generan-malestar-en-santo-domingo-este/) |
| Emergencia eléctrica de un año (Decreto 517-25, sept. 2025); apagón nacional del 13/11/2025 | [Acento](https://acento.com.do/opinion/emergencia-electrica-2025-un-llamado-a-la-planificacion-y-la-transparencia-9549321.html), [El Caribe](https://www.elcaribe.com.do/panorama/dinero/estiman-us200-mm-perdidas-por-apagon/) |
| Gremios de comerciantes calculan pérdidas de 20–25 % de ventas en épocas de apagones (dato viejo, 2017) | [Diario Libre, 07/07/2017](https://www.diariolibre.com/economia/comerciantes-calculan-perdidas-de-25-de-ventas-por-apagones-EF7564240) |
| El fiado vive en la libreta y la memoria del dueño, "uno de los sistemas financieros informales más estables del país" | [Acento, 03/10/2026](https://acento.com.do/opinion/la-matematica-del-colmado-9763338.html) |
| El robo interno suele estar entre un cuarto y un tercio de la merma; incluye "favoritismos" en la caja (cobrar menos a un amigo) | [beancount.io, 29/07/2026](https://beancount.io/es/blog/2026/07/29/retail-shrinkage-inventory-loss-bookkeeping-guide) (general, no RD) |
| En LatAm las carnes están entre lo más robado en supermercados | Censo de mermas citado en [Cleverence](https://www.cleverence.com/articles/business-blogs-es/combatting-inventory-shrinkage-effective-management-5829361/) (Colombia) |

### Lo que dice la competencia al mismo cliente

- **Alegra POS** ya anuncia para colmados: e-CF en cada venta, **modo sin conexión** que sincroniza al volver, fiado digital, cuadre por turno, inventario desde la foto de la factura y prueba de 15 días ([blog de Alegra, 14/05/2026](https://blog.alegra.com/republica-dominicana/sistema-inteligente-para-colmados/)). Sus dolores: fiado en cuaderno, no saber qué se agota, efectivo que "no siempre coincide", el internet que detiene la venta y el negocio que depende del dueño presente.
- **Poszy**: "El fiao deja de vivir en una libreta", "Cuadrar la caja sin discusión" y la urgencia del e-CF (`docs/competencia-poszy.md`).
- **Consecuencia:** "sigue vendiendo sin internet" **ya no es único** frente a Alegra. Lo que sí es nuestro: **varias cajas** sin internet contra la PC de la tienda, el e-CF **firmado en la tienda** (no en un servicio de afuera), la **balanza** y el **cuadre ciego**. El anuncio tiene que decir lo concreto, no solo "funciona offline".
- **LatAm** (secundario): Loyverse sin conexión no hace devoluciones, no registra clientes nuevos y no muestra existencias ([ayuda de Loyverse](https://help.loyverse.com/help/offline-work-of-pos)); Square no lee etiquetas de peso variable de algunas balanzas Mettler Toledo según sus propios usuarios ([comunidad de Square](https://community.squareup.com/t5/Questions-How-To/Will-Square-recognize-a-variable-weight-barcode-label-printed-by/m-p/42600)); Eleventa (México) recibe quejas de cajas secundarias que pierden la conexión con la principal ([Capterra](https://www.capterra.es/software/197855/eleventa)). Sirve para la página de comparar, no para anuncios en RD.

## 2. Ranking de dolores

**Cómo se calcula:** frecuencia (F: cuántos dueños lo viven) × intensidad (I: cuánto les duele o les cuesta) × resolución (R: cuánto lo resuelve Abastalia **hoy**, según `backlog/BACKLOG.md`), cada uno de 1 a 5. Máximo 125. F e I son estimaciones con las fuentes de §1; R sale del backlog. Validar con §5.

| # | Dolor | En palabras del cliente | F | I | R | Puntos | Qué hace Abastalia hoy | Fuente |
|---|---|---|---|---|---|---|---|---|
| 1 | **El e-CF obligatorio y no sé por dónde empezar** (plazo, certificado, multa) | "Nosotros no emitimos facturas", "el facturador gratuito no es aplicable para ellos", "hace mucho más de 150 ventas" (foro DGII) | 5 | 5 | 4 | **100** | e-CF directo con la DGII firmado en la tienda, sin cupo; guía de certificación; páginas /e-cf, /guia-e-cf, /comparar. Falta el precio del acompañamiento (TK-236) | Foro DGII 2025–2026, Diario Libre 10/09/2026, EY, El Día 12/05/2026 |
| 2 | **La caja no cuadra** (faltantes, descuentos y anulaciones que nadie explica, el cajero que cobra menos al amigo) | "Me falta dinero y no sé dónde", "el efectivo no coincide con lo vendido" | 5 | 4 | 5 | **100** | Cuadre ciego por denominaciones, Z, autorizaciones con PIN de un solo uso, permisos por acción, auditoría que no se puede borrar | Alegra 14/05/2026, beancount 29/07/2026, `funcionalidades-v1.md` §4 dolor 1 |
| 3 | **Se va el internet (o la luz) y se para la venta** | "Van a seguir los apagones, vamos a seguir perdiendo dinero" (Conacerd), "se cayó el sistema y la fila esperando" (nuestra) | 4 | 4 | 4 | **64** | Las cajas venden contra la PC de la tienda y el e-CF sale dentro de las 72 h. **La luz no la resolvemos**: hace falta inversor o UPS (decirlo claro) | El Día 02/12/2024, Revista Mercado 09/06/2026, Prensa Latina 26/05/2026, FENACODEP 03/10/2026 |
| 4 | **El fiado en la libreta** (no sé cuánto me deben, no me pagan, discusiones) | "El fiado deja de vivir en una libreta" (Poszy), "¿cuánto le debo?" | 4 | 3 | 5 | **60** | Fiado con límite, abonos, antigüedad 30/60/90, estado de cuenta en PDF para WhatsApp, separado en el Z | Acento 03/10/2026, Alegra, Poszy |
| 5 | **No sé qué tengo ni qué se pierde** (inventario, mermas, carne que no rinde, lo que se agota) | "Vendo mucho pero no sé qué me deja sin existencia" (Alegra) | 4 | 4 | 3 | **48** | Kardex, conteo ciego, mermas con motivo, mínimos y pedido sugerido. **Falta** el rendimiento de cortes (módulo Carnicería) | Alegra, Cleverence |
| 6 | **El negocio depende de que yo esté ahí** | "Si no estoy, no sé qué pasó" | 4 | 3 | 4 | **48** | Panel en el celular (ventas, cuadres, quién debe) y la asistente Talia | Alegra 14/05/2026 |
| 7 | **La balanza no habla con la caja** (pesar, escribir el peso, equivocarse, etiquetas que el sistema no lee) | "Peso, escribo y a veces me equivoco" (nuestra); el pesaje es "un reclamo permanente" de los consumidores (Pro Consumidor) | 3 | 4 | 3 | **36** | Lee etiquetas EAN-13 con peso o precio, libras con 3 decimales. La lectura en vivo de la balanza **falta probar con equipo real** (TK-117) | El Caribe 07/07/2021, comunidad de Square, `docs/legacy/03` |
| 8 | **El sistema cuesta y no tengo con qué** (mensualidad, certificado, cupos, "solo lectura" si no paga; "no tienen computadora ni internet") | "Falta de recursos técnicos y presupuesto" (FENACODEP) | 4 | 3 | 3 | **36** | Sin cupo ni cobro por documento; nunca en solo lectura. Pero pide una PC Windows; precios aún sin publicar (TK-203); versión ligera en la nube en estudio (TK-266) | FENACODEP 03/10/2026, foro DGII 2026, Bolsillo Práctico |
| 9 | **Cambiar de sistema y enseñar al cajero** | "No tengo tiempo de aprender otro programa" (nuestra) | 3 | 3 | 3 | **27** | Datos de ejemplo, teclas visibles. Falta el instalador desde la web (TK-196) | `funcionalidades-v1.md` §4 dolores 11–12 |
| 10 | **Los precios suben y el margen no da** | "La inflación se siente primero en el colmado" (Acento) | 5 | 4 | 1 | 20 | Utilidad con costo promedio; poco más. No es ángulo de anuncio | Acento 03/10/2026, elDinero 2024 |

**Lectura:** los dos primeros empatan, pero el **e-CF tiene fecha** (faltan 30 días al 16/10/2026) y la **caja es permanente**. Primero e-CF, y la caja como mensaje de fondo que dura todo el año. El internet sube si el anuncio dice lo concreto (varias cajas, firma en la tienda).

**Ojo con el colmado más pequeño:** en el foro de la DGII hay colmados sin computadora ni internet, y el gremio pide aplazar el e-CF por falta de presupuesto. Ese dueño no es el cliente de hoy (Abastalia pide una PC Windows) y un anuncio de e-CF muy amplio lo atraerá. Segmentar hacia tiendas con caja o PC (carnicerías, delis, minimarkets, colmados con más de una caja) y decir "en la PC de su tienda" en el anuncio. El de una caja sin PC queda para TK-266. La balanza puntúa bajo en general, pero es **el** dolor de la carnicería y el que ningún competidor dominicano menciona: es el ángulo de segmento.

## 3. Ángulos de anuncio (los 5 principales)

Límites usados: Google Search (anuncio adaptable) **30 caracteres** por título y **90** por descripción, contados uno por uno; Meta, texto principal de unos **125** caracteres antes de "Ver más" y título de unos **40**. No usar "DGII" de forma que parezca un anuncio oficial: Abastalia no es la DGII ni está avalada por ella.

### Ángulo 1 · "El e-CF del 15 de noviembre, sin enredos" (dolor 1)

- **Para quién:** dueños de colmado, carnicería, deli y minimarket que todavía no emiten e-CF o usan el Facturador Gratuito.
- **Mensaje:** le toca el 15/11; Abastalia emite el e-CF en cada venta, firma en su tienda, sin cupo, y le acompaña en la certificación.
- **Destino:** `/e-cf` (ya existe) y `/comparar` para quien busca "facturador gratuito".
- **Cuándo:** ya, hasta el 15/11/2026 (la DGII dijo que no habrá otra prórroga). Después cambia a "¿Ya emite e-CF? ¿Y su caja cuadra?" (ángulo 2) y a quien todavía no cumple.
- **Cuidado:** citar la multa solo con la fuente revisada por un contador (TK-198); mejor el tono de ayuda que el de miedo. El gremio de colmados pide aplazar el e-CF: no burlarse de eso ni presionar con cierres.

**Google Search: títulos** (≤ 30)
1. ¿Ya le toca el e-CF? (20)
2. e-CF obligatorio el 15/11 (25)
3. Facture e-CF desde la caja (26)
4. Sin cupo ni cobro por factura (29)
5. Le guiamos en la certificación (30)
6. Para colmados y carnicerías (27)

**Google Search: descripciones** (≤ 90)
1. La DGII exige e-CF a pequeños y micro desde el 15/11/2026. Vea qué necesita y empiece hoy. (90)
2. Abastalia emite el e-CF en cada venta, firma en su tienda y no cobra por factura. (81)

**Meta** (imagen: la caja con el recibo y el código QR del e-CF)
- Texto: "Desde el 15 de noviembre, colmados y carnicerías pequeñas deben emitir e-CF. Abastalia lo emite en cada venta, desde la PC de su tienda. Vea en 2 minutos qué necesita."
- Título: "¿Ya le toca el e-CF?"
- Botón: "Más información" → `/e-cf`

### Ángulo 2 · "Sepa por qué no cuadra la caja" (dolor 2)

- **Para quién:** dueños con uno o más cajeros que no están todo el día en el mostrador.
- **Mensaje:** el cajero cuenta sin ver lo esperado; usted ve la diferencia, quién dio cada descuento y cada anulación, desde el celular.
- **Destino:** Inicio (`/`), con la sección de cuadre; o una página nueva `/cuadre` si los datos lo justifican (ver §7).
- **Cuidado:** no acusar al empleado ("¿su cajero le roba?"): ofende al cajero que también ve el anuncio y Meta limita textos que afirman cosas de la persona. Hablar de la caja, no de la gente.

**Google títulos:** La caja cuadra al cerrar (24) · Cuadre ciego al cerrar turno (28) · Sepa quién cobró y cuánto (25) · Descuentos solo con su PIN (26) · Vea su caja desde el celular (28) · Cierre Z en una página (22)

**Google descripciones:**
1. El cajero cuenta sin ver lo esperado. Usted ve la diferencia y quién la aprobó. (79)
2. Cada descuento, anulación y faltante queda registrado con nombre y hora. (72)

**Meta**
- Texto: "¿Al cerrar le falta dinero y nadie sabe por qué? Con Abastalia el cajero cuenta sin ver lo esperado, y usted ve cada descuento y anulación desde el celular."
- Título: "La caja cuadra al cerrar"
- Botón: "Más información" (o "Enviar mensaje" cuando haya WhatsApp)

### Ángulo 3 · "Se fue el internet. Sus cajas siguen vendiendo." (dolor 3)

- **Para quién:** tiendas con mala conexión o en zonas de apagones, y quienes tienen más de una caja.
- **Mensaje:** las cajas venden contra la PC de la tienda; el e-CF se firma ahí y se envía al volver la conexión (la DGII da 72 h).
- **Destino:** Inicio (el título ya es "Venda, pese y facture. Aunque se vaya el internet.").
- **Cuidado:** decir **internet**, no **luz**. Sin electricidad no vende ningún sistema; si se menciona la luz, que sea "con un inversor, sigue vendiendo".

**Google títulos:** Venda y facture sin internet (28) · Sin internet, sigue vendiendo (29) · Varias cajas, sin internet (26) · El e-CF sale al volver la red (29)

**Google descripción:** Si se va el internet, sus cajas siguen vendiendo y el e-CF se envía al volver la conexión. (90)

**Meta**
- Texto: "Se fue el internet y la fila esperando. Con Abastalia sus cajas siguen cobrando y facturando; los e-CF salen solos cuando vuelve la conexión."
- Título: "Venda aunque se vaya el internet"
- Video corto sugerido: desconectar el cable del router y seguir cobrando (15 s, sin narración, con subtítulos).

### Ángulo 4 · "El fiado claro, sin libreta" (dolor 4, colmados)

- **Para quién:** colmados y minimarkets de barrio.
- **Mensaje:** quién le debe, cuánto y desde cuándo; límite por cliente y el estado de cuenta para mandar por WhatsApp.
- **Destino:** `/colmado`.
- **Cuidado:** el dueño quiere al cliente que le debe; nada de "deudores" ni "morosos". Es orden, no cobranza.

**Google títulos:** El fiado claro, sin libreta (27) · Quién le debe y desde cuándo (28) · Límite de fiado por cliente (27) · Estado de cuenta por WhatsApp (29)

**Google descripción:** Fiado con límite, abonos y antigüedad. El estado de cuenta listo para WhatsApp. (79)

**Meta**
- Texto: "La libreta se moja, se pierde y no suma. Con Abastalia ve quién le debe, cuánto y desde cuándo, y le manda el estado de cuenta por WhatsApp."
- Título: "El fiado claro y la caja al día"

### Ángulo 5 · "Cada libra cobrada" (dolor 7 + 5, carnicerías y delis)

- **Para quién:** carnicerías, delis, pescaderías y polleras con balanza etiquetadora.
- **Mensaje:** la caja lee la etiqueta de la balanza, vende por libra con 3 decimales, encargos con abono y el inventario por libra.
- **Destino:** `/carniceria` y `/deli`.
- **Cuidado:** **no** prometer "conecta su balanza" hasta cerrar TK-117 con equipo real; hoy se dice "lee las etiquetas de su balanza".

**Google títulos:** Punto de venta para carnicería (30) · Cada libra cobrada (18) · Lee etiquetas de la balanza (27) · Por libra con 3 decimales (25) · Encargos con abono y peso real (30)

**Google descripciones:**
1. Lee etiquetas de balanza con peso o precio. Libras con 3 decimales y cuarto frío aparte. (88)
2. Encargos con fecha y abono, cobrados con el peso real. Inventario por libra y mermas. (85)

**Meta**
- Texto: "Etiqueta de la balanza, escáner y listo: la libra se cobra con su precio y su e-CF. Hecho para carnicerías y delis, aunque se vaya el internet."
- Título: "Cada libra cobrada, cada caja cuadrada"

### Audiencias

| Canal | Segmentación propuesta | Por qué |
|---|---|---|
| Google Search | República Dominicana; idioma español e inglés (muchos tienen el teléfono en inglés); horario de 6:00 a 22:00; solo búsqueda (sin Display al inicio) | Capta a quien ya busca "facturación electrónica" o "sistema para colmado": la intención más alta |
| Meta (Facebook e Instagram) | RD; 25–60 años; intereses de pequeña empresa, emprendimiento, comercio minorista y administración de negocios; personas que administran páginas de negocio; ubicación con más peso en Santo Domingo, Santiago y el Este (donde más apagones se reportan) | El dueño no busca "punto de venta" hasta que tiene el problema; en Meta se le pone delante el dolor |
| Meta, remarketing | Visitantes de `/e-cf`, `/comparar` y de las páginas por negocio (necesita TK-262 y la decisión del dueño sobre el píxel; hoy el sitio no tiene cookies) | Repetir el mensaje a quien ya mostró interés |
| Canal sin anuncios | Contadores y revendedores con el programa de referidos (TK-199) | El contador es a quien el dueño le pregunta por el e-CF |

### Llamadas a la acción

| Momento | Texto del botón o enlace | Hoy |
|---|---|---|
| Duda del e-CF | "Vea si ya le toca" → `/e-cf` | ✅ Listo |
| Quiere hablar | "Escríbanos por WhatsApp" | ⛔ Falta el número (`site.whatsapp` vacío en `config.ts`) |
| Quiere probar | "Pruebe 14 días gratis" (título de 21 caracteres) | ⛔ Falta el instalador en la web (TK-196) |
| Quiere precio | "Vea los planes" → `/precios` | 🟡 Precios por confirmar (TK-203, TK-249) |

**Recomendación:** WhatsApp es la llamada principal en RD (`competencia-poszy.md` §6). Mientras no haya número, solo se puede anunciar el ángulo 1 con destino `/e-cf`, que no necesita conversación.

## 4. Palabras clave candidatas (Google Ads, solo RD)

**Sin datos de volumen.** El Planificador de palabras clave de Google es gratis pero pide una cuenta de Google Ads (decisión del dueño); Google Trends sirve para comparar términos entre sí. Las listas salen de cómo escriben los propios proveedores y la prensa, y de TK-263.

| Grupo | Palabras (concordancia de frase o exacta) | Destino |
|---|---|---|
| e-CF | "facturación electrónica", "factura electrónica dgii", "e-cf dgii", "comprobante fiscal electrónico", "facturación electrónica 15 de noviembre", "programa de facturación electrónica", "certificado digital facturación electrónica", "proveedor de facturación electrónica" | `/e-cf`, `/guia-e-cf` |
| Facturador Gratuito | "facturador gratuito dgii", "facturador gratuito límite", "alternativa facturador gratuito" | `/comparar` |
| Punto de venta por negocio | "punto de venta para colmado", "sistema para colmado", "programa para colmado", "punto de venta para carnicería", "sistema para carnicería", "punto de venta para deli", "sistema de facturación para minimarket" | `/colmado`, `/carniceria`, `/deli` |
| Función | "punto de venta con balanza", "sistema de fiado", "control de fiado", "cuadre de caja", "punto de venta sin internet", "sistema de inventario para colmado" | Inicio o página del segmento |
| Competidores (fase 2, con cuidado) | "alternativa a alegra pos", "poszy" | `/comparar`; nunca usar la marca ajena en el texto del anuncio |

**Negativas de entrada:** empleo, trabajo, vacante, curso, tutorial, pdf, tesis, gratis crack, descargar gratis, iniciar sesión, login, oficina virtual, consulta rnc, consulta ncf, teléfono dgii, multa de tránsito, factura de luz, edesur, edenorte, edeeste.

## 5. Validar antes de gastar

Objetivo: confirmar el orden de los 5 dolores y elegir el mensaje que más mueve, con dueños reales, en 2 semanas y sin costo.

**Entrevistas (6–8 dueños, 20 minutos, en la tienda o por llamada):** 2–3 de cada segmento (carnicería, colmado, deli), con y sin sistema hoy. Reclutar por conocidos, revendedores y el piloto.

1. **Contexto (5 min):** ¿qué vende?, ¿cuántas cajas y empleados?, ¿qué usa hoy para cobrar y facturar?
2. **El último cierre (5 min):** "Cuénteme cómo fue el cierre de ayer." ¿Cuadró? Si no, ¿qué hizo? (Preguntar por el último caso real, no por opiniones.)
3. **Lo que más le quita el sueño (5 min):** "Si pudiera arreglar una sola cosa del negocio mañana, ¿cuál sería?" Luego mostrar 5 tarjetas (e-CF, caja, internet, fiado, balanza) y pedir que las ordene. Preguntar por qué la primera.
4. **e-CF (3 min):** ¿sabe si le toca?, ¿qué ha hecho?, ¿quién le ayuda (contador, proveedor)?, ¿qué le preocupa?
5. **Cierre (2 min):** ¿a quién le pregunta antes de comprar un sistema? ¿Dónde se informa (WhatsApp, Facebook, contador, YouTube)?

No mostrar Abastalia hasta el final; no preguntar "¿lo compraría?". Anotar las frases exactas: son el material para los anuncios.

**Encuesta por WhatsApp (5 preguntas, 2 minutos, a la lista de contactos del dueño y revendedores):**
1. Tipo de negocio (carnicería, colmado, deli, otro).
2. ¿Cuántas cajas tiene? (1, 2, 3 o más).
3. De estas, ¿cuál le molesta más? (una sola: e-CF, la caja no cuadra, el internet, el fiado, la balanza, el inventario).
4. ¿Ya emite e-CF? (sí, no, no sé si me toca).
5. ¿Cómo le gustaría que le contactemos? (WhatsApp, llamada, visita, no gracias).

**Prueba de mensajes (gratis):** mostrar los 5 títulos de Meta de §3 a cada entrevistado, uno por uno, y preguntar "¿qué cree que hace este producto?" y "¿cuál le haría escribir?". Gana el que se entiende sin explicación y más eligen. Con presupuesto (decisión del dueño), el siguiente paso es una prueba A/B pequeña en Meta con los 2 mejores.

## 6. Qué falta para poder lanzar (del dueño)

1. **Número de WhatsApp** para `config.ts` (sin él, ninguna campaña de conversación).
2. **Precios** publicados (TK-203, TK-249).
3. **Instalador** en la web para la prueba de 14 días (TK-196).
4. **Revisión de un contador** de la página `/e-cf` y de la multa citada (TK-198).
5. **Cuenta de anuncios** (Google Ads y Meta Business) y presupuesto. No se creó ninguna.
6. **Medición sin cookies** en el sitio (TK-262) para saber qué anuncio trae clientes.
7. **Prueba social:** fotos y una frase del dueño del piloto (`competencia-poszy.md` §6).

## 7. Tickets que salen de aquí (propuestos, no creados)

- **Página `/cuadre`** o sección de cuadre en el inicio, si las entrevistas confirman el dolor 2 como el primero después del e-CF.
- **Video corto "sin internet"** de 15 s para Meta con la caja real.
- **Rendimiento de cortes** (módulo Carnicería): sube R del dolor 5 de 3 a 5 y da un ángulo propio ("sepa cuánto le rinde la res").
- **Cerrar TK-117** con una balanza real: permite decir "conecta su balanza" en los anuncios de carnicería.
