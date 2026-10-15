# Escuadra: el sistema de diseño del producto

> 15 de octubre de 2026. Escuadra es el sistema de diseño de **Toki** (nombre interno).
> - **Sistema publicado (fuente de verdad: tokens, manual de marca, componentes):** https://claude.ai/artifact/VbcAbd1TMogPauAsyksDfF
> - Lienzo de pantallas que lo usa: https://claude.ai/artifact/WnQS3BTacA39zatGfr6Und

## Para qué

Que todas las pantallas (caja, administración, celular del dueño, reportes y tickets) se vean y se comporten igual, y que una pantalla nueva se arme con piezas ya probadas en vez de dibujarse desde cero. Es la respuesta al dolor #2 y #12 del sistema viejo: copias por cliente y pantallas hechas a mano cada vez.

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
| **Escuadra propio** | Son ~25 componentes sencillos (botones, campos, teclado numérico, líneas de carrito). Control total del tamaño táctil, el foco del escáner y el rendimiento en PCs viejas. |
| `QuickGrid` (Microsoft, MIT) | **Sí**, solo para tablas de administración (ordenar, paginar, virtualizar). Se envuelve en un componente Escuadra para que use los tokens. |

## Partes del sistema

### 1. Tokens (la fuente única de verdad)

- Un archivo `tokens.json` → se genera `escuadra.css` con variables CSS (`--color-marca`, `--espacio-3`, `--texto-total`…).
- **Color:** marco, fondo, superficie, bordes (tarjeta #C9CED4, control #7A838C), texto, marca (azul Toki #0B5CAD, fijo), éxito, aviso, error, sin-internet. Tema claro por defecto; tema oscuro opcional para turnos de noche.
- **Tipografía:** IBM Plex Sans 400/600/700 **incluida en la app** (licencia OFL; nunca desde internet). Cifras tabulares. Escala: 12 · 13 · 15 · 17 · 20 · 24 · 32 · 44 · 52 · 72.
- **Espacio y forma:** escala 4 · 8 · 12 · 16 · 24 · 32 · 48; radios 4 / 6 / 8; bordes de 1 px; sin sombras salvo diálogos.
- **Tamaños táctiles:** mínimo 48 px; 72 px para lo que mueve dinero.
- **Movimiento:** solo opacidad 120–150 ms y el resaltado de la línea nueva; respeta `prefers-reduced-motion`.

### 2. Marca: Toki es nuestra marca (sin colores por revendedor)

Decisión del 15 de octubre de 2026: **no es marca blanca a nivel visual**. Los colores y el logo son siempre los de Toki. Un revendedor solo aparece como "Vendido por …" en la factura y en la pantalla de ayuda.

- Colores de marca: Azul Toki `#0B5CAD`, Tinta `#14181D`, Papel `#FFFFFF`, Mostrador `#EEF0F2`, Azul claro `#E4EFFA`, Azul noche `#6DB3F5` (tema oscuro).
- Logo: **C5, la etiqueta con el ojo que guiña** (símbolo azul #0B5CAD con la "t" blanca y el ojo a la izquierda, junto a "toki" en IBM Plex Sans 700). Variantes: ícono de app (fondo azul, símbolo blanco), 16 px y una tinta para la impresora térmica. Fuente: artboard `CaraGuino` del canvas del logo. Los estados de la mascota (contento, pensando, atento) cambian solo el ojo.
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

## Escuadra Admin: la capa de la administración (TK-173)

La caja se opera; la administración se **revisa**. Por eso la administración de la PC de la tienda (Blazor) y el panel del dueño en la nube (React) usan una capa propia, `escuadra-admin.css`, que solo aplica dentro de `.esc-admin`. La caja (entrar, vender, cobrar, abrir y cerrar) sigue con `escuadra.css` tal cual: alto contraste, teclado primero, 48/72 px.

| Token | Valor |
|---|---|
| Marca | Escala `--brand-50 … --brand-900`; **600 = Azul Toki #0B5CAD** (logo C5). 500 #1F74C2 para barras, 700 #094B8E para hover y "hoy" |
| Neutros cálidos | Fondo #F6F5F2, superficie #FFFFFF, superficie 2/3 #F2F0EC / #E9E6E0, líneas #ECE9E3 / #DAD5CC; texto #191C20, #57524A y #6E685E (todos AA sobre el fondo) |
| Semánticos suaves | Fondo con tinte y texto fuerte: éxito #16663A/#E6F4EB, aviso #8C4A06/#FCF1DD, error #B42318/#FDECEA; un punto de color en cada píldora |
| Datos (en orden fijo) | #1F6FC5 · #0E9384 · #D9822B · #8B5CF6 (validados para daltonismo, ΔE ≥ 11); periodo anterior en gris #D9D4CA |
| Tipografía | Héroe 46 px (38 en el celular), título 28, métrica 24, cuerpo 15, etiquetas 13 en tipo oración (sin MAYÚSCULAS); cifras tabulares |
| Forma | Radio 10 (controles), 16 (tarjetas), 20 (diálogos); superficies sin borde con sombra muy suave (`--elev-1`); foco con anillo azul de 3 px |
| Táctil | 44 px mínimo; barra inferior del celular de 60 px |

**Componentes nuevos** (Razor en `Toki.Escuadra/Components`, espejo en `src/Cloud/panel/src/ui.tsx`): `BarChart` (barras con la referencia en gris, tooltip al pasar o enfocar, estado vacío), `Sparkline`, `Delta` (+12 % con flecha), `Segmented` (Hoy / Ayer / 7 días / Mes), `EmptyState`, `Skeleton`, `Field` (etiqueta, ayuda y error en línea). Clases: marco `esc-shell` / `esc-side` (menú con íconos, contraíble) / `esc-topbar` + `esc-tabbar` (celular), `esc-page-head`, `esc-hero`, `esc-stats`, `esc-alerts` ("Para revisar"), `esc-barlist` (proporciones), `esc-tabs`, `esc-setting` (descripción a la izquierda, formulario a la derecha), `esc-ask` ("Pregúntale a Toki…"), `esc-hide-sm` / `esc-only-sm` (tablas que se vuelven lista en el celular).

**Íconos:** la administración usa más íconos que los 13 de la caja (navegación, métricas, alertas), del mismo juego de trazo estilo Lucide y siempre junto a una palabra.

**Reglas:** el número más importante es el más grande y siempre trae comparación; lo que hay que revisar va primero, con persona, monto, hora y una acción; las cifras se escriben como se dicen ("Faltaron RD$ 150.00 en Caja 2 · Yudelka Martínez · hoy, 12:48 p. m."); el rojo es solo para faltantes y errores; el modo oscuro (TK-183) redefine los mismos tokens bajo `:root[data-theme="dark"] .esc-admin`; `escuadra.js` (en el panel, `theme.ts`) pone `data-theme` en `<html>` según la elección del equipo (automático, claro u oscuro, guardada en `localStorage` con la clave `toki.theme`) o según `prefers-color-scheme`. Solo la administración se oscurece: la caja sigue clara. Los colores fijos (tooltip, fila al pasar, barra superior, borde de peligro) son tokens (`--tip-bg`, `--row-hover`, `--topbar-bg`, `--danger-line`).

**Pendientes de backend (no se cambiaron contratos):** reporte de medios de pago por periodo en la tienda (hoy se suma desde cada Z, hasta 31 turnos); unidad (lb/unidad) en los más vendidos de la nube; ventas por hora en la tienda (para comparar "a esta hora" también en la PC); nombres de clientes en el fiado de la nube.

## Pendientes

- Nombre del producto (afecta el nombre del paquete y la galería).
- Color de marca propio del producto (la interfaz usa azul por defecto).
- Validar el tema claro con luz fuerte en una tienda real y en una pantalla barata.
