# Imágenes y video del sitio (TK-281)

Originales de lo que muestra abastalia.com. Las versiones web (AVIF y WebP en 480, 800 y 1200 px) se generan con `node scripts/images.mjs` desde `src/Cloud/panel` y quedan en `src/Cloud/panel/public/img/site`.

## Capturas reales de la caja (`capturas/`)

Tomadas el 16 oct 2026 con Edge sin interfaz (`capturas/capturas.mjs`) sobre una tienda de prueba local con datos ficticios ("Comercial La Esquina", productos de ejemplo). Para repetirlas: levantar la API local y la caja, crear la tienda de prueba y correr el script (el PIN de prueba está en `appsettings.Development.local.json`, ignorado por git).

## Fotos (`fotos/`) — Pexels, licencia gratuita para uso comercial (pexels.com/license)

| Archivo | Uso | Fuente |
|---|---|---|
| `carniceria.jpg` | Portada de /carniceria | https://www.pexels.com/photo/woman-buying-sausages-20722688/ |
| `colmado.jpg` | Portada de /colmado | https://www.pexels.com/photo/colorful-colombian-grocery-store-interior-38703834/ |
| `deli.jpg` | Portada de /deli | https://www.pexels.com/photo/market-scene-in-valencia-with-jamon-and-cheese-30811288/ |

Reemplazar por fotos propias de las tiendas piloto cuando existan (con autorización firmada de la tienda y de las personas que salen).

## Video (`src/Cloud/panel/public/media/pesando.*`)

"Shopkeeper Weighing Food in Store", Nathanael Arias, Pexels: https://www.pexels.com/video/shopkeeper-weighing-food-in-store-12511385/ (original 4K, 13 s). Recorte de 6 s sin audio:

```
ffmpeg -ss 2 -t 6 -i clip.mp4 -an -vf "scale=960:-2:flags=lanczos,fps=24" -c:v libx264 -preset slow -crf 30 -pix_fmt yuv420p -movflags +faststart pesando.mp4
ffmpeg -ss 2 -t 6 -i clip.mp4 -an -vf "scale=960:-2:flags=lanczos,fps=24" -c:v libvpx-vp9 -b:v 0 -crf 40 -row-mt 1 pesando.webm
ffmpeg -ss 2 -i clip.mp4 -frames:v 1 -vf scale=960:-2 -c:v libwebp -quality 72 pesando-portada.webp
```

En la página arranca solo cuando se ve, se pausa al salir de pantalla y no se mueve con "reducir movimiento" (`LoopVideo`).
