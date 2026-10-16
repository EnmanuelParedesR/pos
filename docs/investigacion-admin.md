# Investigación de usuarios: administración de Abastalia (dueño y supervisor)

Ticket: TK-171. Fecha: 15 de octubre de 2026. Alcance: la administración en la PC de la tienda (Blazor) y el panel del dueño en la nube (React, pensado primero para el celular).

## 1. Síntesis preliminar (de escritorio)

Fuentes: el sistema viejo y sus dolores (`arquitectura-v1.md`), las decisiones del producto (`funcionalidades-v1.md`, canvas), la crítica de la caja (`critica-diseno.md`) y la forma de trabajar de una carnicería o colmado dominicano. Todavía no se ha hablado con dueños reales. Cada hallazgo de abajo es una **hipótesis** que hay que validar (sección 3).

### Quiénes son

| Persona | Contexto | Lo que más mira |
|---|---|---|
| **Dueño de carnicería o colmado** (45–60 años, una o dos tiendas) | Revisa en el celular entre una cosa y otra: en la mañana, al mediodía y de noche. Lee rápido y con poca paciencia. Vive pendiente del efectivo. | ¿Cuánto vendí? ¿La caja cuadró? ¿Quién me debe? ¿Qué se está acabando? |
| **Supervisor o encargado** (25–40 años) | Trabaja en la PC de la tienda, con el mouse y el teclado. Autoriza cosas, recibe mercancía y hace los conteos. | Cierres, compras, inventario, precios |
| **Contador externo** (una vez al mes) | En una computadora. Quiere archivos, no pantallas. | 606/607, ITBIS y comprobantes |

### Trabajos por hacer (JTBD)

1. *"Cuando abro el teléfono, quiero saber en 3 segundos si el día va bien, para no tener que llamar a la tienda."* → Una cifra principal comparada con lo normal ("+12 % vs. un jueves normal"), no seis tarjetas iguales.
2. *"Cuando cierra un turno, quiero saber si faltó dinero y quién era la cajera, para actuar hoy mismo."* → Las alertas van primero, con nombre de la persona y monto en rojo o ámbar.
3. *"Cuando se acaba la carne que más se vende, quiero enterarme antes de que el cliente la pida."* → Existencias bajas de los productos que más rotan.
4. *"Quiero saber cuánto me deben fiado y quién se está pasando del límite."* → Cuentas por cobrar con antigüedad.
5. *"Quiero estar tranquilo con la DGII."* → Un indicador simple ("Comprobantes al día · quedan 312") en vez de una tabla técnica.
6. *"Quiero cambiar un precio rápido cuando sube la res."* → Una acción directa desde la lista, sin formularios largos.
7. *"Quiero preguntarle al asistente como le pregunto a mi encargado."* → El asistente siempre a mano, no escondido en otra sección.

### Temas que salen del análisis

- **La excepción importa más que el promedio:** el dueño no quiere leer tablas, quiere saber qué está raro. Hoy el Resumen trata todo igual.
- **Comparar da sentido:** RD$ 597.75 no dice nada si no se compara con ayer o con el mismo día de la semana pasada.
- **Personas, no ids:** "Cajero 1a2b" no le sirve a nadie. El dueño piensa en "Yudelka".
- **Confianza:** las cifras tienen que ver claramente de qué hora son y si la tienda está conectada.
- **Lo hecho con prisa:** acciones en la parte de abajo del celular, al alcance del pulgar, y números grandes.
- **Estética:** hoy se ve como una plantilla de administración genérica, porque todo es una tarjeta blanca con borde gris y el mismo peso visual. El dueño compara con las apps del banco y con WhatsApp: espera algo moderno, cálido y claro.

### Recomendaciones para el rediseño (priorizadas por impacto y esfuerzo)

| # | Recomendación | Impacto | Esfuerzo |
|---|---|---|---|
| 1 | Resumen jerárquico: una cifra héroe con su comparación, "Para revisar" arriba y una gráfica con el día anterior de referencia | Alto | Medio |
| 2 | Nueva identidad visual para la administración: menos bordes, superficies con tinte, jerarquía tipográfica real y color con significado | Alto | Medio |
| 3 | Navegación: barra lateral compacta con íconos en la PC y barra inferior de 4 o 5 destinos en el celular | Alto | Bajo |
| 4 | Asistente siempre visible (barra "Pregúntale a Talia…") | Medio | Bajo |
| 5 | Nombres de cajeros en la nube (TK-165) | Alto | Medio (backend) |
| 6 | Cifras en lenguaje humano ("Faltaron RD$ 150 en la Caja 2 · Yudelka · 6:05 p. m.") | Alto | Bajo |

## 2. Lo que no cambia

La caja sigue su propio sistema (alto contraste, teclado primero, 72 px para el dinero). La administración es otro contexto: se revisa más que se opera, así que puede ser más rica y más visual, pero debe usar los mismos tokens base y la misma marca.

## 3. Plan para validar con dueños reales

- **Objetivo:** confirmar los JTBD 1–7 y el orden del Resumen.
- **Método:**
  - 6 entrevistas de 45 min con dueños (4 carnicerías y 2 colmados, de Santiago y Santo Domingo). La guía sigue el orden calentamiento → contexto → profundidad → reacción a un prototipo → cierre.
  - Una prueba de usabilidad con 5 tareas en el celular:
    1. ¿Cuánto vendí hoy y es bueno?
    2. ¿Qué caja tuvo faltante?
    3. ¿Quién me debe más?
    4. Generar un código para conectar una caja.
    5. Preguntarle al asistente por lo más vendido.
- **Preguntas clave:**
  - "Cuénteme lo último que revisó del negocio desde el celular."
  - "¿Cómo se enteró la última vez que faltó dinero?"
  - "¿Qué número mira primero?"
  - "Muéstreme cómo lleva el fiado hoy."
- **Éxito:** 5 de 6 contestan la tarea 1 en menos de 10 segundos y sin ayuda.
- **Cuándo:** con el piloto (antes de TK-160). Se registra como TK-172.
