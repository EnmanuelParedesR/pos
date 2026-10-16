# Servicio de acompañamiento en la certificación e-CF (TK-219)

> Plan del servicio, 16 de octubre de 2026. Parte de `docs/competencia-glecf.md` §7 (especificación de TK-219), del proceso de la DGII en `docs/ecf-propio.md` §8 y del asistente que ya existe en la app (TK-155, Escuadra Admin → "Comprobantes electrónicos" → "Certificación con la DGII"). Aún no se vende: ver "Antes de ofrecerlo" al final. No es asesoría legal ni fiscal.

## 1. Qué es y para quién

Un servicio de pago único en el que un técnico de Toki lleva al dueño de la mano hasta que su negocio queda autorizado por la DGII para emitir comprobantes fiscales electrónicos (e-CF) con Toki.

Es para:
- Dueños de colmados, carnicerías y delis que **todavía no son emisores electrónicos** y prefieren "que me lo hagan" antes que seguir la lista solos.
- Negocios con poco tiempo o poca confianza con la Oficina Virtual (OFV) de la DGII.

No es para:
- Quien ya emite e-CF con otro programa: ese caso no repite la certificación y se resuelve con "Ya emito e-CF con otro programa" (TK-221), sin costo aparte.
- Quien quiere hacerlo solo: el asistente de la app sigue incluido en la licencia ("Por tu cuenta").

| | Por tu cuenta | Con Toki (este servicio) |
|---|---|---|
| Quién opera el asistente de la app | El dueño | Un técnico de Toki, por sesión remota con el dueño presente |
| Pasos que piden la clave de la OFV | El dueño, guiado por la lista | El dueño teclea su clave; el técnico le dice qué marcar |
| Set de pruebas, simulación, firma de archivos | Toki, automático | Toki, automático; el técnico vigila los rechazos |
| Precio | Incluido en la licencia | [PRECIO DEL SERVICIO], pago único |

## 2. La regla: Toki nunca recibe la clave de la OFV

Toki no pide, no guarda y no ve la clave de la OFV del dueño ni la del representante. Tampoco la contraseña del certificado digital.

Cómo se cumple en la práctica:
- Todo lo que necesita la clave se hace en **dos sesiones remotas cortas**, con el dueño frente a su computadora. El dueño escribe su clave; el técnico solo indica qué pantalla abrir y qué marcar.
- Cuando el dueño va a escribir una clave, el técnico lo dice en voz alta y **pausa la vista o el control remoto** hasta que el dueño confirme que terminó.
- Si el dueño ofrece la clave por WhatsApp, correo o llamada, el técnico **no la acepta** y le pide que la cambie si ya la envió.
- La contraseña del certificado `.p12` se escribe una sola vez al importarlo en la app (TK-176) y Toki no la guarda.

Esta es la diferencia con GLECF, que recibe la clave para enviar la solicitud por el cliente (`docs/competencia-glecf.md` §4).

## 3. Qué hace Toki y qué hace el dueño

| Tarea | Toki | Dueño |
|---|---|---|
| Revisar que tenga todo lo de la lista (§5) antes de agendar | Sí, por WhatsApp o llamada | Reúne los documentos |
| Comprar el certificado digital | Explica cómo y con quién | Lo compra y hace la validación de identidad con la entidad certificadora |
| Importar el certificado en la app | Guía en la cita 1 | Escribe la contraseña del `.p12` |
| Enviar la solicitud FI-GDF-016 en la OFV | Indica qué llenar (software "adquirido a un proveedor externo", datos de Toki) | Entra con su clave y la envía |
| Delegar el rol de Firmante, si el certificado es de otra persona | Indica los pasos y firma el XML con "Firmar archivo de la DGII" | Entra con su clave y lo sube |
| Postulación, set de pruebas, aprobaciones comerciales, simulación | Genera, firma y envía todo desde la app; vigila los rechazos | Sube en el portal los archivos que Toki prepara y confirma las URL |
| Recepción de e-CF y aprobaciones de la DGII (pasos 8 a 11) | La nube de Toki responde sola | Nada |
| Declaración jurada | Firma el XML con el certificado del negocio | **La lee, la acepta y la sube él** |
| Pedir secuencias e-NCF de producción y registrar las URL en "Mantenimiento Directorio FE" | Indica los valores exactos y carga los rangos en la app | Entra con su clave y los pide o registra |
| Primera venta real | La revisa con el dueño | Vende |

## 4. Paso a paso con tiempos estimados

Los tiempos de Toki son estimados de trabajo. Los marcados **(DGII)** dependen de la DGII o de la entidad certificadora y Toki no los controla ni los promete.

| # | Etapa | Quién | Tiempo estimado |
|---|---|---|---|
| 1 | **Solicitud del servicio:** el dueño pide el acompañamiento (WhatsApp o el botón "Solicitar acompañamiento" de la app, TK-233) | Dueño | — |
| 2 | **Revisión previa:** el técnico repasa la lista de §5 con el dueño y confirma que la app ya está instalada y con ventas | Toki + dueño | 15–20 min por llamada o WhatsApp |
| 3 | **Certificado digital:** el dueño lo compra a Avansi (Viafirma) o a Digifirma y valida su identidad | Dueño | **(DGII / entidad certificadora)** |
| 4 | **Cita 1 (remota):** importar el `.p12` (TK-176); enviar la FI-GDF-016 desde la OFV del dueño; delegar el rol de Firmante si hace falta; pruebas libres en TesteCF | Toki + dueño | 30–60 min |
| 5 | **Espera de la DGII:** la DGII responde por el buzón de la OFV con el acceso al portal de certificación | DGII | **(DGII)** |
| 6 | **Pruebas en CerteCF:** postulación, set de pruebas, aprobaciones comerciales, simulación, PDF de la representación impresa, comunicación con el receptor (pasos 1 a 11 de `docs/ecf-propio.md` §8). Lo que necesita la OFV o el portal lo hace el dueño, por WhatsApp o en una sesión corta | Toki (+ dueño para subir archivos) | Meta de Toki: pasos 2 a 11 en una tarde; cada revisión de la DGII entre pasos **(DGII)** |
| 7 | **Cita 2 (remota):** URL de producción, declaración jurada (la acepta el dueño), pedir secuencias e-NCF, cargarlas en la app y cambiar el ambiente a producción (`eCF`) | Toki + dueño | 30–45 min, después de que la DGII habilite el menú FE **(DGII)** |
| 8 | **Primera venta real:** el técnico la revisa con el dueño (aceptada, recibo con QR); quedan activos los avisos de rechazos | Toki + dueño | 10–15 min |

No se promete una fecha total. Lo que sí se promete: que Toki responde en el canal de soporte (§8) y que corre su parte en cuanto la DGII abre cada paso.

## 5. Lista de lo que prepara el dueño

Antes de la cita 1:
- [ ] **RNC activo y al día** con la DGII (sin declaraciones pendientes).
- [ ] **Acceso a la OFV** de la empresa y, si aplica, del representante: usuario y clave que **solo el dueño conoce**. Probar que entra antes de la cita.
- [ ] **Alta de NCF** (autorización para emitir comprobantes fiscales).
- [ ] **Representante** vinculado al RNC que será el Usuario Administrador e-CF.
- [ ] **Certificado digital** de persona física para procedimientos tributarios, a nombre de ese representante, comprado a **Avansi (Viafirma)** o a la **Cámara de Comercio y Producción de Santo Domingo (Digifirma)**. Pedirlo como archivo `.p12` o `.pfx` con su contraseña (no en token USB ni en la nube del emisor). Precio y formato por confirmar (`docs/ecf-propio.md` §4.2 y §11 #4).
- [ ] La **PC principal** de la tienda con Toki instalado, encendida y con internet durante las citas.
- [ ] Una hora libre para cada cita, sin atender el mostrador.

## 6. Riesgos y qué pasa si la DGII rechaza

| Riesgo | Qué hacemos |
|---|---|
| La DGII rechaza la solicitud FI-GDF-016 (RNC con pendientes, datos que no cuadran) | El técnico lee el motivo con el dueño; el dueño lo corrige con la DGII o su contador; se vuelve a enviar en una sesión corta sin costo adicional |
| Un e-CF del set de pruebas es rechazado (el set se reinicia en la DGII) | Toki corrige la causa, vuelve a cargar el set y lo reenvía. Es trabajo de Toki y está incluido |
| La DGII tarda en responder o en habilitar el siguiente paso | El técnico lo ve en la consola (§9) y avisa al dueño; no hay nada que acelerar desde Toki |
| El certificado no llega como `.p12` o es de otra persona o de otro RNC | La app lo rechaza al importarlo (`Fiscal.CertificateWrongRnc`); el dueño pide uno correcto a la entidad certificadora |
| El dueño no se presenta a una cita | Se reprograma una vez sin costo |
| Algo que la DGII aún no ha confirmado sobre el proceso (preguntas abiertas en `docs/ecf-propio.md` §11) | Se resuelve con la DGII durante la certificación de la tienda piloto, antes de vender el servicio |
| La DGII rechaza la certificación de forma definitiva por causas del negocio | Toki entrega el resumen de lo hecho y del motivo; las condiciones de devolución las define el dueño de Toki junto con el precio (TK-236) |

## 7. Precio

[PRECIO DEL SERVICIO], pago único por negocio (RNC). Incluye las dos citas, el trabajo de las pruebas, los reenvíos por rechazos y la revisión de la primera venta. No incluye el certificado digital, que el dueño compra directamente a la entidad certificadora. El precio se publica en `/certificacion` (`config.ts` → `prices`) cuando el dueño de Toki lo decida.

## 8. Canal de soporte

- **WhatsApp de Toki** (el mismo número del sitio, `config.ts` → `whatsapp`, todavía por confirmar) para agendar, avisos de avance y dudas.
- **Correo** con los avisos automáticos (TK-222).
- Las citas, por la herramienta de sesión remota que define el procedimiento interno: `docs/guia-tecnico-certificacion.md` (TK-232).

Horario y tiempo de respuesta: por definir con el dueño de Toki; no se publican hasta tener personal asignado.

## 9. Cómo se sigue el avance

- **Consola de plataforma** (TK-225): Plataforma → Métricas → "Certificación DGII" muestra el paso actual de cada tienda y desde cuándo; las detenidas 14 días o más en un paso salen primero. Cada vez que el técnico o el dueño marca un paso en el asistente de la app, se actualiza. La etapa "Certificado DGII" cuenta en el embudo.
- **Avisos fuera de la app** (TK-222): el dueño recibe por correo el avance de la certificación, los rechazos de la DGII y el certificado por vencer.
- **Solicitudes del servicio** (TK-233 y TK-234): quién pidió acompañamiento, fecha de cada cita y en qué estado está.

## 10. Antes de ofrecerlo

Según `docs/competencia-glecf.md` §7: TK-141 (TesteCF real), TK-148 (PDF de la representación impresa), TK-152 (receptor de la nube probado en CerteCF), TK-155 (asistente con CerteCF real) y TK-158 (pruebas contra TesteCF) hechos, y **la tienda piloto certificada con este mismo proceso** (TK-237).

## 11. Tickets para entregarlo (M15)

| Ticket | Qué |
|---|---|
| TK-232 | Procedimiento interno del técnico: guion de las dos citas, herramienta de sesión remota y cómo pausar la vista mientras el dueño teclea la clave, qué mirar en la consola, qué hacer ante cada rechazo, mensajes de WhatsApp listos |
| TK-233 | Botón "Solicitar acompañamiento" en "Comprobantes electrónicos" que avisa al personal de Toki |
| TK-234 | Agenda de las dos citas y estado de cada solicitud en la consola de plataforma |
| TK-235 | Lista de preparación del dueño (§5) en `/certificacion` y en el correo de confirmación de la cita |
| TK-236 | Precio, condiciones (reprogramación, rechazo definitivo) y cobro del servicio (decisión del dueño) |
| TK-237 | Certificar la tienda piloto siguiendo este plan de punta a punta y ajustar tiempos y guion |
