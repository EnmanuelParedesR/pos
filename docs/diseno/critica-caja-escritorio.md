# Crítica de diseño: caja de escritorio (TK-281, 16 oct 2026)

Revisión hecha a 1366×768 (PC de caja típica) con datos de prueba en local: entrada, administración, abrir caja, venta y pesaje. Antes, auditoría responsive de 13 pantallas a 375, 768, 1024, 1366 y 1920 px (desborde horizontal, elementos fuera de la pantalla y controles encimados).

## Responsive

| Hallazgo | Arreglo |
|---|---|
| Configuración a 768 px con la barra lateral abierta: la tarjeta seguía en dos columnas y a los campos les quedaban 101 px (scroll horizontal de 28 px) | La tarjeta pasa a una columna según su propio ancho (`@container`, no el de la ventana) |
| Cuadrículas con `minmax(Npx, 1fr)` podían forzar desborde en contenedores angostos (8 lugares) | `minmax(min(Npx, 100%), 1fr)` |
| Falsos positivos revisados: contenido que pasa bajo la barra inferior fija del celular; enlaces que ocupan dos líneas; 16 px de la barra de scroll en la emulación | Sin cambios |

## Crítica (escritorio)

| Hallazgo | Severidad | Estado |
|---|---|---|
| Primer día: "Abrir caja" decía "No hay cajas libres… Configuración → Cajas" sin botón: callejón sin salida | 🔴 | Botón "Crear una caja" (→ Configuración → Cajas) para quien puede crear cajas; los demás ven a quién pedírsela. Prueba en `RegisterSafetyTests` |
| La barra lateral necesitaba scroll a 768 px de alto (Ayuda, usuario y Salir ocultos) | 🟡 | Enlaces más compactos en pantallas de ≤ 820 px de alto; entra completa a 1366×728 |
| "Comprobantes electrónicos" se cortaba en la barra lateral | 🟡 | "Comprobantes e-CF" |
| Entrada: lista "¿Quién entra?" y teclado separados por un hueco grande a ≥ 1280 px | 🟢 | Pendiente: juntarlos en un bloque centrado |
| "Para revisar" repite "Quedan 0 comprobantes" para E31, E32 y E34 | 🟢 | Pendiente: agruparlos en una fila |
| Venta: total y Cobrar dominan, pesaje claro, teclas F visibles | ✅ | Se mantiene |

Sugerencia de producto (no aplicada): que la instalación cree "Caja 1" sola, para que el dueño pueda vender sin pasar por Configuración.
