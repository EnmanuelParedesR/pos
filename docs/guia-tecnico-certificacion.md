# Guía del técnico: acompañamiento en la certificación e-CF (TK-232)

> Procedimiento interno para el personal de Abastalia que da el servicio de `docs/servicio-certificacion.md` (TK-219). No se publica. Pasos de la DGII: `docs/ecf-propio.md` §8. Última revisión: 16 de octubre de 2026; se ajusta con la tienda piloto (TK-237).

## 0. La regla que no se rompe

**Abastalia nunca pide, recibe, ve ni guarda la clave de la Oficina Virtual (OFV)** del dueño ni la del representante, ni la contraseña del certificado `.p12`.

- Si el dueño la escribe en el chat, la dice en voz alta o la manda por WhatsApp o correo: no la use, borre el mensaje en su lado, dígale que la cambie hoy y anótelo en la solicitud ("clave expuesta, se pidió cambio").
- Nunca deje guardado un acceso remoto a la PC del dueño (sin acceso desatendido, sin contraseñas fijas de la herramienta).
- Nunca entre a la OFV ni al portal de certificación de la DGII "para adelantar": esos pasos los hace el dueño, en su navegador.

## 1. Antes de agendar

1. La solicitud llega por correo ("Abastalia · Solicitud de acompañamiento") y aparece en **Plataforma → Acompañamiento** con estado **Nueva**.
2. Escriba al dueño por WhatsApp (mensaje 1, §7) en el mismo día hábil.
3. En una llamada de 15–20 minutos repase la lista de preparación (`docs/servicio-certificacion.md` §5, la misma de `/certificacion#preparacion`):
   - RNC activo y al día, alta de NCF, representante vinculado al RNC.
   - El dueño **prueba que entra a la OFV** (él solo; usted no ve la pantalla).
   - Certificado digital comprado a Avansi (Viafirma) o Digifirma, como archivo `.p12`/`.pfx`. Si todavía no lo tiene, mande el mensaje 2 y no agende hasta que lo tenga.
   - Abastalia instalado en la PC principal, con ventas y **conectado a la nube** (Configuración → Nube).
4. En **Plataforma → Acompañamiento → Editar**: estado **Agendada**, fecha de la cita 1 (hora de la tienda) y una nota. Al guardar, el dueño recibe el correo de confirmación con la lista de preparación. Confirme también por WhatsApp (mensaje 3).

## 2. La herramienta de sesión remota

Mientras no se elija una definitiva, la herramienta debe cumplir todo esto:

- La sesión la **inicia el dueño** con un código de un solo uso; se cierra al terminar y no queda acceso guardado.
- El dueño puede **pausar o detener** lo que se comparte en cualquier momento.
- Solo vista, salvo que el dueño acepte el control para un paso concreto de Abastalia (no de la OFV).

Candidata: **Asistencia rápida** de Windows (viene con el sistema, el dueño comparte con un código y puede pausar). Otra herramienta solo si cumple lo mismo.

**Cuando el dueño va a escribir una clave** (OFV o contraseña del `.p12`):

1. Dígalo en voz alta: "Ahora va a escribir su clave. Voy a dejar de ver su pantalla".
2. Pídale que pause lo compartido (o pause usted la vista y suelte el control). Confirme que su pantalla está en negro o en pausa.
3. Espere a que el dueño diga "listo" y lo reanude él.
4. Si la clave quedó visible en algún momento, deténgase y pídale que la cambie al terminar la cita.

## 3. Guion de la cita 1 (30–60 min)

Objetivo: certificado importado, solicitud FI-GDF-016 enviada y pruebas libres hechas.

1. **Abrir** (2 min): presentarse, recordar la regla de la clave, confirmar que hay una hora sin mostrador.
2. **Revisar Abastalia** (5 min): Escuadra Admin → Comprobantes electrónicos. La tienda está en **Simulado** o **TesteCF**; la tarjeta "Certificación con la DGII" aparece; la nube está conectada.
3. **Importar el certificado** (5–10 min, TK-176): Configuración → Comprobantes → importar el `.p12`. **El dueño escribe la contraseña con la vista en pausa.** Si la app responde que el certificado es de otro RNC (`Fiscal.CertificateWrongRnc`), pare: el dueño pide uno correcto a la entidad certificadora (mensaje 6).
4. **Solicitud FI-GDF-016** (10–20 min): el dueño entra a la OFV (vista en pausa al escribir la clave). Usted le dicta qué llenar: software **"adquirido a un proveedor externo"**, con los datos de Abastalia como proveedor. El dueño la envía. Marque el paso 1 en el asistente.
5. **Delegar el Firmante** (si el certificado es de otra persona): el dueño descarga el XML de delegación, Abastalia lo firma con "Firmar archivo de la DGII" y el dueño lo sube.
6. **Pruebas libres en TesteCF** (10 min): cambiar el ambiente a Pre-certificación (TesteCF), una venta de cada tipo que use el negocio (31, 32, 34) y ver que salgan **Aceptado**. Marque el paso 2.
7. **Cerrar** (3 min): explicar que la DGII responde por el buzón de la OFV (días, no horas) y que el dueño nos avisa cuando llegue el acceso al portal (mensaje 4). En la consola: estado **Sesión 1 hecha** y una nota con lo hecho.

## 4. Entre citas: pruebas en CerteCF

Cuando el dueño avisa que llegó el acceso al portal, el resto (pasos 3 a 11 de `docs/ecf-propio.md` §8) se hace por WhatsApp o en sesiones cortas; meta: una tarde.

| Paso del portal | Quién | Qué hace el técnico |
|---|---|---|
| Postulación | Dueño + Abastalia | Le pasa las URL de la tienda (Compras → Recibidos (e-CF) → Receptor en la nube); el dueño descarga el XML, la app lo firma, el dueño lo sube |
| Set de pruebas | Abastalia | El dueño le manda el Excel del portal; se sube en el asistente ("Set de pruebas") y se toca "Enviar y consultar" hasta que todo esté Aceptado |
| Aprobaciones comerciales | Abastalia | Igual, con el Excel de aprobaciones |
| Simulación | Abastalia | Ambiente en CerteCF, ventas normales en la caja; vigilar aceptados/rechazados en la tarjeta |
| PDF de la representación impresa | Dueño | Se los pasa para subir al portal |
| URL de prueba y comunicación | Dueño / nube | El dueño confirma las URL; la nube de Abastalia responde sola a la DGII |

Cada vez que se termina un paso, márquelo en el asistente de la tienda: así la consola muestra el avance y el dueño recibe el aviso por correo (TK-222).

## 5. Guion de la cita 2 (30–45 min)

Se agenda cuando la DGII habilita el menú FE en la OFV (después de la verificación del RNC). Al guardar la fecha en la consola, el dueño recibe la confirmación.

1. **URL de producción**: el dueño las registra en el portal (o en "Mantenimiento Directorio FE"). Copiar cada dirección **completa**, con `https://`; el error más común es escribir solo el dominio.
2. **Declaración jurada**: el dueño la descarga, **la lee y la acepta él**. La app la firma ("Firmar archivo de la DGII") y el dueño la sube. Usted no la acepta por él.
3. **Secuencias e-NCF**: el dueño las pide en la OFV (vista en pausa al entrar). Usted le dicta los tipos y cantidades; se cargan en Configuración → Comprobantes.
4. **Ambiente de producción** (`eCF`): cambiarlo en la app y revisar que la pantalla "Comprobantes electrónicos" diga Producción.
5. **Primera venta real** (10–15 min): una venta normal; debe salir **Aceptado** y el recibo con QR. Revisar que los avisos de rechazo estén activos en el panel (Avisos).
6. **Cerrar**: estado **Certificada** en la consola (el asistente marca el último paso; la consola de certificación lo cuenta en el embudo). Mensaje 7.

Si algo de la DGII no llega a tiempo, la cita se divide: estado **Sesión 2 hecha** cuando se haya hecho todo menos la primera venta, y una cita corta después.

## 6. Qué mirar en la consola

- **Plataforma → Acompañamiento** (TK-234): estado de cada solicitud, contacto, horario preferido, fechas de las citas y notas del equipo (el dueño no las ve). Las abiertas salen primero. Cada consulta y cada cambio queda en la auditoría de la plataforma.
- **Plataforma → Métricas → "Certificación DGII"** (TK-225): el paso actual de cada tienda y cuántos días lleva en él. **Detenida** = 14 días o más en un paso: escriba al dueño (mensaje 5) y anote el motivo.
- **Plataforma → la tienda**: la caja conectada (último envío), la licencia y "Facturación electrónica: por la nube (DGII)" o "simulada".
- Si la tienda no envía datos hace más de un día, el avance no se actualiza: pídale al dueño que revise el internet antes de suponer que está detenida.

## 7. Qué hacer ante cada rechazo de la DGII

Detalle técnico de cada caso en `docs/ecf-propio.md` (§2.4 envío y resultado, §8 certificación, §9 correcciones, §11 riesgos y preguntas abiertas).

| Rechazo | Qué se ve | Qué hace el técnico |
|---|---|---|
| **FI-GDF-016 rechazada** (RNC con pendientes, datos que no cuadran) | Mensaje en el buzón de la OFV | Leerlo con el dueño en una sesión corta. El dueño lo corrige con la DGII o su contador. Reenviar sin costo |
| **e-CF del set de pruebas rechazado** | Fila en rojo en "Set de pruebas", con el código y el mensaje | El set se reinicia en la DGII. Anote el mensaje, corrija la causa (avise a desarrollo si es del XML), pida al dueño el Excel nuevo y súbalo otra vez. Trabajo de Abastalia, incluido |
| **Rechazo con `secuenciaUtilizada = false`** (certificado o firma inválida, XML que no cumple el XSD, firmante no delegado) | En "Rechazados por la DGII": "La DGII no consumió el número" | El número se puede volver a usar. Corregir la causa (delegación del firmante, certificado) y reemplazar |
| **Rechazo de una venta en la simulación o en producción** | "Rechazados por la DGII" con el motivo | Tocar **Reemplazar**: se emite un e-CF nuevo con los mismos datos. Un rechazado no se corrige con nota de crédito |
| **Certificado de otro RNC** (`Fiscal.CertificateWrongRnc`) | La app no lo importa | El dueño pide uno a nombre del representante de ese RNC (mensaje 6) |
| **Certificado de una entidad no reconocida** (`Fe.CertificateUntrusted`, receptor en la nube) | El receptor no se activa | Confirmar que es de Avansi o Digifirma; si es de otra entidad autorizada por INDOTEL, avisar a desarrollo (`docs/ecf-propio.md` §10.12) |
| **URL mal registradas** | La DGII no puede entregar e-CF ni aprobaciones (pasos 8 a 11 no avanzan) | Revisar con el dueño que cada URL esté completa en el portal y en "Mantenimiento Directorio FE" |
| **Aprobación comercial rechazada** | En la prueba de aprobaciones | Revisar el Excel y la fila; reenviar desde el asistente |
| **La DGII tarda** | Sin respuesta en el buzón o en el portal | Nada que acelerar desde Abastalia. Avisar al dueño (mensaje 5) y anotar la fecha en la consola |
| **Rechazo definitivo por causas del negocio** | Respuesta de la DGII | Entregar al dueño un resumen de lo hecho y del motivo; condiciones según TK-236. Estado **Cancelada** con la nota |

## 8. Mensajes de WhatsApp listos

Cambie lo que está entre corchetes. Trato de usted, sin abreviaturas.

**1. Primer contacto**
> Buenos días, [NOMBRE]. Le escribe [TÉCNICO], de Abastalia. Recibimos su solicitud de acompañamiento para la certificación de facturación electrónica de [TIENDA]. ¿Le queda bien una llamada de 15 minutos [DÍA] a las [HORA] para revisar lo que necesita? Abastalia nunca le va a pedir su clave de la Oficina Virtual de la DGII.

**2. Falta el certificado digital**
> [NOMBRE], para la primera cita hace falta el certificado digital del representante del negocio. Lo vende Avansi (Viafirma) o la Cámara de Comercio y Producción de Santo Domingo (Digifirma). Pídalo como archivo .p12 o .pfx, con su contraseña. Cuando lo tenga, me avisa y agendamos.

**3. Confirmación de la cita**
> Queda su cita [1/2] para el [FECHA] a las [HORA]. Le llegó un correo con lo que debe tener listo. Necesitamos la PC principal de la tienda encendida y con internet, y una hora libre sin atender el mostrador. Cuando haga falta su clave, usted la escribe y yo dejo de ver su pantalla.

**4. Después de la cita 1**
> Gracias, [NOMBRE]. La solicitud a la DGII quedó enviada. La DGII responde en el buzón de su Oficina Virtual; puede tardar unos días. Cuando le llegue el acceso al portal de certificación, me avisa por aquí y seguimos.

**5. Seguimiento (detenida o esperando a la DGII)**
> [NOMBRE], le escribo para saber cómo va la certificación de [TIENDA]. ¿Le llegó alguna respuesta de la DGII en el buzón de la Oficina Virtual? Si tiene un mensaje de la DGII, me lo puede reenviar sin la clave.

**6. Certificado que no sirve**
> [NOMBRE], el certificado que compró no corresponde al RNC del negocio (o no es del representante). Hay que pedir uno nuevo a la entidad certificadora a nombre de [REPRESENTANTE] para el RNC [RNC]. Cuando lo tenga, lo importamos en una sesión corta.

**7. Certificación completa**
> ¡Felicidades, [NOMBRE]! [TIENDA] ya factura con comprobantes electrónicos aceptados por la DGII. Si la DGII rechaza alguno, le llega un aviso por correo y lo reemplaza desde Comprobantes electrónicos. Cualquier duda, escríbanos por aquí.

**8. Si el dueño manda su clave**
> [NOMBRE], por su seguridad no podemos recibir su clave de la Oficina Virtual. Le pido que la cambie hoy mismo desde la OFV. En las citas usted la escribe y yo no veo su pantalla en ese momento.
