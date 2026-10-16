# Competencia: GLECF frente a Toki

> Ticket TK-218 (#188). 15 de octubre de 2026. Solo investigación: no cambia código.
> Fuentes: el sitio público de GLECF (cada afirmación lleva su URL, leída el 15/10/2026) y los documentos de este repositorio (`backlog/BACKLOG.md`, `docs/ecf-propio.md`, `docs/permisos.md`, `docs/metricas-producto.md`, `docs/competencia-poszy.md`).
> No se creó cuenta, no se llenaron formularios y no se instaló la app. Lo que GLECF **no dice** en su sitio se marca como "no se menciona"; eso no prueba que no lo tenga.

## 1. Quién es GLECF

| | |
|---|---|
| Empresa | GL Creaciones SRL, RNC 132-15887-3, Bávaro, Punta Cana ([términos](https://glecf.com/terminos/)). |
| Producto | ERP en la nube con facturación electrónica incluida. Se presenta como "el único ERP gratis del país" con e-CF ([inicio](https://glecf.com/)). Se instala desde el navegador como app (Android, iPhone, computadora), sin tienda de aplicaciones ([instalar](https://glecf.com/instalar/)). |
| Posicionamiento | **No es un punto de venta.** Es un ERP de oficina: cliente → cotización → orden → conduce → factura e-CF → pago ([ERP](https://glecf.com/erp/)), más compras, nómina, contabilidad, préstamos, activos fijos y sucursales. El gancho es el e-CF gratis y la certificación ante la DGII hecha desde la app. |
| Otros productos | API REST para emitir e-CF desde otro software ([API](https://glecf.com/api/)), conector de Odoo y de Shopify, bandeja de WhatsApp/Instagram/Messenger con IA que redacta y una persona envía ([inicio](https://glecf.com/), [IA](https://glecf.com/ia/)). |
| IA | Asistente "Glé", de solo lectura, que responde con los datos del negocio y cita de dónde salen; lectura de facturas de suplidor en PDF. Dicen que corre sobre Claude Sonnet 5 de Anthropic desde agosto de 2026 ([IA](https://glecf.com/ia/)). |
| Madurez | Dice estar "en producción" y emitiendo e-CF reales, con "cero rechazos ante la DGII", sin número de clientes ni testimonios ([nosotros](https://glecf.com/nosotros)). La página de novedades empieza el 2/10/2026 y la certificación hecha por GLECF salió el 8/10/2026 ([novedades](https://glecf.com/novedades)). Hecho "con ayuda de inteligencia artificial", según el pie de [nosotros](https://glecf.com/nosotros). |
| Contacto | Formulario de contacto, Instagram y correo de lunes a viernes de 9:00 a 18:00 ([términos](https://glecf.com/terminos/)). Sin teléfono ni WhatsApp de ventas publicados ([inicio](https://glecf.com/)). |

## 2. Precios (vistos el 15/10/2026)

Todo en RD$; **no publican precios en US$** ([precios](https://glecf.com/precios/)).

| Plan | Precio | e-CF al mes | Usuarios | Incluye |
|---|---|---|---|---|
| **Gratis** | RD$0, "para siempre", sin tarjeta | 25 | 1 | El ERP completo; la certificación la hace el cliente desde la app; soporte por correo "según disponibilidad" |
| **Licencia Anual** (recomendado) | **RD$6,300 al año** (unos RD$525 al mes), pago por adelantado, "no es una mensualidad" | hasta 1,000 | hasta 3 | Lo anterior + asistente Glé, asistente de WhatsApp, **certificación guiada** (la hace GLECF), reporte 606, lectura de PDF de suplidores por lote, conservación por 10 años, soporte incluido |
| **A medida** | Por contrato | más de 1,000 | más de 3 | Lo de la Licencia Anual y lo que pida el cliente |

Notas ([precios](https://glecf.com/precios/), [términos](https://glecf.com/terminos/)):
- Solo cuentan los e-CF de producción. No publican cargo por excedente: al pasarse se sube de plan.
- El **certificado digital no está incluido**; se compra a una entidad autorizada (recomiendan Viafirma, [certificación](https://glecf.com/certificacion/)).
- Sin prorrateo ni reembolso. Si la licencia vence sin renovar, **la cuenta queda en solo lectura**. Cambios de precio con 30 días de aviso, en la renovación.
- Sin SLA ("no garantizamos un porcentaje de disponibilidad"); responsabilidad limitada a lo pagado en 12 meses. Al cerrar, los comprobantes se guardan 10 años y lo demás se borra en 30 días.

**Lectura:** la Licencia Anual es más barata que el Básico de Poszy (RD$950 al mes). Pero el cupo no sirve para el comercio de mostrador: 1,000 e-CF al mes son unos 33 por día, y una carnicería o un colmado emite cientos de facturas de consumo (32) al día. Una tienda así cae en el plan "a medida", sin precio publicado. Además, el sitio solo menciona el 606, no el 607 ni el 608 ([precios](https://glecf.com/precios/), [ERP](https://glecf.com/erp/)).

## 3. La "prueba" de GLECF

**GLECF no tiene una prueba de 14 días.** Ninguna página la menciona ([inicio](https://glecf.com/), [precios](https://glecf.com/precios/), [instalar](https://glecf.com/instalar/), [términos](https://glecf.com/terminos/), [novedades](https://glecf.com/novedades)). Lo que ofrece es un **plan gratis permanente** con límites de uso (25 e-CF, 1 usuario), sin tarjeta y sin instalación, y la invitación "Crear cuenta gratis" en cada página. La API sugiere probar primero en un ambiente interno que no toca la DGII ([API](https://glecf.com/api/)).

Consecuencia para TK-196: los 14 días de Toki son una decisión propia del dueño, no un calco. Lo que sí conviene tomar de GLECF es **qué pasa al terminar** (ver §7 y TK-220).

## 4. Acompañamiento en la certificación

Es lo más fuerte de GLECF ([certificación](https://glecf.com/certificacion/), [novedades](https://glecf.com/novedades)).

**Requisitos que pide al cliente:** RNC activo y al día, claves de la Oficina Virtual (OFV) de la empresa y del representante, alta de NCF, certificado `.p12` del representante.

**Pantalla "Certificación DGII" en cuatro pasos:**
1. **Certificado:** se sube y muestra titular, emisor y vencimiento.
2. **Solicitud:** el cliente escribe su clave de la OFV y **GLECF envía la FI-GDF-016** por él; revisa los datos con las reglas de la DGII.
3. **Set de pruebas:** GLECF descarga el set y genera, firma y envía cada uno de los 15 pasos con un botón, "sin escribir XML ni subir archivos a mano". Registra por el cliente las URL de su facturador (pasos 7 y 12).
4. **Listo para facturar:** consulta a la DGII si ya es emisor; el cliente carga sus secuencias e-NCF y pasa a producción.

GLECF vigila el portal de la DGII y avisa cada avance en la app y por correo.

**Quién hace qué y cuánto cuesta:**

| | Plan Gratis | Licencia Anual (RD$6,300/año) |
|---|---|---|
| Quién opera la pantalla | El cliente | Un usuario de soporte de GLECF ("hazla por mí") |
| Qué hace siempre el cliente | RNC, claves, certificado, aceptar la declaración jurada | Igual: lee y acepta la declaración jurada y una autorización desde su usuario |
| Costo aparte | No publicado | No publicado: va dentro de la licencia |
| Tiempo | No lo dan: "los plazos los pone la DGII" | Igual |

**Si ya es emisor con otro programa:** no repite la certificación. Sube su certificado, registra el dominio de GLECF en "Mantenimiento Directorio de Facturadores Electrónicos" de la OFV y pasa a producción. Explican el error típico: escribir solo el dominio, no la URL completa ([certificación](https://glecf.com/certificacion/)).

**Diferencia de fondo con el diseño de Toki:** GLECF **recibe la clave de la OFV del cliente** para enviar la solicitud. `docs/ecf-propio.md` §8 decidió lo contrario: Toki no entra a la OFV ni al portal con las credenciales del dueño; prepara y firma los archivos y el dueño hace los pasos de su clave. El servicio de Toki (TK-219) debe dar la misma comodidad sin pedir esa clave (ver §7).

## 5. Comparación por funcionalidad

Leyenda de Toki: ✅ hecho · 🔨 en curso · ⬜ planeado · — no previsto.

| Área | GLECF (según su sitio) | Toki hoy | Toki planeado |
|---|---|---|---|
| **Tipo de producto** | ERP de oficina en la nube ([ERP](https://glecf.com/erp/)) | ✅ Punto de venta local-primero para alimentos, con panel en la nube | — |
| **Caja, turnos, cuadre** | No se menciona | ✅ Turno por caja, cuadre ciego por denominaciones, Z, permisos de caja separados (TK-212) | — |
| **Peso y balanza** | No se menciona | ✅ Libra con 3 decimales, etiquetas EAN-13; 🔨 balanza en vivo (TK-117) | — |
| **Fiado** | Cuentas por cobrar y "cómo suele pagar cada contacto" ([novedades](https://glecf.com/novedades)) | ✅ Fiado con límite, antigüedad, estado de cuenta | — |
| **Sin internet** | No se menciona; es una app web | ✅ La tienda vende y firma sin internet; cola de 72 h | ⬜ Contingencia serie B (TK-157) |
| **e-CF tipos** | Los 10: 31, 32, 33, 34, 41, 43, 44, 45, 46, 47 ([ERP](https://glecf.com/erp/)) | 🔨 31, 32 (RFCE), 33, 34 contra XSD; falta TesteCF (TK-141) | 41 de compras después de V1 (TK-159) |
| **e-CF en producción** | Sí, según el sitio ([nosotros](https://glecf.com/nosotros)) | ⬜ Hoy simulado; camino propio en M13 | TK-141, TK-158 |
| **Cupo de e-CF** | 25 / 1,000 / a medida ([precios](https://glecf.com/precios/)) | — | Uso justo, nunca se detiene |
| **Certificación DGII** | ✅ En la app, un botón por paso; "hazla por mí" con la licencia | ⬜ Asistente (TK-155) | Acompañamiento como servicio (TK-219) |
| **Cambio desde otro emisor** | Documentado, sin recertificar ([certificación](https://glecf.com/certificacion/)) | — | ⬜ TK-221 |
| **Aviso de vencimiento del certificado** | Muestra el vencimiento al subirlo ([certificación](https://glecf.com/certificacion/)) | ✅ Aviso a 30 días (TK-176) | ✅ |
| **Avisos** | Notificación push aunque la app esté cerrada (rechazo de la DGII) y correo ([instalar](https://glecf.com/instalar/)) | ✅ Alertas en el Resumen y la barra de la caja | ⬜ TK-222 |
| **Reportes fiscales** | 606, armado desde el Excel de comprobantes recibidos de la DGII y completado con el XML del suplidor ([ERP](https://glecf.com/erp/), [novedades](https://glecf.com/novedades)). 607 y 608 no se mencionan | ✅ 606, 607 y 608 | ⬜ Cruce con el Excel de la DGII (TK-223) |
| **Compras desde la factura del suplidor** | Lectura con IA de PDF por lote, revisada por una persona ([IA](https://glecf.com/ia/)) | — | ⬜ TK-224 |
| **Cotizaciones** | ✅ Con ahorro del cliente y descuento por línea ([novedades](https://glecf.com/novedades)) | ✅ TK-194 | ✅ |
| **Usuarios y permisos** | Roles Administrador y Usuario ([ERP](https://glecf.com/erp/)); en Glé, siete interruptores por persona ([IA](https://glecf.com/ia/)) | ✅ Permisos por acción, roles de partida, nadie da lo que no tiene (TK-212 a TK-216) | ✅ |
| **Auditoría** | Más de 40 tipos de evento con antes y después ([ERP](https://glecf.com/erp/)) | ✅ Encadenada por hash, verificada cada noche | ✅ |
| **Asistente IA** | Glé de solo lectura, cita la fuente ([IA](https://glecf.com/ia/)) | ✅ Asistente del dueño en el panel | ✅ |
| **Mensajería** | Bandeja de WhatsApp/Instagram/Messenger con borradores de IA ([IA](https://glecf.com/ia/)) | ✅ Catálogo con pedido por WhatsApp (TK-195) | Recordatorio de cobro por WhatsApp (add-on) |
| **Integraciones** | API REST, Odoo, Shopify ([API](https://glecf.com/api/), [inicio](https://glecf.com/)) | — | — (no es el segmento) |
| **Panel con más de una persona** | Hasta 3 usuarios en la licencia | ✅ Dueño y Solo lectura en el panel (TK-217) | ✅ |
| **Métricas de producto** | No se menciona | ✅ Embudo, retención y salud (TK-190) | Etapa de certificación por cliente (TK-225) |
| **Novedades públicas** | ✅ Página de cambios con fechas ([novedades](https://glecf.com/novedades)) | — | ⬜ TK-226 |
| **Plazos e-CF** | ✅ Página con los plazos por grupo, verificados con la DGII el 31/8/2026 ([plazos](https://glecf.com/plazos/)) | — | ⬜ TK-198 |
| **Al vencer o no pagar** | La cuenta queda en solo lectura ([términos](https://glecf.com/terminos/)) | ✅ Gracia de 15 días y luego Restringido: vender, e-CF, exportar y respaldar nunca se detienen | ✅ |

## 6. Dónde cada uno va delante

### GLECF va delante

1. **Certificación resuelta dentro de la app, hoy.** Un botón por paso del set de pruebas, URL registradas por ellos, avisos de cada avance y la opción de que su equipo lo haga todo. Toki tiene el diseño (`docs/ecf-propio.md` §8) pero TK-155 no está hecho y todavía no se probó en TesteCF.
2. **Entrar cuesta cero y dura un minuto.** Plan gratis permanente, sin tarjeta y sin instalar nada ([precios](https://glecf.com/precios/), [instalar](https://glecf.com/instalar/)). Toki pide una PC principal y un instalador.
3. **Camino claro para quien ya factura con otro programa** ([certificación](https://glecf.com/certificacion/)).
4. **606 sin digitar:** Excel de la DGII + XML del suplidor + lectura de PDF con IA ([ERP](https://glecf.com/erp/), [IA](https://glecf.com/ia/)).
5. **Avisos fuera de la app** (push y correo) cuando la DGII rechaza o avanza la certificación ([instalar](https://glecf.com/instalar/), [certificación](https://glecf.com/certificacion/)).
6. **Confianza por transparencia:** precios públicos, página de novedades, página de plazos y términos legibles.
7. **Precio de la licencia:** RD$6,300 al año, con certificación incluida.

### Toki va delante

1. **Es un punto de venta.** Caja, turnos, cuadre ciego, peso y balanza, fiado en el mostrador, varias cajas. GLECF no menciona nada de esto.
2. **Funciona sin internet** y firma en la tienda con cola de 72 h. GLECF es solo web.
3. **Sin cupo de e-CF.** Para una tienda de mostrador el cupo de GLECF (1,000 al mes) se acaba en la primera semana del mes; el precio "a medida" no se publica.
4. **607 y 608** además del 606.
5. **El dueño no entrega la clave de la OFV.** Toki prepara y firma; el dueño hace lo suyo en su navegador (`docs/ecf-propio.md` §8).
6. **Nunca en solo lectura.** Vencida la licencia, la tienda sigue vendiendo, emitiendo e-CF y exportando.
7. **Permisos finos** (caja, compras, pagos, reportes fiscales separados) frente a Administrador/Usuario.

## 7. Especificaciones para TK-196 y TK-219

### TK-196 · Prueba gratis de 14 días (agregar al alcance)

Lo que GLECF enseña: el cliente decide en la primera sesión, no en el día 14. Su gancho es que todo funciona sin pagar y el límite es el uso, no el reloj.

- **Los 14 días cuentan desde la primera venta**, no desde la instalación (quien instala un viernes y abre el lunes no pierde días). El reloj vive en la licencia de la tienda (`LicenseStatus.Trial` hoy no vence) y se refleja en el panel.
- **Todo encendido durante la prueba**, incluido el e-CF en **TesteCF** (sin valor fiscal, con el aviso que ya existe en `/admin/ecf`), para que el dueño vea su factura con QR antes de certificarse.
- **Avisos** en la caja y en el panel los días 10 y 13, y por correo (TK-222).
- **Al terminar:** la tienda **no queda en solo lectura** (a diferencia de GLECF). Pasa a Restringido: 1 caja, vende, cobra y exporta; se apagan las funciones de pago. Detalle en TK-220.
- **Conversión en un paso:** "Comprar licencia" abre WhatsApp o un enlace de pago con el código de la tienda; la licencia nueva se aplica sin reinstalar y conserva los datos.
- **Medir** la prueba con el embudo de `docs/metricas-producto.md`: instalada → primera venta → 7 días con ventas → compra.

### TK-219 · Acompañamiento en la certificación (servicio)

Lo que GLECF enseña: el cliente quiere "que me lo hagan"; la parte humana vende más que la pantalla. Lo que Toki hace distinto: **nunca recibe la clave de la OFV**.

**Dos niveles, como GLECF:**

| | Por tu cuenta | Con Toki |
|---|---|---|
| Quién opera el asistente (TK-155) | El dueño | Un técnico de Toki, por sesión remota con el dueño presente |
| Pasos con la clave de la OFV (FI-GDF-016, postulación, URL, declaración jurada, secuencias) | El dueño, guiado por la lista | El dueño teclea su clave en la sesión; el técnico dice qué marcar. Toki no guarda ni ve la clave |
| Set de pruebas, simulación, PDF, acuses | Toki, automático | Toki, automático; el técnico vigila rechazos |
| Precio | Incluido en la licencia | Cobro único (ya previsto "alta e-CF", `docs/competencia-poszy.md` §3); decisión del dueño |

**Lo que incluye el servicio:**
1. **Antes:** lista de requisitos en una página (RNC al día, claves de OFV, alta NCF, representante). Guía para comprar el certificado `.p12` con Avansi o Digifirma (precio y formato por confirmar, `docs/ecf-propio.md` §11 #4).
2. **Cita 1 (30–60 min):** importar el `.p12` (TK-176), enviar la FI-GDF-016 desde la OFV del dueño, delegar el rol de Firmante si hace falta.
3. **Mientras la DGII responde:** Toki corre el set y la simulación en cuanto se abre el portal; el técnico ve el avance en la consola (TK-225) y avisa al dueño por WhatsApp.
4. **Cita 2:** URL de producción, declaración jurada (la acepta y firma el dueño, como en GLECF), pedir secuencias, cargarlas y pasar el ambiente a `eCF`.
5. **Después:** primera venta real revisada con el dueño; aviso de rechazos activo (TK-222).

**Promesa medible:** los pasos 2 a 11 en una tarde (`docs/ecf-propio.md` §8). No prometer el total: el tiempo de la DGII no depende de Toki (GLECF tampoco lo promete).

**Página pública "Certifícate con Toki"** (TK-226 o dentro de TK-197): requisitos, etapas, quién hace qué, precio del servicio y la respuesta a "ya facturo con otro programa" (TK-221).

**Antes de vender el servicio:** TK-141, TK-152 (receptor en la nube para los pasos 8–11), TK-155 y TK-158 deben estar hechos; certificar primero la tienda piloto.

## 8. Brechas priorizadas (impacto × esfuerzo)

Esfuerzo S (≤ 2 días), M (≤ 1 semana), L (más).

| Prioridad | Ticket | Brecha | Impacto | Esfuerzo |
|---|---|---|---|---|
| 1 | TK-155 + TK-219 | Certificación en la app y como servicio | Alto | L |
| 2 | TK-220 | Qué pasa al terminar la prueba de 14 días | Alto | S |
| 3 | TK-221 | Cambiarse a Toki ya siendo emisor electrónico | Alto | S |
| 4 | TK-222 | Avisos por correo y notificación de rechazos y del avance de la certificación | Medio | M |
| 5 | TK-225 | Seguimiento de la certificación por cliente en la consola | Medio | S |
| 6 | TK-223 | Cruzar compras con el Excel de comprobantes recibidos de la DGII | Medio | M |
| 7 | TK-224 | Compra en borrador desde la factura del suplidor (PDF o foto) con IA | Medio | M |
| 8 | TK-226 | Página de novedades y página "Certifícate con Toki" | Medio | S |

**Agregar a tickets existentes:** TK-198 debe incluir la tabla de plazos por grupo (como [plazos](https://glecf.com/plazos/)), con los 120 días de las empresas nuevas. TK-197 debe decir "sin cupo de e-CF" y "nunca en solo lectura".

**No copiar:** nómina, contabilidad, préstamos, activos fijos, API pública, conectores de Odoo y Shopify, bandeja de redes sociales. Son de un ERP de oficina, no del mostrador de alimentos. Tampoco pedir la clave de la OFV del cliente.

## 9. Tickets propuestos

### TK-220 · Fin de la prueba de 14 días (S)
- **Alcance:** la licencia de prueba guarda la fecha de la primera venta y vence 14 días después; avisos en la caja y en la administración los días 10 y 13; al vencer pasa a Restringido (1 caja, vender, cobrar, e-CF, exportar y respaldar siguen; funciones de pago apagadas); botón "Comprar licencia" con el código de la tienda; la nube conoce el estado (prueba, activa, restringida) para el panel y la consola. Depende de TK-196.
- **Aceptación:** prueba con reloj falso: día 13 avisa, día 15 restringe sin bloquear la venta ni el e-CF; aplicar una licencia pagada vuelve a Activa sin perder datos; una tienda instalada sin ventas no consume días.

### TK-221 · Cambiarse a Toki ya siendo emisor electrónico (S)
- **Alcance:** en el asistente de certificación, la opción "Ya emito e-CF con otro programa": importar el `.p12`, mostrar las URL de Toki para "Mantenimiento Directorio FE" (solo el dominio, con el error típico explicado), cargar las secuencias que quedan y cambiar el ambiente a `eCF`. Confirmar con la DGII que no hay que recertificar ni anular los rangos del otro programa (TK-177).
- **Aceptación:** guía y pantalla probadas con una tienda en TesteCF; un e-CF enviado después del cambio sale aceptado; la guía dice qué hacer con los números que el programa anterior dejó sin usar.

### TK-222 · Avisos fuera de la app: rechazos, certificado y certificación (M)
- **Alcance:** la nube manda correo (y, con el add-on, WhatsApp) al dueño cuando la DGII rechaza un e-CF, el certificado vence en 30 días, la cola pasa de 24 h o la certificación avanza de paso. Necesita eventos de la tienda hacia la nube (rechazo, vencimiento, paso de certificación) sin datos del comprador. Preferencias por aviso en el panel.
- **Aceptación:** un rechazo en TesteCF llega por correo en menos de 10 minutos con internet; apagar un aviso en el panel lo detiene; el correo no lleva RNC ni nombre del comprador.

### TK-223 · Cruzar compras con el Excel de comprobantes recibidos de la DGII (M)
- **Alcance:** subir el Excel de la OFV con los comprobantes recibidos del mes; Toki lo cruza con las compras y gastos registrados y lista los que faltan, los duplicados y los montos distintos; "Crear compra en borrador" desde un faltante. Mejora el 606 sin digitar.
- **Aceptación:** con un Excel de ejemplo, el cruce encuentra faltantes, duplicados y diferencias; el 606 después de completar coincide con el Excel; prueba de integración del cruce.

### TK-224 · Compra en borrador desde la factura del suplidor con IA (M)
- **Alcance:** subir el PDF o la foto de la factura del suplidor (o el XML del e-CF recibido, cuando exista TK-154); la IA propone suplidor, NCF, líneas, cantidades y costos, emparejados con los productos; una persona revisa y guarda el borrador. Nada se aplica sin revisión. Es el "factura del suplidor por foto" ya previsto.
- **Aceptación:** con 10 facturas reales de carnicería, el borrador sale con suplidor y NCF correctos en todas y con las líneas emparejadas en la mayoría; ningún costo ni existencia cambia hasta aplicar la compra; el documento original queda adjunto.

### TK-225 · Seguimiento de la certificación por cliente en la consola (S)
- **Alcance:** la tienda informa a la nube el paso de certificación en que está (solo el número y la fecha, sin XML ni claves); la consola de plataforma (TK-189) lista los clientes por paso, con días en el paso y los atascados más de 5 días; etapa nueva "Certificado" en el embudo de `docs/metricas-producto.md`.
- **Aceptación:** una tienda que avanza en TesteCF/CerteCF aparece en el paso correcto; el evento no lleva datos fiscales; la consulta queda auditada.

### TK-226 · Páginas de novedades y de certificación (S)
- **Alcance:** página pública de novedades con fecha (generada desde las notas de versión) y aviso "Novedades" en la administración tras actualizar; página "Certifícate con Toki" con requisitos, etapas, quién hace qué, precio del servicio (TK-219) y el caso de quien ya es emisor (TK-221). Va junto con TK-197.
- **Aceptación:** ambas páginas publicadas y rápidas en el celular; la administración muestra la novedad una vez por versión.

## 10. Fuentes (GLECF)

- https://glecf.com/ (inicio y sección de precios)
- https://glecf.com/precios/
- https://glecf.com/certificacion/
- https://glecf.com/erp/
- https://glecf.com/ia/
- https://glecf.com/api/
- https://glecf.com/instalar/
- https://glecf.com/plazos/
- https://glecf.com/novedades
- https://glecf.com/nosotros
- https://glecf.com/terminos/
