# Imágenes y video del sitio (TK-281)

Originales de lo que muestra abastalia.com. Las versiones web (AVIF y WebP en 480, 800 y 1200 px) se generan con `node scripts/images.mjs` desde `src/Cloud/panel` y quedan en `src/Cloud/panel/public/img/site`.

## Capturas reales de la caja (`capturas/`)

Tomadas el 16 oct 2026 con Edge sin interfaz (`capturas/capturas.mjs`) sobre una tienda de prueba local con datos ficticios ("Comercial La Esquina", productos de ejemplo). Para repetirlas: levantar la API local y la caja, crear la tienda de prueba y correr el script (el PIN de prueba está en `appsettings.Development.local.json`, ignorado por git).

## Fotos (`fotos/`) — Pexels, licencia gratuita para uso comercial (pexels.com/license)

| Archivo | Uso | Fuente |
|---|---|---|
| `carniceria.jpg` | Portada de /carniceria | https://www.pexels.com/photo/woman-buying-sausages-20722688/ |
| `colmado.jpg` | Portada de /colmado y escena del inicio (TK-312) | https://www.pexels.com/photo/colorful-colombian-grocery-store-interior-38703834/ |
| `deli.jpg` | Portada de /deli | https://www.pexels.com/photo/market-scene-in-valencia-with-jamon-and-cheese-30811288/ |

Reemplazar por fotos propias de las tiendas piloto cuando existan (con autorización firmada de la tienda y de las personas que salen).

## Video

El lazo "pesando" (Pexels 12511385, balanza colgante de aguja) salió del sitio en TK-312: Abastalia solo lee balanzas digitales y etiquetas con peso. El único video del sitio es el del producto (`design/video/README.md`).
