# Preguntas abiertas de Abastalia

Un solo lugar con todo lo que falta decidir o confirmar fuera del código, agrupado por quién responde. Cada punto dice qué se pregunta, por qué importa, qué hace Abastalia hoy mientras tanto y dónde está el detalle. Al tener la respuesta: escribirla en la línea **Respuesta:**, con fecha y quién la dio, y actualizar el ticket o el documento enlazado.

Fuentes: `backlog/BACKLOG.md`, `docs/ecf-propio.md` §11, `docs/servicio-certificacion.md`, `docs/cuotas.md`, `docs/competencia-*.md`, `docs/despliegue-azure.md`, `docs/instalador-web.md`, `docs/adr/0011-firma-del-receptor-en-la-tienda.md` y `backlog/reviews/`. Si una pregunta se resuelve en su documento de origen, se borra de aquí.

| Grupo | Quién responde | Puntos |
|---|---|---|
| 1 | Dueño de Abastalia | 18 |
| 2 | DGII | 35 + pasos de TK-141 |
| 3 | Contador | 6 |
| 4 | Abogado | 5 |
| 5 | INDOTEL | 1 |
| 6 | Hardware y pruebas de campo | 7 |
| 7 | Piloto | 3 |

---

## 1. Dueño de Abastalia (decisiones de negocio)

### D1 · Precios y límites de los planes (TK-203, TK-197)
- **Pregunta:** ¿Cuánto cuestan y qué limita cada plan (Una caja, Varias cajas, Varias sucursales)? ¿Hace falta un plan de entrada para el colmado de una caja frente a Poszy (RD$950 al mes)?
- **Por qué importa:** el sitio no se puede anunciar sin precios; el plan propuesto cuesta varias veces el Básico de Poszy.
- **Hoy:** (TK-293) `config.ts` → `prices.monthlyUsd` con los provisionales del 16/10/2026; el sitio los muestra marcados "provisionales" mientras el cobro con Paddle esté apagado.
- **Fuente:** TK-203, TK-197; `docs/competencia-poszy.md` §3 y "Lectura"; `src/Cloud/panel/src/pages/site/config.ts`.
- **Respuesta:** (16/10/2026) Hacer un análisis con un consejo de analistas del mercado dominicano para identificar qué planes benefician al proyecto y a los clientes (en curso). (16/10/2026, TK-258) Aprobados como **provisionales**: Esencial US$12, Estándar US$19 y Pro US$35 al mes (anual = 10 meses), todas las funciones en todos, y adicionales con cantidad; detalle en `docs/cobro-paddle.md`. Pendiente: precio por cajero.

### D2 · Precio de la instalación
- **Pregunta:** ¿Cuánto cuesta la instalación (pago único)?
- **Por qué importa:** es uno de los precios pendientes antes de anunciar el sitio.
- **Hoy:** `prices.installation` en `null`.
- **Fuente:** `docs/despliegue-azure.md` ("Pendiente del dueño antes de anunciar el sitio"); `config.ts`.
- **Respuesta:** Sale del análisis de D1. La instalación debe ser fácil para el usuario; el negocio de Abastalia es la post-instalación, con horas de soporte.

### D3 · Precio y condiciones del acompañamiento en la certificación (TK-236)
- **Pregunta:** ¿Precio del servicio, cómo se cobra, cuántas reprogramaciones y reintentos incluye y qué se devuelve si la DGII rechaza la certificación de forma definitiva?
- **Por qué importa:** el servicio (TK-219) está listo salvo esto; sin precio no se puede vender.
- **Hoy:** `[PRECIO DEL SERVICIO]` en el documento; `prices.certificationSupport` en `null` ("Precio por confirmar" en `/certificacion`); el documento propone reprogramar una vez sin costo y reenviar la FI-GDF-016 sin costo.
- **Fuente:** TK-236; `docs/servicio-certificacion.md` §6 y §7.
- **Respuesta:** El acompañamiento debe ser válido y mínimo; la app y el asistente (bot) deben ayudar más.

### D4 · Horario y tiempo de respuesta del acompañamiento
- **Pregunta:** ¿En qué horario se atiende el servicio y en cuánto tiempo se responde?
- **Por qué importa:** se publica en el sitio y en los correos de las citas.
- **Hoy:** "por definir"; no se publica hasta tener personal asignado.
- **Fuente:** `docs/servicio-certificacion.md` §8.
- **Respuesta:** SLA por plan: planes baratos con SLA bajo. Investigar los mejores (en el análisis de D1).

### D5 · Programa de referidos (TK-199)
- **Pregunta:** ¿Qué incentivo reciben revendedores, contadores y clientes que refieren (comisión recurrente o meses gratis)?
- **Por qué importa:** define el código de referido en el alta y el reporte de pagos.
- **Hoy:** no existe.
- **Fuente:** TK-199; `docs/competencia-poszy.md` (TK-199).
- **Respuesta:** Se puede configurar: descuento en el siguiente pago, solo cuando el referido paga un plan específico (no cuando entra a la plataforma).

### D6 · ¿La caja espera hasta 5 s el resultado de una 31 antes de imprimir? (TK-208)
- **Pregunta:** Para la factura de crédito fiscal (31), ¿la caja espera unos 5 segundos la respuesta de la DGII antes de imprimir?
- **Por qué importa:** con espera, el cliente recibe la factura ya aceptada; sin ella, la fila avanza más rápido pero la RI puede salir con la leyenda de contingencia.
- **Hoy:** la venta nunca espera a la DGII; el diseño propone esperar hasta 5 s solo en la 31 e imprimir con leyenda si no llega.
- **Fuente:** TK-208; `docs/ecf-propio.md` §10 ("Cuándo imprimir la leyenda de contingencia").
- **Respuesta:** Sí, esperar la respuesta de la DGII.

### D7 · Dominio
- **Pregunta:** ¿Qué dominio usan el sitio, el panel, la API y el correo?
- **Por qué importa:** sin dominio no hay URL canónica; el dominio administrado de Azure limita los correos por hora.
- **Hoy:** `config.ts` → `url` vacío; correo con el dominio administrado de Azure.
- **Fuente:** TK-197; `docs/despliegue-azure.md` §Pendiente; `config.ts`.
- **Respuesta:** Un dominio sobre "abastalia", el que esté disponible, con subdominios (app., api.).

### D8 · Número de WhatsApp
- **Pregunta:** ¿Qué número de WhatsApp usa Abastalia para ventas, soporte y el acompañamiento?
- **Por qué importa:** "Hablemos" y los mensajes del técnico dependen de él.
- **Hoy:** `config.ts` → `whatsapp` vacío; los botones van a `/precios#contacto`.
- **Fuente:** TK-197; `docs/servicio-certificacion.md` §8; `config.ts`.
- **Respuesta:** Sin número por ahora.

### D9 · Configuración de Paddle (TK-249)
- **Pregunta:** ¿Cuenta de Paddle, productos y precios a cargar para el cobro?
- **Por qué importa:** pedido del dueño para cobrar en línea.
- **Hoy:** TK-249 todavía no está en `backlog/BACKLOG.md` ni hay documento de cobro en el repositorio; no hay código de Paddle.
- **Fuente:** pedido del dueño (TK-249).
- **Respuesta:** Hecho: la llave de la API de sandbox ya se pasó y se pidió crear los productos (creados).

### D10 · Custodia del certificado en la nube (TK-247, ADR-0011)
- **Pregunta:** ¿Se enciende el modo CloudCustody cuando se cumplan las condiciones? ¿Se asume el costo de las que piden dinero (llaves en HSM con Key Vault Premium o Managed HSM, seguro de ciberriesgo, mandato notarizable)?
- **Por qué importa:** con la PC apagada el receptor no acusa; la custodia lo resolvería, con riesgo legal y de seguridad.
- **Hoy:** apagado (`Fe:AllowCloudCustody` = false); la tienda firma (StoreSigned). Condiciones 1 a 7 en ADR-0011; la 2 ya está hecha.
- **Fuente:** `docs/adr/0011-firma-del-receptor-en-la-tienda.md` ("Condiciones para encender B"); TK-246; TK-247.
- **Respuesta:** Queda apagado por ahora; si se enciende, se hace con HSM (TK-247).

### D11 · Aprobación comercial desde el panel que la tienda no puede firmar (TK-250)
- **Pregunta:** Si el dueño decide en el panel y la tienda no tiene certificado, ¿basta con el registro o debe avisársele?
- **Por qué importa:** el dueño cree que aprobó y la decisión nunca sale.
- **Hoy:** se reintenta en cada consulta de la tienda y solo queda en el registro (atajo de TK-250).
- **Fuente:** TK-250 en `backlog/BACKLOG.md`; nota TK-250 en ADR-0011.
- **Respuesta:** Debe haber conectividad entre la nube y la tienda sin exponer la aplicación (así funciona: la tienda abre la conexión hacia la nube).

### D12 · Precio del adicional de lectura de facturas con IA
- **Pregunta:** ¿Cuánto cuesta al mes el adicional `SupplierInvoiceReading` (300 facturas)?
- **Por qué importa:** la tienda lo sugiere cuando se agotan las 20 incluidas.
- **Hoy:** `[PRECIO]`.
- **Fuente:** TK-248; `docs/cuotas.md` ("Servicio adicional").
- **Respuesta:** Lo responde el análisis de D1.

### D13 · Cuotas mensuales por plan
- **Pregunta:** ¿Se aprueban los límites propuestos por plan (preguntas al asistente, facturas leídas, correos, invitaciones, etc.)?
- **Por qué importa:** fijan el costo máximo de cada negocio y lo que ve el cliente al pasarse (429).
- **Hoy:** valores por defecto en `appsettings.json`, propuestos en el documento.
- **Fuente:** TK-248; `docs/cuotas.md` ("Cuotas mensuales por plan").
- **Respuesta:** Lo responde el análisis de D1.

### D14 · Bloquear la retención del archivo frío (TK-207)
- **Pregunta:** ¿Se bloquea la política de retención de 10 años del contenedor `ecf-archive`? Es irreversible.
- **Por qué importa:** bloqueada, nadie (ni Abastalia) puede borrar un e-CF antes de 10 años.
- **Hoy:** retención puesta pero sin bloquear.
- **Fuente:** TK-207; `docs/despliegue-azure.md` ("Bloquear la retención").
- **Respuesta:** "Debe mantenerse" (por confirmar si significa bloquearla, que es irreversible).

### D15 · Revisión de la guía de contingencia (TK-156)
- **Pregunta:** ¿La guía de contingencia para el dueño de la tienda se entiende y se aprueba?
- **Por qué importa:** es lo que lee el dueño cuando no puede emitir.
- **Hoy:** escrita, sin revisar.
- **Fuente:** TK-156; `docs/contingencia-ecf.md`.
- **Respuesta:** No.

### D16 · Plan B ante el plazo del 15/11/2026 (§11 #2)
- **Pregunta:** Si la primera tienda no queda certificada a tiempo, ¿se usa el Facturador Gratuito de la DGII para poco volumen?
- **Por qué importa:** el plazo de pequeños y micro vence el 15/11/2026.
- **Hoy:** prioridad a TesteCF y al piloto; sin plan B decidido.
- **Fuente:** `docs/ecf-propio.md` §11 #2.
- **Respuesta:** Sí, usar la propuesta (Facturador Gratuito de la DGII como plan B).

### D17 · Umbral del reloj para firmar (§11 #12)
- **Pregunta:** ¿Desde qué diferencia de hora con la DGII se deja de firmar (propuesta: 2 minutos)?
- **Por qué importa:** una `FechaHoraFirma` posterior a la hora de la DGII causa rechazo; bloquear de más detiene ventas.
- **Hoy:** implementado en TK-156 con 2 minutos (`Fiscal:MaxClockSkewSeconds`): pasado el umbral no se firma, pero la venta sigue y el e-CF se firma al corregir la hora.
- **Fuente:** `docs/ecf-propio.md` §11 #12; TK-156.
- **Respuesta:** el dueño aceptó la propuesta de 2 minutos.

### D18 · Certificado digital: entidad, formato y precio (§11 #4)
- **Pregunta:** ¿Con quién se compra el certificado de prueba (Avansi o Digifirma), en qué formato (`.p12` exportable, token o nube), a qué precio y con qué vigencia?
- **Por qué importa:** Abastalia solo importa `.p12`/`.pfx`; sin certificado no se prueba nada con la DGII (TK-141).
- **Hoy:** la guía del dueño pide `.p12` o `.pfx`; precio y formato "por confirmar".
- **Fuente:** `docs/ecf-propio.md` §11 #4 y §12 (TK-141); `docs/servicio-certificacion.md` §5.
- **Respuesta:** Se hará con la cuenta piloto (Abastalia no tendrá RNC); investigar cuál entidad es la más económica para nuestros clientes.

### D19 · Referidos: planes que califican y monto del descuento (TK-255)
- **Pregunta:** ¿Qué planes cuentan para el referido y cuánto se descuenta (porcentaje o monto) en el siguiente pago de quien refirió?
- **Por qué importa:** los referidos están construidos pero apagados hasta definir `Billing:Referrals`.
- **Hoy:** apagado. Además falta en Paddle: permiso de la llave de API para crear descuentos y los eventos `adjustment.*` en el destino de notificaciones.
- **Fuente:** `docs/cobro-paddle.md` (Referidos).
- **Respuesta:** (16/10/2026, TK-276) Califican los tres planes que se venden (Esencial, Estándar y Pro). Descuento **fijo de US$12** en el próximo pago de quien recomienda, hasta **3 por mes**. Quien llega recomendado tiene **30 días de prueba** en vez de 14. Con plan anual, las recompensas se juntan en un solo descuento al renovar, con un tope del 50 % del precio del plan. Sigue apagado mientras el cobro con Paddle esté apagado. Los contadores y revendedores van por otro canal (TK-199).

### D20 · Precio por cajero
- **Pregunta:** ¿Cuánto cuesta cada cajero adicional, cuántos incluye cada plan y cómo se cuentan (propuesta: usuarios con PIN que entraron en el mes)?
- **Por qué importa:** el dueño quiere cobrar por cajero (trazabilidad de quién hizo qué); sin estas cifras no se carga en Paddle.
- **Hoy:** no construido; TK-258 deja el camino libre para un adicional "Cajero adicional" por cantidad.
- **Respuesta:** (16/10/2026, TK-277) Adicional `ExtraCashier` "Cajero adicional" a **US$2 al mes** (US$20 al año) por cajero. Incluidos: Esencial **2**, Estándar **4**, Pro **8** (Varias sucursales, 12); cada caja adicional suma 2 y cada sucursal adicional suma 4. Se cuentan los **cajeros habilitados** (usuarios activos que pueden vender o abrir turno), no los que entraron en el mes; el Dueño no cuenta. El tope va en la licencia y solo impide **habilitar** a otro cajero: nunca entrar, vender ni emitir e-CF. Desactivar a alguien libera su lugar y guarda su historial.

---

## 2. DGII

Canal: facturacionelectronica@dgii.gov.do · 809-689-3444, opción 4. Ticket para enviarlas: **TK-177** (necesita al dueño). Los números son los de `docs/ecf-propio.md` §11.

No están aquí, porque no son preguntas a la DGII: #2, #4 y #12 (decisiones del dueño, D16, D18 y D17), #29 (instalador, H4), #11, #13, #15 y #16 (riesgos ya cubiertos o tareas internas) y #14 (revisar la "Bitácora" cada mes).

### 2.1 Acceso a TesteCF (TK-141)

Sin esto no se puede probar nada contra la DGII real (todo está probado con una DGII simulada).

1. Elegir el RNC: el de Abastalia o el de la tienda piloto (decisión del dueño).
2. Enviar la solicitud FI-GDF-016 desde la OFV de ese RNC; designar al Usuario Administrador e-CF y delegar el rol de Firmante.
3. Comprar el certificado de persona física del firmante (D18).
4. Prueba de humo: semilla → token → una 32 por RFCE aceptada.
5. Enviar a la DGII las preguntas #1, #6, #7 y #8 (criterio de cierre de TK-141).

- **Fuente:** `docs/ecf-propio.md` §8 y §12 (TK-141), §11 #3.
- **Respuesta (fecha de acceso y RNC usado):**

### 2.2 Preguntas por escrito

- **#1 · ¿Custodiar certificados y alojar receptores convierte a Abastalia en "proveedor de servicios de FE" (NG 10-2021)?**
  - Por qué: la respuesta escrita es condición para encender CloudCustody (D10).
  - Mientras: no se custodia; la tienda firma (StoreSigned).
  - Respuesta:
- **#6 · ¿Cuál es la URL del servicio "Estatus de servicios / ventanas de mantenimiento"?**
  - Por qué: la salud de e-CF (TK-156) no puede avisar de una caída de la DGII.
  - Mientras: sin esa consulta.
  - Respuesta:
- **#8 · ¿Hay sanción para el receptor que no acusa (nube caída o tenant sin consentimiento)?**
  - Por qué: define cuánto riesgo hay en el 503 del receptor.
  - Mientras: 503 y lo recibido se guarda.
  - Respuesta:
- **#17 · ¿Redondear a 2 decimales un peso de 3 y recalcular el precio es lo esperado (`CantidadItem`)?**
  - Por qué: las carnicerías venden por peso con 3 decimales; el XSD admite 2.
  - Mientras: se redondea y se recalcula (§10.8, diferencia 2).
  - Respuesta:
- **#18 · ¿Se envía `<Comprador />` vacío en una 32 a consumidor anónimo o se omite?**
  - Por qué: choca con la regla de "no enviar etiquetas vacías".
  - Mientras: §10.8, diferencia 3; probar una 32 sin comprador.
  - Respuesta:
- **#19 · ¿Venta a crédito sin `FechaLimitePago`? ¿El saldo a favor es forma de pago 7 (nota de crédito) u 8 (otras)?**
  - Por qué: fiado y saldo a favor son de uso diario.
  - Mientras: sin respuesta; ver §11 #19.
  - Respuesta:
- **#20 · Anulación (`anularrango`): ¿qué nombre lleva el archivo y qué responde?**
  - Por qué: la Descripción Técnica no lo dice.
  - Mientras: `RNC + ANECF + fecha.xml`; un 200 sin código se toma como aceptado.
  - Respuesta:
- **#21 · e-CF de reemplazo de un rechazado: ¿lleva `InformacionReferencia`? ¿Fecha nueva o la de la venta? ¿Mismo e-NCF o uno nuevo con ANECF del viejo?**
  - Por qué: cada rechazo se reemplaza así.
  - Mientras: e-CF normal, fecha nueva, número nuevo.
  - Respuesta:
- **#22 · QR de un e-CF sin comprador: ¿se omite `RncComprador` o va vacío?**
  - Por qué: un QR mal armado no valida en la consulta.
  - Mientras: se omite.
  - Respuesta:
- **#24 · En CerteCF, ¿reenviar tras un timeout cuenta como envío duplicado en el set de pruebas?**
  - Por qué: en CerteCF no se consulta el TrackId por e-NCF; un rechazo reinicia el set.
  - Mientras: se reenvía.
  - Respuesta:
- **#25 · Nota de crédito sobre una venta con e-CF reemplazado: ¿va sobre el reemplazo?**
  - Por qué: devoluciones de ventas que tuvieron rechazo.
  - Mientras: sobre el e-CF vigente (el reemplazo).
  - Respuesta:
- **#26 · e-CF firmado y anulado con ANECF sin enviarse, cuya RI ya tiene el cliente: ¿basta el ANECF? ¿Cómo va en el 607 si no se reemplaza?**
  - Por qué: TK-205 lo permite.
  - Mientras: la pantalla recomienda reemplazar con número nuevo; el 607 lo lista con el e-NCF anulado.
  - Respuesta:
- **#28 · ¿Dónde trae el certificado el RNC o la cédula del titular, y debe coincidir con el RNC de la empresa?**
  - Por qué: Abastalia rechaza certificados de otro RNC al importarlos.
  - Mientras: lee `SERIALNUMBER` u `organizationIdentifier`.
  - Respuesta:
- **#31 · Regularización de la serie B: ¿la 32 de menos de RD$250,000 va por RFCE o completa? ¿Fecha del NCF B o del día? ¿Cómo se trata un B04?**
  - Por qué: TK-157 regulariza la contingencia.
  - Mientras: RFCE, fecha del día con `FechaNCFModificado` = la del B; e-CF 34 con `NCFModificado` = el B04.
  - Respuesta:
- **#32 · 607 de una venta con NCF B regularizada: ¿se reporta con el NCF B y sin el e-CF de reemplazo?**
  - Por qué: evita declarar la venta dos veces (ver también C4).
  - Mientras: con el NCF B; el reemplazo queda fuera.
  - Respuesta:
- **#34 · ¿El receptor debe validar que la cédula del firmante esté delegada para el RNC? ¿Publica la DGII las raíces de confianza?**
  - Por qué: solo la DGII sabe la delegación.
  - Mientras: valida la cadena (Avansi y Digifirma cargadas) y el RNC o una cédula; no consulta `consultaestado`.
  - Respuesta:
- **#36 · ¿Cómo comprobar que un RNC es de quien lo declara al activar el receptor con una cédula delegada?**
  - Por qué: con una cédula cualquier firmante válido podría declarar el RNC de una empresa.
  - Mientras: la tienda firma un reto de la nube; un segundo intento da `Fe.RncTaken` y lo resuelve soporte.
  - Respuesta:
- **#37 · Ante un e-CF duplicado, ¿se repite el acuse original (Estado 0) o se responde Estado 1, motivo 3?**
  - Por qué: si el primer acuse se perdió, el emisor nunca lo ve.
  - Mientras: Estado 1 motivo 3; como emisor, un motivo 3 se toma como entregado.
  - Respuesta:
- **#40 · Cambiarse a Abastalia ya siendo emisor: ¿hay que recertificar? ¿Se sigue la secuencia desde el siguiente número libre? ¿Se avisa el cambio en la OFV?**
  - Por qué: TK-221 lo ofrece.
  - Mientras: no recertifica; carga el resto del rango desde el número siguiente; si la DGII pide anular, se anula y se carga una secuencia nueva.
  - Respuesta:
- **#42 · ¿Cómo se regulariza una nota B03 o B04 emitida en contingencia?**
  - Por qué: el reemplazo pierde la referencia a la factura original (ver también C4).
  - Mientras: solo regulariza B01 y B02; B03 y B04 quedan pendientes con un aviso y frenan el 607 del período.
  - Respuesta:
- **#43 · ¿Acepta la DGII una venta de RD$ 0 (descuento del 100 %) sin `TablaFormasPago`?**
  - Por qué: el XSD la admite, pero puede haber validaciones de negocio (ver también C5).
  - Mientras: se permite con autorización de supervisor.
  - Respuesta:
- **#44 · Receptor con la PC apagada: ¿qué hace el emisor ante un 503? ¿Vale un acuse tardío enviado por el receptor, y a qué servicio? ¿Hay plazo para acusar?**
  - Por qué: en StoreSigned el receptor depende de la PC de la tienda.
  - Mientras: 503 con `Retry-After: 60`; lo recibido se guarda; el acuse tardío se firma y se guarda, **no se envía**.
  - Respuesta:
- **#45 · ¿Hay plazo para enviar el ACECF? ¿Hay códigos de motivo de rechazo o siempre es texto libre?**
  - Por qué: la aprobación comercial se decide en la tienda o en el panel (TK-250).
  - Mientras: sin plazo ni cuenta regresiva; motivo libre con sugerencias.
  - Respuesta:

### 2.3 Por comprobar en TesteCF o CerteCF (necesitan TK-141)

- **#5 · ¿El set de pruebas exige tipos que la tienda no usa (41, 43–47)?**
  - Mientras: el generador del set es genérico por columnas.
  - Respuesta:
- **#7 · ¿Acepta la DGII un prefijo de ruta por tenant en las URL del receptor (`/{amb}/{rnc}/fe/…`) o exige un host por contribuyente?**
  - Mientras: prefijo de ruta.
  - Respuesta:
- **#9 · ¿Vida real del token y límites de envío por minuto?**
  - Mientras: se lee `expira`; token en caché hasta 5 min antes.
  - Respuesta:
- **#10 · ¿Llevan `FechaVencimientoSecuencia` la 32 y la 34 de producción?**
  - Mientras: según el formato (no va); ver la autorización real en la OFV.
  - Respuesta:
- **#23 · ¿Cuánto esperar con código 0 ("no encontrado") antes de reenviar?**
  - Mientras: sigue consultando (máximo cada 5 minutos) y no reenvía.
  - Respuesta:
- **#27 · ¿Acepta la DGII un ANECF de un solo número dentro de un rango en uso, y para una 32 enviada por RFCE que falló?**
  - Mientras: lo permite (TK-205).
  - Respuesta:
- **#30 · ¿Forma real del Excel del set de pruebas (nombres de columnas, repetidos, `#e`, hojas)?**
  - Mientras: nombre del elemento sin mayúsculas, índices `[n]`, `#e` o vacío = no se envía, primera hoja con `TipoeCF` y `eNCF`, `Padre.Nombre` para repetidos.
  - Respuesta:
- **#33 · ¿Alguna entidad pone el RNC de la empresa en el sujeto del certificado?**
  - Mientras: acepta cédula en el de una empresa; rechaza otro RNC.
  - Respuesta:
- **#35 · Las URL del directorio (`obtenerdirectorioporrnc`), ¿son la base del contribuyente o el servicio completo?**
  - Mientras: acepta las dos.
  - Respuesta:
- **#38 · Bajar los XSD de los tipos recibidos 41, 43, 44, 45, 46 y 47.**
  - Mientras: se acusan sin validar el XSD (sí firma, RNC y campos que usa la tienda).
  - Respuesta:
- **#39 · ¿Hay emisores (o el simulador) que envían sin autenticarse cuando existe la URL de autenticación?**
  - Mientras: el receptor exige token (`Fe:RequireToken` = true).
  - Respuesta:
- **#41 · ¿Forma real del Excel de comprobantes recibidos de la OFV?**
  - Mientras: el cruce (TK-223, TK-240) lee `.xlsx`, `.xls` y `.csv`; encabezado en las primeras 15 filas; columnas por nombre. Necesita un archivo real del piloto.
  - Respuesta:

---

## 3. Contador

### C1 · Revisar un mes de 606 y 607 (TK-161, R5 #3)
- **Pregunta:** ¿Se pueden pegar tal cual en la herramienta de la DGII? En particular: qué va en las columnas de pago de una nota de crédito, si los emisores de e-CF siguen reportando en el 607 lo que la DGII ya recibió, y el código de forma de pago del 606.
- **Por qué importa:** un 607 mal armado declara más ingresos de los que hubo.
- **Hoy:** columnas en el orden de la DGII, montos sin ITBIS (R5 corregido); sin revisión de un contador.
- **Fuente:** TK-161; `backlog/reviews/R5-M8-M9.md` #1 y #3.
- **Respuesta:**

### C2 · ITBIS llevado al costo en el 606
- **Pregunta:** La carne está exenta: ¿el ITBIS pagado en compras para ventas exentas va en "ITBIS llevado al costo"? ¿Cómo se separan bienes y servicios?
- **Por qué importa:** cambia el crédito de ITBIS de las carnicerías.
- **Hoy:** bienes para compras y servicios para gastos tipo 2 o 3; sin "ITBIS llevado al costo".
- **Fuente:** `backlog/reviews/R5-M8-M9.md` #3.
- **Respuesta:**

### C3 · Tipos de anulación del 608 (TK-192)
- **Pregunta:** ¿Son correctos el tipo 08 para los e-NCF anulados por ANECF y el 04 para los rechazados por la DGII?
- **Por qué importa:** el 608 se presenta cada mes.
- **Hoy:** 08 y 04; el TXT oficial lo arma la herramienta de la DGII.
- **Fuente:** TK-192.
- **Respuesta:**

### C4 · Regularización de B03 y B04 y 607 con NCF B
- **Pregunta:** ¿Cómo se regulariza una nota B03 o B04 emitida en contingencia y cómo va en el 607 una venta con NCF B regularizada?
- **Por qué importa:** los B03 y B04 pendientes frenan el 607 del período.
- **Hoy:** B03 y B04 quedan pendientes con aviso "hágalo con su contador"; el 607 usa el NCF B.
- **Fuente:** `docs/ecf-propio.md` §11 #32 y #42; `backlog/reviews/R9-mtls-receptor-prueba.md` #11.
- **Respuesta:**

### C5 · Venta de RD$ 0
- **Pregunta:** ¿Cómo se trata un obsequio o una promoción del 100 % (comprobante, 607, inventario)?
- **Por qué importa:** la caja ya la permite.
- **Hoy:** venta de RD$ 0 sin pagos, con supervisor; cuenta en "Ventas" del Z; e-CF sin `TablaFormasPago`.
- **Fuente:** TK-239; `docs/ecf-propio.md` §11 #43.
- **Respuesta:**

### C6 · Revisión de la página `/e-cf` (TK-198)
- **Pregunta:** ¿Es correcto lo que dice la página "¿Ya te toca?" (fechas, requisitos, 72 h, serie B, FAQ)? ¿A qué páginas de la DGII se enlaza?
- **Por qué importa:** es contenido público sobre obligaciones fiscales.
- **Hoy:** publicada solo con lo de `docs/ecf-propio.md`, sin tabla de todos los grupos y con aviso de que no es asesoría legal.
- **Fuente:** TK-198; `docs/competencia-poszy.md` (criterio: "revisado por un contador").
- **Respuesta:**

---

## 4. Abogado

### A1 · Términos del servicio
- **Pregunta:** ¿Texto de los términos (prueba de 14 días, gracia y modo restringido, responsabilidad, datos al cancelar)?
- **Por qué importa:** el sitio no puede publicarse sin ellos; el pie aún no los enlaza.
- **Hoy:** no existen.
- **Fuente:** TK-197, TK-245.
- **Respuesta:**

### A2 · Privacidad y Ley 172-13
- **Pregunta:** ¿Política de privacidad para lo que sube a la nube, el panel en el celular y el asistente con IA? ¿Qué exige la transferencia internacional (los datos viven en Azure eastus2)?
- **Por qué importa:** datos personales de clientes y cajeros salen del país.
- **Hoy:** no existe la página; ADR-0008 limita lo que sube; las métricas no suben datos nuevos.
- **Fuente:** TK-197; `docs/funcionalidades-v1.md` §9 punto 9; ADR-0011 condición 7; `docs/metricas-producto.md` §Privacidad.
- **Respuesta:**

### A3 · Mandato de custodia del certificado
- **Pregunta:** ¿Cómo debe ser el mandato notarizable del dueño y del titular, y el texto del consentimiento, para custodiar su certificado en la nube?
- **Por qué importa:** condición 6 y 7 para encender CloudCustody (D10).
- **Hoy:** CloudCustody apagado; texto de consentimiento versionado, sin revisión legal.
- **Fuente:** ADR-0011 condiciones 6 y 7; `docs/ecf-propio.md` §10 ("Consentimiento").
- **Respuesta:**

### A4 · Ley 126-02 (comercio electrónico, documentos y firmas digitales)
- **Pregunta:** ¿Valen los consentimientos y autorizaciones que el dueño y el titular aceptan en pantalla, o hace falta firma digital?
- **Por qué importa:** el consentimiento del receptor y las aprobaciones comerciales desde el panel se aceptan en pantalla.
- **Hoy:** aceptación en pantalla, auditada.
- **Fuente:** pedido del dueño; no aparece aún en los documentos.
- **Respuesta:**

### A5 · Registro del nombre en ONAPI
- **Pregunta:** ¿Está libre "Abastalia" para registrarlo como marca?
- **Por qué importa:** el nombre y el logo ya están en el producto y el sitio.
- **Hoy:** sin búsqueda.
- **Fuente:** `docs/funcionalidades-v1.md` §9 punto 9.
- **Respuesta:**

---

## 5. INDOTEL

### I1 · Otras entidades de certificación
- **Pregunta:** Además de Avansi y Digifirma, ¿hay otras entidades vigentes (OPTIC, Lleida.net, Novosit) que emitan certificados para e-CF?
- **Por qué importa:** si un suplidor firma con otra entidad, el receptor rechaza su e-CF (motivo 2).
- **Hoy:** `Fe:TrustedRoots` solo con Avansi (Viafirma) y Digifirma.
- **Fuente:** `docs/ecf-propio.md` §10 ("Confianza"); `docs/guia-tecnico-certificacion.md`.
- **Respuesta:**

---

## 6. Hardware y pruebas de campo

### H1 · App de la tienda en una PC de caja real (TK-114)
- **Pregunta:** ¿Abre bien y Ctrl+R, zoom y deslizar no hacen nada?
- **Hoy:** compila; no se ha abierto en una PC de caja.
- **Fuente:** TK-114; R7 #11.
- **Respuesta:**

### H2 · Balanzas reales (TK-117)
- **Pregunta:** ¿Leen bien CAS (PD-II, ER), Toledo 8217 y el envío continuo por COM?
- **Por qué importa:** la carnicería vende por peso.
- **Hoy:** probadas sin equipo; se puede escribir el peso con motivo.
- **Fuente:** TK-117.
- **Respuesta:**

### H3 · Impresora térmica y gaveta (TK-118)
- **Pregunta:** ¿Imprimen bien la factura y el Z (58 y 80 mm, QR) y abre la gaveta?
- **Hoy:** probado sin equipo; simulada en desarrollo.
- **Fuente:** TK-118; TK-239.
- **Respuesta:**

### H4 · Instalador y servicio en una VM Windows limpia (TK-160)
- **Pregunta:** ¿Funcionan la cuenta del servicio, la llave del certificado en el almacén de la máquina (§11 #29) y `backups\work`?
- **Hoy:** sin probar en una máquina limpia; el certificado queda en `CurrentUser\My` de la cuenta del servicio.
- **Fuente:** TK-160; R5 #6; R6 2, 5 y 10; `docs/ecf-propio.md` §11 #29.
- **Respuesta:**

### H5 · Certificado de firma de código (Authenticode)
- **Pregunta:** ¿Qué certificado se compra y quién lo guarda?
- **Por qué importa:** sin él, SmartScreen asusta en la primera descarga.
- **Hoy:** instalador sin firmar (compra del dueño).
- **Fuente:** TK-242; `docs/instalador-web.md` §Pendiente.
- **Respuesta:**

### H6 · Dónde se aloja el feed de actualizaciones
- **Pregunta:** ¿GitHub Releases o Azure Blob, y con qué URL?
- **Por qué importa:** sin feed no hay actualizaciones ni enlace de descarga.
- **Hoy:** `Updates:FeedUrl` y `config.ts` → `installerUrl` vacíos; `/descargar` muestra `[URL DEL INSTALADOR]`.
- **Fuente:** TK-242; `config.ts`.
- **Respuesta:**

### H7 · Instalación silenciosa de SQL Server Express 2025
- **Pregunta:** ¿Son correctos el enlace, los parámetros y el tamaño? ¿Compila el instalador con Inno Setup 6?
- **Hoy:** sin verificar.
- **Fuente:** TK-242; `docs/instalador-web.md` §Pendiente.
- **Respuesta:**

---

## 7. Piloto

### P1 · Validar con 6 dueños reales (TK-172)
- **Pregunta:** ¿Entienden y usan la administración y el panel en el celular (entrevistas y prueba de usabilidad)?
- **Hoy:** solo investigación y crítica internas.
- **Fuente:** TK-172; `docs/investigacion-admin.md`.
- **Respuesta:**

### P2 · Fotos y testimonios para el sitio
- **Pregunta:** ¿Autoriza la tienda piloto fotos, un video del dueño y cifras de cuadre en el sitio?
- **Por qué importa:** ni Poszy ni GLECF tienen testimonios.
- **Hoy:** el sitio usa dibujos y "cifras ilustrativas".
- **Fuente:** `docs/competencia-poszy.md` ("Prueba social"); TK-245.
- **Respuesta:**

### P3 · Certificar la tienda piloto de punta a punta (TK-237)
- **Pregunta:** ¿Cuánto tarda de verdad cada paso de `docs/servicio-certificacion.md` y qué hay que cambiar en el guion antes de vender el servicio?
- **Hoy:** sin hacer; depende de TK-141, TK-148, TK-152, TK-155 y TK-158.
- **Fuente:** TK-237.
- **Respuesta:**
