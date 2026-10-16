# Guía de contingencia de comprobantes electrónicos (para el dueño)

> TK-156. Basada en el Decreto 587-24 (arts. 40–43) y en `docs/ecf-propio.md` §6. Revísela con su contador; lo que manda es la norma de la DGII.
> En Abastalia: **Administración → Comprobantes electrónicos**, tarjetas "Salud de la operación" y "Contingencia (serie B)".

## 1. ¿Qué le pasa a la tienda?

| Situación | ¿Puede vender con e-CF? | Qué hacer |
|---|---|---|
| **Sin internet** o la **DGII no responde** | Sí | Nada. Siga vendiendo. Cada factura se firma en la PC principal y queda en la cola; sale sola al volver la conexión. El recibo lleva la leyenda de contingencia. |
| La **hora de la PC principal** está adelantada o atrasada **más de 2 minutos** frente a la de la DGII | Sí (se firman después) | Corrija la hora de Windows (Configuración → Hora e idioma → Fecha y hora → Sincronizar ahora). Mientras tanto siga vendiendo: la DGII rechaza lo firmado con la hora mal, así que cada factura queda guardada **sin firmar** (el recibo sale sin código de seguridad ni QR y con la leyenda de contingencia). En cuanto la hora está bien, Abastalia las firma y las envía solas, y al reimprimir el recibo ya trae el código. La caja y "Salud de la operación" lo avisan. No es "imposibilidad de emitir": no declare la contingencia por esto. |
| **Sin certificado digital**, certificado **vencido** o la **PC principal dañada** | No | Es "imposibilidad de emitir": declare la contingencia (sección 3). |
| La DGII lleva **más de 15 días hábiles** caída | Sí (se guardan) | La OFV habilita reportes alternos: llame a la DGII. |

**Plazo de envío:** 72 horas desde la firma de cada comprobante. Abastalia avisa en el Resumen a las 24 horas (ámbar) y a las 60 horas (rojo).

## 2. Para evitar la contingencia

- Renueve el certificado **antes** de que venza. Abastalia avisa 30 días antes en el Resumen y en la caja.
- Tenga cargado un **rango de comprobantes serie B** (consumo B02 y, si vende a empresas, crédito fiscal B01). Se piden en la OFV y se cargan en "Contingencia (serie B)". Abastalia no los usa mientras pueda firmar.
- Pulse "Consultar a la DGII" si duda de si el problema es su internet o la DGII: "La DGII no responde" es de la DGII (intermitencia o mantenimiento); "No se llega a la DGII" es casi siempre el internet de la tienda. La DGII tiene un servicio de estatus y ventanas de mantenimiento, pero solo para programas con una llave que todavía no entrega; si duda, llame (sección 6).
- Deje la hora de Windows en automático ("Establecer la hora automáticamente"). Con la hora atrasada más de 5 minutos respecto a lo último guardado, Abastalia no guarda nada (protege el registro de auditoría); con más de 2 minutos de diferencia con la DGII, deja de firmar (ver arriba).

## 3. Si no puede emitir e-CF

1. Entre a la **Oficina Virtual (OFV)** de la DGII y **declare la contingencia** (total o parcial), con el motivo.
2. En Abastalia, "Contingencia (serie B)": escriba el mismo motivo y pulse **Activar contingencia** (pide un segundo toque). Abastalia solo la deja activar si de verdad no puede firmar.
3. La caja muestra "Contingencia: se facturan comprobantes serie B en papel" y cada venta toma el siguiente NCF B. El recibo dice "NCF" y no lleva código de seguridad ni QR.
4. Resuelva el problema (importe el certificado nuevo en Configuración → Comprobantes, o repare la PC). En cuanto Abastalia puede firmar, **vuelve solo a los e-CF**, aunque la contingencia siga activa.
5. La contingencia dura como **máximo 15 días calendario**. Abastalia cuenta los días y avisa en rojo si se pasa.

## 4. Al salir de la contingencia

1. Declare la **salida** en la OFV.
2. En Abastalia pulse **Terminar contingencia**.
3. Pulse **Regularizar**: por cada comprobante serie B, Abastalia emite un e-CF del mismo tipo y con los mismos datos que lo **reemplaza** (código de modificación 4, con referencia al NCF B). Ese e-CF va **solo a la DGII**; el cliente se queda con su comprobante B.
4. Tiene **30 días calendario** desde la salida. Abastalia avisa en ámbar y, pasado el plazo, en rojo.

## 5. Plan B: si la certificación no está lista el 15/11/2026

Decisión del dueño de Abastalia (D16, `docs/preguntas-abiertas.md`): si su negocio está obligado a emitir e-CF desde el **15 de noviembre de 2026** (pequeños y micro) y la certificación con Abastalia todavía no termina, use el **Facturador Gratuito de la DGII** mientras tanto. Sirve para **poco volumen** (pocas facturas al día, por ejemplo las de crédito fiscal); no reemplaza la caja.

1. **Confirme que le aplica.** Llame a la DGII (809-689-3444, opción 4) o pregunte a su contador si su negocio puede usar el Facturador Gratuito y con qué límites. *(Verificar con la DGII: requisitos y tope de facturas.)*
2. **Pídalo en la OFV.** Entre a la Oficina Virtual con su clave y solicite el Facturador Gratuito. La DGII entrega un certificado digital para usarlo **solo ahí** (no sirve en Abastalia).
3. **Siga vendiendo en Abastalia.** La caja, el inventario y los cierres siguen igual. Para las facturas que deben ser e-CF (por ejemplo, la de crédito fiscal que pide un cliente), emítalas en el Facturador Gratuito con los mismos datos de la venta.
4. **Anote cada una.** Guarde el e-NCF del Facturador Gratuito junto al número de la venta de Abastalia (en la nota de la venta o en una hoja), para cuadrar el 607 con su contador. Abastalia no recibe esas facturas.
5. **Siga con la certificación** en Comprobantes electrónicos → "Certificación con la DGII" ("¿Qué hago ahora?" dice el paso siguiente).
6. **Al quedar autorizado,** cargue las secuencias e-NCF de producción en Abastalia, cambie el ambiente a Producción (eCF) y deje de usar el Facturador Gratuito. Avise a su contador desde qué fecha factura con Abastalia.

## 6. Contactos

- DGII, facturación electrónica: 809-689-3444, opción 4 · facturacionelectronica@dgii.gov.do
- Soporte de Abastalia: el de su contrato.

## Pendiente de confirmar con la DGII

Las preguntas abiertas están en `docs/ecf-propio.md` §11 (#30 a #33): si la 32 de reemplazo va completa o por resumen, cómo se reporta en el 607 la venta con NCF B regularizada y la nota de crédito sobre un NCF B.
