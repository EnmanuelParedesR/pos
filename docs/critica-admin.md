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
