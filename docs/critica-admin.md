## Crítica de diseño: administración de Toki (PC de la tienda y panel en la nube)

Ticket: TK-171. Se revisó la pantalla Resumen de la administración en Blazor (captura del 15 de octubre de 2026) y el panel React, que usa los mismos tokens. Se puede rediseñar todo, incluida la paleta.

### Impresión general
Funciona, pero parece una plantilla de administración genérica: todo es una tarjeta blanca con borde gris del mismo peso, la gráfica está casi vacía y no hay nada que diga qué es importante hoy. La mayor oportunidad es pasar de "mostrar datos" a "decirle al dueño qué pasa" y darle una identidad visual propia y moderna.

### Usabilidad

| Hallazgo | Severidad | Recomendación |
|---|---|---|
| Las 4 KPI pesan lo mismo, y "Cajas abiertas" rompe la cuadrícula con una fila de una sola tarjeta | 🔴 | Una cifra héroe (vendido hoy) con su comparación contra el mismo día de la semana anterior, y luego 3 métricas secundarias en una fila |
| "Para revisar" (faltantes, comprobantes, sincronización) no aparece en el Resumen de la tienda | 🔴 | Alertas arriba, con persona, monto y hora, y un botón de acción |
| Sin comparación, RD$ 597.75 no se puede interpretar | 🔴 | Indicador de cambio (+12 % / −8 %) y la serie anterior en gris en la gráfica |
| La gráfica de 7 días muestra seis barras en 0.00 con su etiqueta; es ruido | 🟡 | Ocultar las etiquetas en cero, agregar una línea base, un tooltip y un estado vacío con texto |
| La tabla de más vendidos tiene las columnas desalineadas (CANT. y VENTA SIN ITBIS) y no muestra la parte del total | 🟡 | Lista con barra de proporción, cantidad con unidad (lb) y monto alineado a la derecha |
| La navegación es solo texto, sin íconos, y "Vender F1" compite con todo | 🟡 | Barra lateral con íconos y estado activo con tinte; "Vender" como acción secundaria arriba o al pie |
| No hay selector de fecha ni de tienda en el Resumen de la tienda | 🟢 | Control segmentado Hoy / Ayer / 7 días / Mes |

### Jerarquía visual
- **Lo primero que se ve:** cuatro números del mismo tamaño. No es lo correcto: debería ser el total del día y si va bien o mal.
- **Orden de lectura:** de izquierda a derecha y en Z, sin un punto focal. La barra azul de "Vender" se lleva la vista antes que el contenido.
- **Énfasis:** el azul solo aparece en la gráfica y en "Vender", y el color no comunica nada (ni éxito ni alerta).

### Consistencia

| Elemento | Problema | Recomendación |
|---|---|---|
| Superficies | Todas son blancas con un borde de 1 px gris; todo parece igual de importante | Superficies sin borde con elevación sutil, fondo cálido de un solo tono y radio de 12–16 px en la administración |
| Tipografía | Las etiquetas en MAYÚSCULAS de 12 px compiten con los títulos, y la cifra de 32 px se usa en todas partes | Escala clara: héroe 40–48 px, métricas 24 px, etiquetas de 13 px en tipo oración, y cifras tabulares |
| Color | Una paleta de caja (alto contraste, pensada para un local con mucha luz) aplicada a una vista de análisis | Una capa de administración: neutros cálidos, la marca como acento, semánticos suaves (fondo con tinte y texto fuerte) y una paleta para gráficas |
| Íconos | Casi no hay | Íconos de línea (estilo Lucide) en la navegación, en las alertas y en las métricas |

### Accesibilidad
- **Contraste:** el texto pasa AA. Los bordes de tarjeta (#C9CED4 sobre #EEF0F2) están por debajo de 1.5:1, pero son decorativos.
- **Áreas táctiles:** 44–48 px en la navegación, correcto. En el celular (panel) la barra inferior debe ser de 56 px con íconos y texto.
- **Lectura:** 15 px base, correcto. Las etiquetas en mayúsculas de 12 px con espaciado son más difíciles de leer para un dueño de 50 años, así que se pasan a tipo oración.

### Lo que funciona bien
- Las cifras tabulares, el formato RD$ y el logo C5 en la barra.
- La información correcta ya está disponible: ventas, ganancia, cajas abiertas y más vendidos.
- La separación entre la caja (operación) y la administración (revisión).

### Recomendaciones prioritarias
1. **Resumen que cuenta una historia.** Una cifra héroe con comparación y mini-tendencia, "Para revisar" arriba, la gráfica con la referencia de la semana pasada, y los más vendidos con barras de proporción.
2. **Capa visual "Escuadra Admin".** Neutros cálidos (fondo #F6F5F2, tinta #14181D), la marca como acento con una escala de 50 a 900, semánticos suaves, una paleta de datos de 4 colores, superficies sin borde con sombra muy sutil, radio de 14 px, íconos y una escala tipográfica real. La caja no cambia.
3. **Navegación moderna y adaptable.** Barra lateral con íconos y colapsable en la PC, barra inferior en el celular, asistente siempre visible ("Pregúntale a Toki…"), estados vacíos y de carga (*skeletons*) cuidados, y cero tablas sin estilo.

## Rondas de diseño (TK-173)

Ciclo: implementar → capturar (PC 1366×768 y celular 375×812) → criticar → corregir. Datos de prueba: 14 días de ventas en la tienda local (Caja 2, Yudelka), un cierre con faltante de RD$ 150, uno con sobrante de RD$ 25 y la costilla por debajo del mínimo.

**Antes (ronda 0).** Seis tarjetas blancas con borde gris del mismo peso, barra oscura de la caja arriba, "Vender" en azul compitiendo con todo, gráfica casi vacía y la tabla de más vendidos desalineada.

### Ronda 1: capa Escuadra Admin y nuevo Resumen
- **Qué se hizo:** capa `.esc-admin` (neutros cálidos, escala de azul 50–900, sombras suaves, radio 16), barra lateral con íconos y colapsable, barra inferior en el celular, Resumen con "Para revisar" arriba, cifra héroe con comparación y mini-tendencia, gráfica con la semana anterior en gris, medios de pago y lo más vendido con barras de proporción.
- **Impresión:** por primera vez se entiende en 3 segundos cuánto se vendió y qué está mal. Ya no parece una plantilla.
- **Hallazgos:**
  - 🔴 La escala de la gráfica saltaba de 25k a 50k: las barras ocupaban media gráfica. → Escala con pasos 1 · 1.5 · 2 · 2.5 · 3 · 4 · 5 · 6 · 8.
  - 🔴 El sobrante (RD$ 25) salía en rojo, igual que un faltante. → Rojo solo para faltantes; ámbar para sobrantes.
  - 🟡 La tarjeta de medios de pago quedaba medio vacía y el subtítulo listaba las cajas abiertas en una línea larga. → Nueva tarjeta "Cajas ahora" (persona, desde qué hora) debajo de los medios de pago; el subtítulo queda corto.
  - 🟡 Celular: el ícono de la alerta quedaba solo en una fila y el control Hoy/Ayer/7 días/Mes no ocupaba el ancho. → Alerta en cuadrícula ícono + texto + botón; acciones del encabezado a todo el ancho.
  - 🟡 Celular: las etiquetas de la gráfica se montaban ("Sáb 10 Dom 1"). → Dos renglones en pantallas angostas.
  - 🟡 Celular: Productos y Turnos se volvían una tabla con desplazamiento lateral y el precio quedaba fuera de vista. → Columnas secundarias ocultas en el celular; la categoría o la caja pasan a un segundo renglón.

### Ronda 2: Productos, Turnos, Configuración y el panel en la nube
- **Qué se hizo:** tablas con encabezado fijo, números a la derecha, fila con tinte al pasar el mouse, esqueletos de carga y estados vacíos con qué hacer; formularios con el error al lado de cada campo; Configuración con la descripción a la izquierda y el formulario a la derecha; el panel React con el mismo marco, la barra "Pregúntale a Toki…" en todas las pantallas, Resumen con comparación "a esta hora" contra el mismo día de la semana pasada, Ventas con el periodo anterior en gris y Tiendas que se refresca sola al conectar una caja (TK-170).
- **Hallazgos:**
  - 🔴 Los renglones pensados para el celular (categoría, caja) también salían en la PC por un empate de especificidad en el CSS. → Corregido.
  - 🔴 El diálogo de producto es más alto que 768 px y el botón Guardar quedaba fuera de vista. → Pie del diálogo fijo abajo.
  - 🟡 Existencias con tres decimales ("27.800 lb"): así pesa la balanza, no así lee el dueño. → "27.8 lb" en la administración.
  - 🟡 Celular: el precio se partía en dos renglones y el nombre del cajero quedaba apretado por el avatar. → Precio sin cortes, existencia debajo del precio y sin avatar en las tablas del celular.
  - 🟡 Cierre Z: "Entradas y retiros +0.00 · −0.00" no se entiende. → Dos renglones, cada uno con su nombre.
  - 🟡 Panel, Ventas: "Del vie, 9 oct al jue, 15 oct" y "vs. RD$ 0.00 antes". → "Del 9 al 15 de octubre" y "Sin ventas en los 7 días anteriores"; los días sin ventas en gris.
  - 🟢 Panel, Cuadres: la tabla dejaba un hueco blanco con un solo cierre. → Sin altura mínima.

### Ronda 3: periodos, menú contraído y casos sin comparación
- **Qué se revisó:** Resumen en Hoy, Ayer, 7 días y Mes; menú contraído; Productos y Turnos en el celular; panel (Ventas, Asistente) en el celular; que la caja (entrar, vender) siga igual.
- **Hallazgos:**
  - 🔴 Sin ventas el mes anterior, el héroe decía "vs. los mismos días del mes pasado (RD$ 0.00)" y las métricas "0 el mes pasado": ruido que parece un error. → "No hay ventas en … para comparar" y sin notas en cero.
  - 🟡 En "Mes" todas las barras salían en el azul oscuro de "hoy". → Solo el último día del periodo va oscuro.
  - 🟡 En "Mes", "Para revisar" traía un faltante de hace 9 días. → Las alertas siempre miran los últimos 7 días, sea cual sea el periodo.
  - 🟡 Celular, Turnos: la columna de la flecha le quitaba espacio al nombre. → Sin flecha en el celular (la fila entera se toca).
  - ✅ La caja no cambió: entrar con PIN, vender y cerrar siguen con su barra oscura, alto contraste y 48/72 px; la capa nueva solo aplica dentro de `.esc-admin`.
- **Veredicto:** se puede entregar. Lo que queda depende del backend (ver pendientes en `sistema-de-diseno.md`).
