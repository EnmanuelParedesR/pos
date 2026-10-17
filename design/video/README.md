# Video del producto (TK-296)

El video de "Cómo funciona" en el inicio: 42 s, 1280×720, sin sonido, con textos en pantalla y datos de ejemplo. Sale de la app real, no de una animación: si cambia la pantalla, se vuelve a capturar y a montar con dos comandos. No hace falta instalar nada: Playwright .NET (el de `tests/Abastalia.UiTests`) y Blender 4.5 portable.

| Archivo | Qué es |
|---|---|
| `src/Cloud/panel/src/pages/site/productVideo.json` | El guion: tomas, duración, textos en pantalla y descripción de cada toma. Única fuente de los textos del video, de la pista `.vtt` y de la transcripción del sitio. |
| `tests/Abastalia.UiTests/ProductVideoTests.cs` | La captura (`ProductVideo_capture`) y los textos como PNG (`ProductVideo_overlays`). Solo corre con `ABASTALIA_VIDEO=1`. |
| `design/video/overlays.html` | Diseño de los textos, la portada, el cierre y el marco del teléfono (fuente y colores de Escuadra). |
| `design/video/montaje.py` | El montaje y la codificación en Blender. |
| `design/video/capturas/` | Fotogramas y PNG intermedios (ignorado por git). |
| `src/Cloud/panel/public/media/abastalia-demo.*` | Lo que publica el sitio: `.mp4` (H.264, ≤ 3 MB), `.webm` (VP9, ≤ 3 MB), `-portada.webp` (≤ 60 KB) y `.es.vtt` (descripciones). |

## Rehacerlo (PowerShell, desde la raíz del repo)

```powershell
dotnet build tests/Abastalia.UiTests; npm ci --prefix src/Cloud/panel   # una vez
$env:ABASTALIA_VIDEO = "1"; dotnet test tests/Abastalia.UiTests --no-build -- --filter-method "*ProductVideo_*"
& "$env:LOCALAPPDATA\Programs\blender-portable\blender-4.5.14-windows-x64\blender.exe" -b --factory-startup --python design/video/montaje.py -- design/video/capturas src/Cloud/panel/public/media
```

El primero levanta en 127.0.0.1 la nube (Sqlite), el panel (`vite build` + `preview`), la API de la tienda conectada a esa nube y la caja web con balanza falsa (1.250 lb), todo con bases desechables en `%TEMP%`, y graba cuatro tomas (`venta`, `cobro`, `cierre`, `telefono`). Tarda unos 2 minutos. El segundo escribe los cuatro archivos y muestra sus pesos.

- **Solo cambió un texto** de `productVideo.json`: `--filter-method "*ProductVideo_overlays"` (segundos) y el comando de Blender. No hace falta volver a capturar.
- **Revisar fotogramas** sin codificar: `$env:ABASTALIA_VIDEO_REVIEW = "4,14,30"` antes de Blender guarda `revision-XXs.png` en la carpeta de salida (use una carpeta temporal).
- **Calidad**: `ABASTALIA_VIDEO_MP4` (por defecto `HIGH`) y `ABASTALIA_VIDEO_WEBM` (`MEDIUM`; el VP9 de Blender pesa más a igual nivel e ignora un bitrate fijo), con un fotograma clave cada 5 s. `ABASTALIA_VIDEO_ONLY=mp4|webm` codifica uno solo. Para comparar nitidez: `ABASTALIA_VIDEO_REVIEW` da los fotogramas sin codificar y un `new_movie` del video en el secuenciador de Blender los mismos segundos ya codificados (TK-307: 41–46 dB PSNR en ambos).
- **La hora es fija** (TK-307): la prueba corre la nube, la API de la tienda y la caja con `Clock__Shift` (solo el entorno E2E lo lee: `ShiftedClock`) y el navegador del panel con el reloj de Playwright, todos al último día de semana a las 10:30 a. m. hora de la tienda. Da igual a qué hora se capture: la caja, la factura y el panel siempre dicen 10:30 a. m. y el panel muestra "Hoy" con la mañana de ventas.

## Datos

Todo es ficticio: la tienda "Comercial La Esquina", la dueña "Ana Pérez" (`ana.perez@example.com`), la cajera "María", el catálogo y dos semanas de ventas que sube una segunda PC de la tienda firmada con su propia clave (como hace la app). Talia no llama a ningún modelo: la prueba intercepta `/api/assistant/ask` y contesta con los tres productos más vendidos de la semana que da el panel. El e-CF es el simulado de V1 (sin certificado ni DGII).

## Pesos del último render (17 oct 2026, TK-307)

`abastalia-demo.mp4` 2.4 MB · `abastalia-demo.webm` 2.5 MB · `abastalia-demo-portada.webp` 11 KB.
