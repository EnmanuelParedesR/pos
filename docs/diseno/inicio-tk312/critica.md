# Crítica final del inicio (TK-312)

17/10/2026. Revisado en 320, 375, 1366 y 1920 px, en claro y en oscuro, con y sin «reducir movimiento». Hoja de direcciones: `index.html` (Artifact https://claude.ai/artifact/PLStgbYkFGhEZL31dR7DEg).

## Qué eligió el dueño

Talia al frente (dirección C). Del resto se toma lo de A: seis beneficios arriba, el e-CF ilustrado, los hitos junto al video y movimiento sutil. En el mostrador va la foto real del colmado (opción 1c con imagen): el lazo de la balanza de aguja salió.

## Ciclo 1 (primera versión aplicada)

| Severidad | Hallazgo | Arreglo |
|---|---|---|
| 🟡 Media | En el teléfono, los seis beneficios eran seis tarjetas altas (unos 1,000 px de mosaicos) | Ícono al lado del título en todos los mosaicos de íconos por debajo de 560 px, como ya hacía el glosario |
| 🟡 Media | En escritorio, las cuatro preguntas de Talia iban una por fila: una cola larga bajo el teléfono | Dos por fila (ancho máximo 580 px) |
| 🟡 Media | El subtítulo del hero ocupaba cuatro líneas y repetía «desde el celular» del beneficio | Más corto: «Talia, su asistente, le dice cuánto vendió y quién le debe.» |
| 🟢 Baja | La foto del colmado y el póster del video no salen en una captura de página completa | No es un error: las dos se cargan al acercarse (lazy, TK-282) |
| 🟢 Baja | La barra de menú se sale del ancho a 320–375 px | Ya existía y es a propósito: el menú se desliza de lado (TK-245) |

## Ciclo 2

Sin hallazgos altos ni medios. Ningún desborde horizontal de 320 a 1920 px (el menú se desliza dentro de su barra). El oscuro, elegido en el pie, conserva el contraste de los tokens de siempre. La banda del e-CF sigue oscura en los dos temas. Con «reducir movimiento» o sin JavaScript, todo se ve quieto y completo.

## Veracidad

- Talia: solo lo que responden sus herramientas de solo lectura (`Assistant.cs`). La primera respuesta es la del video del producto; las otras llevan «Datos de ejemplo». Las acciones van en TK-329.
- e-CF: la firma ocurre en la PC principal (el modo de custodia en la nube de TK-151 está apagado). El chip «e-CF en cola: N» es el texto de la barra de la caja (`Frame.razor`). Las 72 horas son las de la DGII.
- Balanza: el inicio ya no muestra pesas analógicas. La tarjeta de Carnicería dice «Etiquetas de balanza», que es lo que la caja lee.

## Lighthouse (teléfono, 3 corridas, mejor LCP)

Inicio: LCP 2.34 s (antes 2.45 s), CLS 0, SEO 1, accesibilidad 1, rendimiento 0.96. Las otras 10 páginas: LCP 2.1–2.4 s, CLS 0, SEO 1, accesibilidad 1. Sin errores de aserción.
