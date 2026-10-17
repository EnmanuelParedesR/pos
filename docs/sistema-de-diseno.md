# Escuadra: el sistema de diseño del producto

> 15 de octubre de 2026. Escuadra es el sistema de diseño de **Abastalia** (nombre interno).
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
- **Color:** marco, fondo, superficie, bordes (tarjeta #C9CED4, control #7A838C), texto, marca (Berenjena #6B1F5E, fija; TK-356), éxito, aviso, error, sin-internet. Tema claro por defecto; tema oscuro opcional para turnos de noche.
- **Tipografía:** IBM Plex Sans 400/600/700 **incluida en la app** (licencia OFL; nunca desde internet). Cifras tabulares. Escala: 12 · 13 · 15 · 17 · 20 · 24 · 32 · 44 · 52 · 72.
- **Espacio y forma:** escala 4 · 8 · 12 · 16 · 24 · 32 · 48; radios 4 / 6 / 8; bordes de 1 px; sin sombras salvo diálogos.
- **Tamaños táctiles:** mínimo 48 px; 72 px para lo que mueve dinero.
- **Movimiento:** solo opacidad 120–150 ms y el resaltado de la línea nueva; respeta `prefers-reduced-motion`.

### 2. Marca: Abastalia es nuestra marca (sin colores por revendedor)

Decisión del 15 de octubre de 2026: **no es marca blanca a nivel visual**. Los colores y el logo son siempre los de Abastalia. Un revendedor solo aparece como "Vendido por …" en la factura y en la pantalla de ayuda.

- Colores de marca (TK-356): **Berenjena `#6B1F5E`** (hover `#551849`), Tinta `#14181D`, Papel `#FFFFFF`, Mostrador `#EEF0F2`, Berenjena clara `#F5EAFA`, Berenjena noche `#E2A3D3` (tema oscuro del sitio) y **Ámbar `#FFC233`** (TK-273: solo acento en anuncios, avatar alterno y Talia; siempre sobre berenjena o con texto Tinta, nunca solo sobre blanco ni como texto).
- Logo: **la etiqueta de precio girada 45°** (TK-273): etiqueta berenjena #6B1F5E con la esquina cortada, el hueco de colgar y la "a" blanca de dos pisos, junto a "abastalia" en IBM Plex Sans 700. Sobre oscuro (marco de la caja, administración en tema oscuro) la etiqueta es blanca y la "a" berenjena. Una tinta para la impresora térmica: el hueco y la "a" quedan del color del papel. Componentes: `Logo.razor` y `Logo` en `ui.tsx` (colores en `.esc-logo` de `escuadra.css`). Antes: C5, la etiqueta con el ojo que guiña (Toki, TK-267).
- Talia (la asistente): la misma etiqueta en ámbar con cara, dentro de un círculo berenjena. Estados atenta, contenta y pensando: cambian solo los ojos y la boca. Componente: `Talia` en `ui.tsx` (solo el panel). Reglas de uso en `docs/mascota.md`.
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
├─ Abastalia.Escuadra/        ← Razor Class Library: tokens, CSS, componentes, íconos, fuentes
├─ Abastalia.Client.UI/       ← pantallas de la caja y administración (usa Escuadra)
└─ …
src/Cloud/ (celular del dueño y panel web) ← también usa Abastalia.Escuadra
tests/
├─ Abastalia.Client.UI.Tests/ ← bUnit por componente (EscuadraComponentTests) y contraste y tokens (EscuadraTokenTests), TK-243
└─ Abastalia.UiTests/         ← EscuadraVisualTests (TK-243): capturas con Playwright de /dev/escuadra a 1366×768 (caja clara, administración clara y oscura)
                           contra Baselines/*.{windows,linux,macos}.png, con tolerancia de píxeles; si falta la referencia la escribe; borrarla acepta un cambio
```

- **Galería de componentes** dentro de la app (solo en modo desarrollo): cada componente con todos sus estados. Es la referencia viva para programar y revisar.
- **Reglas automáticas:** ningún color, tamaño o fuente escrito a mano fuera de los tokens (linter de CSS en CI); prueba de contraste de cada par texto/fondo; captura visual que falla si algo cambia sin querer (`EscuadraVisualTests`).

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
| Marca | Escala `--brand-50 … --brand-900`; **600 = Berenjena #6B1F5E** (la etiqueta del logo). 500 #9B418A para barras, 700 #57174C para hover y "hoy"; en oscuro 500 #C17CB2 (TK-356) |
| Neutros cálidos | Fondo #F6F5F2, superficie #FFFFFF, superficie 2/3 #F2F0EC / #E9E6E0, líneas #ECE9E3 / #DAD5CC; texto #191C20, #57524A y #6E685E (todos AA sobre el fondo) |
| Semánticos suaves | Fondo con tinte y texto fuerte: éxito #16663A/#E6F4EB, aviso #8C4A06/#FCF1DD, error #B42318/#FDECEA; un punto de color en cada píldora |
| Datos (en orden fijo) | #1F6FC5 · #0E9384 · #D9822B · #8B5CF6 (validados para daltonismo, ΔE ≥ 11); periodo anterior en gris #D9D4CA |
| Tipografía | Héroe 46 px (38 en el celular), título 28, métrica 24, cuerpo 15, etiquetas 13 en tipo oración (sin MAYÚSCULAS); cifras tabulares |
| Forma | Radio 10 (controles), 16 (tarjetas), 20 (diálogos); superficies sin borde con sombra muy suave (`--elev-1`); foco con anillo de marca de 3 px |
| Táctil | 44 px mínimo; barra inferior del celular de 60 px |

**Componentes nuevos** (Razor en `Abastalia.Escuadra/Components`, espejo en `src/Cloud/panel/src/ui.tsx`): `BarChart` (barras con la referencia en gris, tooltip al pasar o enfocar, estado vacío), `Sparkline`, `Delta` (+12 % con flecha), `Segmented` (Hoy / Ayer / 7 días / Mes), `EmptyState`, `Skeleton`, `Field` (etiqueta, ayuda y error en línea). Clases: marco `esc-shell` / `esc-side` (menú con íconos, contraíble) / `esc-topbar` + `esc-tabbar` (celular), `esc-page-head`, `esc-hero`, `esc-stats`, `esc-alerts` ("Para revisar"), `esc-barlist` (proporciones), `esc-tabs`, `esc-setting` (descripción a la izquierda, formulario a la derecha), `esc-ask` ("Pregúntale a Talia…"), `esc-hide-sm` / `esc-only-sm` (tablas que se vuelven lista en el celular).

**Íconos:** la administración usa más íconos que los 13 de la caja (navegación, métricas, alertas), del mismo juego de trazo estilo Lucide y siempre junto a una palabra.

**Reglas:** el número más importante es el más grande y siempre trae comparación; lo que hay que revisar va primero, con persona, monto, hora y una acción; las cifras se escriben como se dicen ("Faltaron RD$ 150.00 en Caja 2 · Yudelka Martínez · hoy, 12:48 p. m."); el rojo es solo para faltantes y errores; el modo oscuro (TK-183) redefine los mismos tokens bajo `:root[data-theme="dark"] .esc-admin`; `escuadra.js` (en el panel, `theme.ts`) pone `data-theme` en `<html>` según la elección del equipo (automático, claro u oscuro, guardada en `localStorage` con la clave `abastalia.theme`) o según `prefers-color-scheme`. Solo la administración se oscurece: la caja sigue clara. Los colores fijos (tooltip, fila al pasar, barra superior, borde de peligro) son tokens (`--tip-bg`, `--row-hover`, `--topbar-bg`, `--danger-line`).

**Pendientes de backend:** ninguno. Ya existen el reporte de medios de pago por periodo y el de ventas por hora en la tienda (`/api/reports/sales/by-payment-method`, `/by-hour`), la unidad en los más vendidos de la nube y los nombres de clientes en el fiado de la nube.

## Pendientes

- Nombre y color de marca: decididos (Abastalia, TK-267; color Berenjena #6B1F5E desde TK-356, antes Azul Abastalia #0B5CAD). Logo: Etiqueta 2.0 (TK-273, abajo).
- Validar el tema claro con luz fuerte en una tienda real y en una pantalla barata.

### Marca Abastalia (TK-267, 16 oct 2026)

- Se mantienen la etiqueta con el ojo que guiña, la paleta (Azul Abastalia #0B5CAD, Tinta #14181D) y la tipografía. Solo cambió la letra del símbolo: de la "t" de Toki a una **"a" de dos pisos**, la misma forma de la "a" de IBM Plex del nombre `abastalia`.
- Variantes probadas en `docs/diseno/marca-abastalia.html` (a simple, a doble y a con cola, de 16 a 120 px, en claro, oscuro y sobre azul). Crítica de diseño: la "a" simple se confunde con "d" u "o" a 16–32 px y la cola se pierde a 16 px. La "a" doble se lee en todos los tamaños.
- El color no cambia: "abasto" no pide otro color, y el azul conserva la confianza y la continuidad (blanco sobre azul 6.6:1, AA). El nombre de 9 letras entra en la cabecera del sitio y del panel a 320 px sin desborde.
- Imágenes para buscadores y redes: `src/Cloud/panel/public/og/abastalia.png` (1200×630) y `og/logo.png` (512×512), generadas con Edge headless a partir del mismo SVG.

### Logo v2: Etiqueta 2.0 (TK-273, 16 oct 2026)

- Por qué cambió: el símbolo C5 juntaba tres ideas (etiqueta, ojo de Toki y letra). A 16 px el ojo y la "a" se volvían una mancha, y la punta a la izquierda dejaba medio vacío el avatar redondo de las redes.
- Exploración en `docs/diseno/logo-abastalia-v2.html`: cuatro direcciones (A Etiqueta 2.0, B Toldo, C Moneda, D Sello) probadas a 16 px reales, avatar, anuncio de Meta, Google, WhatsApp, pestaña oscura y recibo térmico a una tinta. El dueño eligió **A**.
- La etiqueta se gira 45° para que llene el cuadrado y el círculo; la "a" pasa de 25% a 40% del ancho. El ojo se cambia por el hueco de colgar, que se imprime limpio en la térmica. A 16 px el hueco cae en un píxel y la esquina cortada sostiene la silueta.
- **Ámbar #FFC233** nuevo, solo como acento: el azul Abastalia contra el azul de Facebook (#0866FF) tiene 1.4:1 y se funde con la interfaz de Meta. Ámbar sobre azul 4.1:1, Tinta sobre ámbar 9.9:1.
- Azul sobre Tinta es 2.7:1, así que sobre oscuro la etiqueta pasa a blanco (`.esc-logo--on-dark` y `:root[data-theme="dark"] .esc-admin .esc-logo`).
- **Talia**: etiqueta ámbar con ojos y sonrisa en Tinta, en un círculo azul (no pierde la silueta sobre blanco, que con ámbar solo sería 1.5:1). En el panel va en el chat (atenta; pensando mientras busca; contenta en el estado vacío) y en la barra "Pregúntale a Talia".
- Archivos de marca en `src/Cloud/panel/public/og/`: `abastalia.png` (1200×630, barra azul con tramo ámbar), `logo.png` (512, sobre blanco, para JSON-LD), `avatar.png` y `avatar-ambar.png` (1024, a sangre: las redes lo recortan en círculo) y `talia.png` (1024, perfil del WhatsApp de Talia). Todos con Edge headless desde el mismo SVG.
- Crítica de diseño final (sobre la galería de Escuadra, el sitio y el acceso en oscuro, y las imágenes generadas):
  - Funciona: la silueta con la esquina cortada se distingue a 16 px; en el marco de la caja y en oscuro la etiqueta blanca se lee de inmediato; el recibo sale limpio a una tinta; el avatar llena el círculo; Talia se reconoce como de la familia sin confundirse con el logo (color y cara en lugar de letra).
  - Se corrigió en la ronda: la boca de Talia "pensando" se veía triste (curva hacia abajo); ahora es una sonrisa corta.
  - Se acepta: a 16 px el hueco cae en un píxel; a 28 px la diferencia entre estados de Talia es sutil, y el texto ("Buscando en sus datos…") es el que dice el estado.
  - Se mantiene a propósito: en el menú del panel "Asistente" sigue con el ícono de línea (sparkles); Talia a color rompería el ritmo de los íconos de línea. Talia va donde ella habla: el chat y la barra "Pregúntale a Talia".
  - Pendiente fuera de este ticket: no hay ícono .ico del instalador ni de la ventana de la caja; cuando se haga, sale de `og/avatar.png`.

### Paleta Berenjena (TK-356, 17 oct 2026)

- Por qué cambió: medimos el color de marca de 26 sitios que el dueño ve cada día (terminal de tarjetas, bancos, DGII, otros puntos de venta). El Azul Abastalia #0B5CAD quedaba a ΔE 7 (OKLab ×100) del terminal **Azul** #0072C6, que está junto a casi todas las cajas, y a ΔE 4 de Treinta #0050BD, que también combina azul con amarillo. Ocho de esas marcas son azules; ninguna es violeta, ciruela ni marrón.
- Se probaron nueve paletas (Añil, Berenjena, Barro, Tinta y etiqueta amarilla, Uva, Ciruela, Buganvilia, Vino, Cacao) con maquetas de la caja, del sitio y del tema oscuro: https://claude.ai/artifact/XWrmoeeyBm7SUuZ8cVbxqe. El dueño eligió **Berenjena**: ΔE 16 o más frente a todas las marcas medidas y 17 o más frente a los colores de estado (error, aviso, éxito, sin internet), así que el botón Cobrar no se confunde con una alerta. Barro y Buganvilia se descartaron por acercarse al rojo de error; Tinta y etiqueta, porque el ámbar ya significa "sin internet".
- No cambian: la forma del logo, IBM Plex Sans, los neutros, los colores de estado, la paleta de datos de los gráficos ni el Ámbar #FFC233. Talia conserva su etiqueta ámbar, ahora en un círculo berenjena.
- Contraste: blanco sobre berenjena 10.6:1, ámbar sobre berenjena 6.6:1, berenjena sobre el fondo #F6F5F2 9.7:1; en oscuro, enlaces #C17CB2 5.4:1, blanco sobre el botón #9C4E8C 5.4:1, Berenjena noche #E2A3D3 sobre Tinta 8.8:1. Berenjena sobre Tinta es 1.7:1, así que sobre oscuro la etiqueta sigue pasando a blanco.
- La escala `--brand-50 … 900` mantiene el tono (OKLCH −25°) y los mismos pasos de claridad que la escala azul anterior. Los colores de categoría que elige el usuario (por defecto #0B5CAD) son datos, no marca, y no cambian.
- Imágenes regeneradas en `src/Cloud/panel/public/og/` (`abastalia.png`, `logo.png`, `avatar.png`, `avatar-ambar.png`, `talia.png`) y `favicon.svg`.
- Crítica de diseño (galería de Escuadra en claro y oscuro, sitio a 1366 y 375 px, sitio en oscuro, imágenes de redes):
  - Funciona: el botón Cobrar en berenjena no se confunde con Peligro ni con la línea en error; la etiqueta blanca sobre el marco oscuro se lee igual que antes; en oscuro el botón principal #9C4E8C y la Berenjena noche del sitio mantienen AA; Talia gana presencia (ámbar sobre berenjena 6.6:1).
  - Se corrigió en la ronda: los tintes claros salían rosados y quedaban a ΔE 2 del fondo de error (la línea seleccionada y el aviso informativo parecían alertas). La escala 50/100 y los tintes de la caja y del sitio se corrieron 20° hacia el violeta (#F9F2FD, #F1E4F8, #F5EAFA; en oscuro #2F1F36); ahora se ven lila junto al salmón del error, y el texto y el ícono siguen marcando la diferencia.
  - Se acepta: la paleta de datos de los gráficos empieza en azul #1F6FC5 (validada para daltonismo); el gráfico de una sola serie usa la marca.
  - Fuera de este ticket: los gráficos de los videos (`design/video/overlays.html`, sesión de videos, avisada con la paleta) y las maquetas viejas en `docs/diseno/`, que quedan como historial.
