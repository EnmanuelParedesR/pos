# Escuadra: el sistema de diseño del producto

> 15 de octubre de 2026. Escuadra es el sistema de diseño de **Toki** (nombre interno).
> - **Sistema publicado (fuente de verdad: tokens, manual de marca, componentes):** https://claude.ai/artifact/VbcAbd1TMogPauAsyksDfF
> - Lienzo de pantallas que lo usa: https://claude.ai/artifact/WnQS3BTacA39zatGfr6Und

## Para qué

Que todas las pantallas (caja, administración, celular del dueño, reportes y tickets) se vean y se comporten igual, que un revendedor pueda poner su marca sin romper nada, y que una pantalla nueva se arme con piezas ya probadas en vez de dibujarse desde cero. Es la respuesta al dolor #2 y #12 del sistema viejo: copias por cliente y pantallas hechas a mano cada vez.

## Principios (los mismos de la hoja del lienzo)

1. **Lo primero es lo más grande:** total, peso y devuelta mandan en cada pantalla.
2. **El escáner nunca se pierde:** todo escaneo llega al campo de venta; en PIN, cantidad o efectivo se ignora con aviso.
3. **Sin internet no es un error:** ámbar con la cola de e-CF, nunca rojo.
4. **Avisos en línea, no ventanas:** el próximo escaneo reemplaza el aviso.
5. **La pantalla enseña:** teclas visibles en cada control, sin gestos escondidos.
6. **Nada se mueve:** mismas posiciones en cada venta; sin animar totales, peso ni cuadrículas.
7. **Pocos íconos:** 13 en todo el producto, siempre con palabra o estado.

## Decisión: componentes propios, sin MudBlazor ni Fluent UI

`read-v1.md` y `arquitectura-v1-vsa.md` dejaban pendiente "MudBlazor o Fluent UI". Se decide **construir los componentes propios de Escuadra**:

| Opción | Por qué no / por qué sí |
|---|---|
| MudBlazor | Se ve como Material (sombras, ripple, tipografía); quitarle ese aspecto cuesta más que hacer los componentes. |
| Fluent UI Blazor | Se ve como Microsoft 365; componentes pensados para oficina, no para una caja de pie con pantalla táctil. |
| **Escuadra propio** | Son ~25 componentes sencillos (botones, campos, teclado numérico, líneas de carrito). Control total del tamaño táctil, el foco del escáner, el tema por revendedor y el rendimiento en PCs viejas. |
| `QuickGrid` (Microsoft, MIT) | **Sí**, solo para tablas de administración (ordenar, paginar, virtualizar). Se envuelve en un componente Escuadra para que use los tokens. |

## Partes del sistema

### 1. Tokens (la fuente única de verdad)

- Un archivo `tokens.json` → se genera `escuadra.css` con variables CSS (`--color-marca`, `--espacio-3`, `--texto-total`…).
- **Color:** marco, fondo, superficie, bordes, texto, marca (del revendedor), éxito, aviso, error, sin-internet. Tema claro por defecto; tema oscuro opcional para turnos de noche.
- **Tipografía:** IBM Plex Sans 400/600/700 **incluida en la app** (licencia OFL; nunca desde internet). Cifras tabulares. Escala: 12 · 13 · 15 · 17 · 20 · 24 · 32 · 44 · 52 · 72.
- **Espacio y forma:** escala 4 · 8 · 12 · 16 · 24 · 32 · 48; radios 4 / 6 / 8; bordes de 1 px; sin sombras salvo diálogos.
- **Tamaños táctiles:** mínimo 48 px; 72 px para lo que mueve dinero.
- **Movimiento:** solo opacidad 120–150 ms y el resaltado de la línea nueva; respeta `prefers-reduced-motion`.

### 2. Marca: Toki es nuestra marca (sin colores por revendedor)

Decisión del 15 de octubre de 2026: **no es marca blanca a nivel visual**. Los colores y el logo son siempre los de Toki. Un revendedor solo aparece como "Vendido por …" en la factura y en la pantalla de ayuda.

- Colores de marca: Azul Toki `#0B5CAD`, Tinta `#14181D`, Papel `#FFFFFF`, Mostrador `#EEF0F2`, Azul claro `#E4EFFA`, Azul noche `#6DB3F5` (tema oscuro).
- Logo y mascota: el **ticket con cara** y la sonrisa en ✓ (dirección A). Archivos SVG en el grupo "Logos" del sistema publicado: logo completo, símbolo, símbolo simplificado para 24px o menos y versión de una tinta para la impresora térmica.
- La marca nunca se usa para estados.

### 3. Componentes

| Grupo | Componentes |
|---|---|
| Marco | Barra de estado (caja, cajero, turno, internet, cola e-CF, equipos), barra de teclas, menú lateral de administración |
| Acciones | Botón principal de dinero (Cobrar), principal, secundario, peligro con PIN, tecla visible (keycap) |
| Entrada | Campo de escaneo (foco permanente), campo numérico de dinero, teclado numérico, PIN, selector segmentado, chips de categoría |
| Venta | Lectura de balanza (estable / pesando / vacía / desconectada), mosaico de producto, línea de carrito, bloque de totales, devuelta |
| Avisos | Aviso en línea (no encontrado, etiqueta repetida), píldora de estado, banner de sin conexión, diálogo de supervisor |
| Datos | Tabla (QuickGrid), barra de filtros, tarjeta de cifra, gráfica de barras simple, estado vacío |
| Celular | Tarjeta de resumen, burbuja del asistente, pestañas inferiores |
| Marca | Logo, mascota (solo en celular, bienvenida y estados vacíos; nunca en la caja) |
| Impresión | Plantillas de ticket, Z y cuadre (ESC/POS 58/80 mm) y de reporte en hoja carta, con los mismos tokens de texto |

### 4. Íconos

Un solo juego de trazo (2 px, 20 px) incluido como sprite SVG: buscar, balanza, impresora, conexión, usuario, candado, listo, aviso, error, quitar, borrar, flecha, más. Base sugerida: Lucide (licencia ISC), copiando solo esos 13.

## Dónde vive en el código

```
src/Client/
├─ Toki.Escuadra/        ← Razor Class Library: tokens, CSS, componentes, íconos, fuentes
├─ Toki.Client.UI/       ← pantallas de la caja y administración (usa Escuadra)
└─ …
src/Cloud/ (celular del dueño y panel web) ← también usa Toki.Escuadra
tests/
├─ Toki.Escuadra.Tests/  ← bUnit por componente
└─ Toki.Escuadra.Visual/ ← capturas con Playwright a 1024×768, 1366×768 y 390×844, tema claro/oscuro y 3 marcas
```

- **Galería de componentes** dentro de la app (solo en modo desarrollo): cada componente con todos sus estados. Es la referencia viva para programar y revisar.
- **Reglas automáticas:** ningún color, tamaño o fuente escrito a mano fuera de los tokens (linter de CSS en CI); prueba de contraste de cada par texto/fondo; captura visual que falla si algo cambia sin querer.

## Cómo se diseña y se revisa

1. Las pantallas nuevas se dibujan primero en el lienzo con las piezas de Escuadra.
2. Las revisan los agentes `ux-flow-reviewer` y `ui-visual-reviewer` (en `.claude/agents/`).
3. Un componente nuevo entra a Escuadra solo si lo usan al menos dos pantallas; si no, se queda en la pantalla.
4. Cambios a Escuadra: con su entrada en el registro de cambios y las capturas visuales actualizadas.
5. Cuando el nombre del producto esté decidido, publicar Escuadra como **Design System** en el lienzo para que todo diseño nuevo lo use automáticamente.

## Orden de construcción

| Fase | Qué | Cuándo |
|---|---|---|
| 0 | Tokens, fuentes, íconos, marco (barra de estado y de teclas), botones, tecla visible, campo de escaneo, aviso en línea, galería y pruebas visuales | **Antes del primer slice**, junto al esqueleto |
| 1 | Balanza, mosaico, línea de carrito, totales, teclado numérico, PIN, diálogo de supervisor | Con el slice de venta |
| 2 | Tabla, filtros, tarjeta de cifra, gráfica, estados vacíos, menú lateral | Con administración y reportes |
| 3 | Tema oscuro, componentes del celular, mascota, plantillas de impresión | Con el plan Negocio |

## Pendientes

- Nombre del producto (afecta el nombre del paquete y la galería).
- Color de marca propio del producto (la interfaz usa azul por defecto).
- Validar el tema claro con luz fuerte en una tienda real y en una pantalla barata.
