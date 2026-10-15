# Balanzas (Toki.Hardware.Scales)

Balanzas en un puerto COM (RS-232, o USB con puerto COM virtual). Se configuran en la caja: Configuración → Equipos de la caja (puerto, protocolo, velocidad). El análisis de las tramas está en `ScaleProtocols` (puro, con pruebas de bytes en `tests/Toki.Hardware.Tests`); `SerialScale` solo abre el puerto, pide y lee.

| Protocolo | Balanzas | Puerto | Cómo se lee |
|---|---|---|---|
| `Cas` | CAS PD-II, ER Plus / ER Jr (modo ECR) y compatibles | 9600 8N1 | La caja envía ENQ (05), la balanza responde ACK (06); la caja envía DC1 (11) y la balanza responde `SOH STX STA SIGN W×6 UNIT×2 BCC ETX EOT`. STA: `S` estable, `U` en movimiento. BCC: XOR de STA a UNIT. |
| `Toledo8217` | Mettler Toledo 8217, y las que emulan "Toledo" (Magellan, muchas balanzas de mostrador) | 9600 7E1 | La caja envía `W`; la balanza responde `STX peso CR` (`01.250`, en libras) o, si no puede pesar, `STX ? estado CR`. Bits del estado: 0 en movimiento, 1 sobre capacidad, 2 bajo cero, 3 fuera del rango de cero. Sin punto decimal, los dígitos son centésimas. |
| `Continuous` | Indicadores que envían el peso sin pedirlo (formato A&D / CAS continuo: `ST,GS,+  1.250 lb`) | 9600 8N1 | Una línea por lectura, terminada en CR o LF. `ST` estable, `US` o `M` en movimiento, `OL` sobrecarga. Sin palabra de estado, el peso es estable cuando llega igual tres veces seguidas. |

Siempre se devuelve el peso en libras: kg, g y oz se convierten y se redondea a la milésima. Un peso negativo nunca es estable. Si la balanza no contesta en 0.7 s o la trama llega dañada (BCC malo, sin terminador), la lectura es `Disconnected` y el puerto se reabre en la siguiente.

## Fuentes

- Protocolo ECR de CAS (ENQ/ACK/DC1, trama SOH…EOT con BCC por XOR): manuales de interfaz de balanzas de mostrador que lo implementan, por ejemplo el protocolo de Novitus (https://novitus.pl/file/get/821), que describe el mismo intercambio y los campos STA/SIGN/UNIT/BCC; CAS lo documenta en el manual de servicio de la PD-II y la ER Plus ("ECR protocol").
- Mettler Toledo 8217: la configuración de referencia (9600 baudios, 7 bits, paridad par, comando `W`, respuesta `STX peso CR`, estado `STX ? estado CR`) es la del controlador de balanzas del IoT Box de Odoo (`addons/hw_drivers/iot_handlers/drivers/SerialScaleDriver.py`, https://github.com/odoo/odoo); el significado de los bits del byte de estado es el de la familia 8213/8217 que también describe la especificación NCI/Weigh-Tronix (https://cdn.chipkin.com/assets/uploads/imports/resources/Weigh-Tronix%20NCI%20Interface%20Specification.pdf) y la guía de referencia de los Datalogic Magellan 9300i/9400i (modo de balanza "Mettler Toledo 8217").
- Envío continuo: formato de salida de indicadores A&D (`ST,GS,+0001.250 lb`) y el modo continuo de CAS.

## Falta probar con equipo real

Las tramas salen de los manuales, no de una balanza conectada. Antes del piloto, en la tienda: conectar cada modelo, usar "Leer balanza" en Equipos de la caja y confirmar el protocolo, la velocidad y los bits de estado (sobre todo el de sobrecarga de Toledo y el texto de sobrecarga de CAS, que cambia según el modelo).
