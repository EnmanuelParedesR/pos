# Talia: la asistente de Abastalia (panel y sitio)

17/10/2026. Tickets: TK-315 (Talia en el sitio), TK-347 (base de conocimiento), TK-348 (evaluaciones y ajuste del modelo), TK-349 (cara v2 y «Me sirvió»), TK-350 (este plan). Decisión de arquitectura: ADR-0015.

Talia contesta en dos lugares con el mismo código:

| | Panel (`/app`, con sesión) | Sitio (abastalia.com, sin cuenta) |
|---|---|---|
| Quién pregunta | Dueño y su equipo | Cualquier visitante (casi siempre un prospecto) |
| Endpoint | `POST /api/assistant/ask` | `POST /api/public/assistant/ask` |
| Herramientas | 10 de datos (solo lectura, solo su negocio) + `panel_help` + `search_knowledge` (todo) | Solo `search_knowledge` (solo artículos `public`) |
| Límites | Cuota del plan (preguntas y tokens al mes), 10/min por usuario, 5 vueltas, ≤ 4,000 tokens de salida por vuelta | 6/min por IP; presupuesto diario para todos (2M tokens ≈ US$0.80) y por IP (100K, ~30 preguntas); 3 vueltas; ≤ 2,500 tokens de salida por vuelta; pregunta ≤ 500 letras; historial ≤ 6 de 500; cuerpo ≤ 32 KB |
| Dónde | Página Asistente, barra «Pregúntale a Talia» y botón flotante | Botón flotante en todas las páginas; el chat se descarga al abrirlo (no pesa en el LCP) |

## 1. Cómo funciona

```
pregunta ─► reglas fijas (system, se cachean) + contexto del día (system) + historial recortado
        ─► modelo ─► ¿pide herramienta? ─sí─► herramienta (datos del negocio o base de conocimiento) ─► modelo …
                                     └no─► respuesta ─► limpieza de Markdown ─► chat (texto plano; solo rutas conocidas son enlaces)
        la última vuelta va sin herramientas (tool_choice none): siempre contesta
```

- **Código:** `src/Hosts/Abastalia.Host.Cloud/Assistant/` — `Talia.cs` (bucle, ajustes, limpieza, `search_knowledge`), `TaliaKnowledge.cs` (búsqueda), `Assistant.cs` (Talia del panel y el cliente de Azure OpenAI), `PublicAssistant.cs` (Talia del sitio, presupuesto diario, «Me sirvió»), `TaliaUpdates.cs` (base desde Blob, precios de Paddle, vigía de la DGII). Frontend: `src/Cloud/panel/src/pages/AssistantPage.tsx` (chat de los dos), `TaliaPopup.tsx`, `pages/site/SiteTalia.tsx`.
- **Reglas (prompts):** `OwnerAssistant.Rules` y `PublicAssistant.Rules`. Primero las reglas, iguales para todos; después el contexto (fecha, tiendas). Así Azure cachea el prefijo: en las evaluaciones, ~40 % de los tokens de entrada salen de caché (cobrados a la décima parte).
- **Base de conocimiento:** `Assistant/Knowledge/*.md`, un tema por archivo y una respuesta por sección `##`, compilados en la API. 37 artículos (28 `public`, 9 `panel`). Búsqueda BM25 en memoria con normalización en español (sin acentos, plural y género, prefijo de 6 letras para verbos y sustantivos, sinónimos: «cuesta» → precio, «fiado» → cuentas por cobrar, «apagón» → internet). Sin servicio externo: cero costo y cero latencia.
- **Contenido que no se negocia:** el sitio no dice «fiado» (TK-312), ni en los artículos públicos ni en las respuestas; los precios salen del artículo `precios` (prueba que los compara con `config.ts`); nada interno (Azure, modelos, costos) en la base (prueba).
- **Fallas que se ven como respuesta:** el filtro de contenido de Azure (jailbreak) devuelve 400 y Talia contesta «No puedo ayudar con eso…» en vez de «no disponible» (lo encontraron las evaluaciones).

## 2. El modelo: barato pero bueno

Ajuste por bot en la configuración `Talia:Panel` y `Talia:Public` (`Deployment`, `ReasoningEffort`, `Verbosity`, `MaxCompletionTokens`); sin cambiar código.

**Resultados de las evaluaciones** (38 casos, juez gpt-5-mini; tabla completa en la sección 6):

RESULTADOS_RONDA_2

**Decisión:** DECISION_MODELO

## 3. El arnés (harness): cómo sabemos que Talia contesta bien

Tres capas, de la más barata a la más cara:

1. **Pruebas deterministas, en CI, gratis** (`tests/Abastalia.Cloud.Tests/Talia/`):
   - `TaliaKnowledgeTests`: cada artículo se lee, ids y títulos únicos, secciones de 12 a 260 palabras, enlaces que existen, nada interno, ningún «fiado» en lo público, precios iguales a `config.ts`, y **recall@3 ≥ 90 %** sobre 102 preguntas reales (`knowledge-evals.json`; hoy 96 %). Escribe `TestResults/talia/retrieval.txt` con las que fallan.
   - `PublicAssistantTests` / `PublicBudgetTests`: sin cuenta, solo `search_knowledge`, nunca artículos del panel, historial sin mensajes `system`, Markdown limpio, la última vuelta sin herramientas, presupuesto diario y su renovación al día siguiente de la tienda.
   - `AssistantTests` (panel): solo los datos del negocio de quien pregunta, reglas primero.
   - Panel (Vitest): `SiteTalia.test.tsx` (endpoint público sin token, enlaces, «Respuestas automáticas», «Me sirvió», Talia descansando), `talia.test.tsx` (el avatar PNG sale del mismo SVG; sin hueco a ≤ 32 px).
2. **Evaluaciones con modelos reales, a mano o antes de cambiar algo** (`TaliaLiveEvals`, apagadas en CI):
   - `./deploy/azure/talia-evals.ps1` (lee la clave con `az`; usa `oai-abastalia-dev-evals` en `rg-abastalia-dev`, nunca producción). Opciones: `-Models "gpt-5-nano:low:low,gpt-5-mini:low:low"` (despliegue:razonamiento:verbosidad), `-Repeat 3`, `-Only public-`, `-Judge gpt-5-mini`.
   - Una tienda de ejemplo con números conocidos («Colmado La Esquina»: hoy RD$ 2,500 en 3 ventas, ayer RD$ 1,600, faltante de María de RD$ 150…), 17 casos del panel y 21 del sitio (`cases.json`): reglas (contiene / no contiene, herramienta usada, sin Markdown, ≤ 160 palabras) y un **juez** (otro modelo) que califica de 1 a 5 contra lo que devolvieron las herramientas.
   - Informe en `TestResults/talia/report-*.md` (+ `.json`): por modelo, % que pasa, nota del juez, tokens (entrada, caché, salida, razonamiento), **US$ por 1,000 preguntas**, latencia p50/p95, y cada falla con su respuesta.
3. **Producción:**
   - Una línea de log por respuesta, **sin la pregunta**: bot, vueltas, herramientas, tokens, caché, razonamiento, ms y versión de la base (`Talia answered …`). En Application Insights se grafica costo y uso por bot.
   - «Me sirvió / No me sirvió» bajo cada respuesta: solo al tocarlo se envían la pregunta y la respuesta (`Talia feedback …`, el chat lo dice). Es la fuente de nuevos casos de evaluación.

**Regla de trabajo:** un cambio en las reglas, la base o el modelo se prueba primero con `talia-evals.ps1` y no baja la nota del juez ni el % que pasa del modelo elegido. Un 👎 con una pregunta nueva se vuelve un caso en `cases.json` o en `knowledge-evals.json`.

## 4. Cómo se mantiene al día (servicios ya construidos)

Tres servicios dentro de la API (`Assistant/TaliaUpdates.cs`), como `BackgroundService`: **sin Azure Functions**, porque corren en la Web App que ya se paga (US$0 extra) y se despliegan con ella. Una Function solo valdría la pena si estos trabajos crecieran tanto que molestaran a la API (no es el caso: unos KB cada 10 minutos y dos páginas al día).

```
repo Knowledge/*.md ──talia-knowledge-publish.ps1 (pruebas primero)──► Blob talia-knowledge
                                                                           │ cada 10 min
API: TaliaKnowledgeRefresher ── Problems() vacío? ──sí──► TaliaKnowledgeStore.Current ◄── las dos Talias
                                     └no─► se queda con la última base buena y lo dice en el log
     + Paddle encendido ──► los precios de Paddle reemplazan los provisionales (artículo precios)
API: TaliaSourceWatcher (cada 24 h) ── páginas de la DGII en `watch:` ── ¿cambió el texto? ──► correo al personal + log; nunca edita la base
```

| Servicio | Qué hace | Configuración |
|---|---|---|
| **Base fuera de la API** (`TaliaKnowledgeRefresher`) | Lee los `.md` del contenedor `talia-knowledge` cada 10 min; si pasan las mismas reglas de las pruebas (`TaliaKnowledge.Problems()`: formato, enlaces, nada interno, sin «fiado» en lo público, tamaño de las secciones) Talia los usa; si no, se queda con la última base buena y lo dice en el log (`Talia knowledge … rejected`). Contenedor vacío = la base compilada. | `Talia:Knowledge:Container` (`talia-knowledge` en Bicep), `ServiceUri` (por defecto la del archivo), `Folder` (desarrollo), `RefreshMinutes` (10) |
| **Precios en línea** (`TaliaLivePrices`) | Con el cobro de Paddle encendido, las tres primeras secciones del artículo `precios` (planes, adicionales, anual) salen del catálogo de Paddle con el mismo redondeo a RD$ del sitio; instalación, acompañamiento y promoción quedan como están. Así Talia nunca cita los provisionales de `config.ts` cuando ya hay precios reales. | `Talia:Knowledge:DopPerUsd` (61.5; una prueba lo compara con `config.ts`) |
| **Vigía de la DGII** (`TaliaSourceWatcher`) | Una vez al día baja las páginas que los artículos listan en `watch:` (documentación del e-CF, Facturador Gratuito, instructivo de contingencia), compara su texto visible (sin scripts ni valores de SharePoint por petición: dos descargas seguidas dan el mismo resultado) y, si cambió, escribe a `Certification:StaffEmails` qué artículos revisar. Guarda el estado en `talia-knowledge/_state/watch.json`. | `Talia:Knowledge:WatchHours` (24; 0 = apagado, así en las pruebas) |

| Qué cambia | Dónde | Cómo llega a producción |
|---|---|---|
| Un dato del producto (función nueva, plazo de la DGII) | `Assistant/Knowledge/<tema>.md` | PR → CI → `./deploy/azure/talia-knowledge-publish.ps1` (corre las pruebas de la base y sube; en ≤ 10 min Talia lo usa, sin desplegar la API). El despliegue normal de la API también lo trae compilado. |
| Un precio | Paddle (cuando el cobro está encendido) o `config.ts` + `precios.md` (mientras no) | Con Paddle, solo: el refresco lo toma del catálogo en ≤ 10 min. Sin Paddle, PR (una prueba exige que `precios.md` diga lo mismo que `config.ts`) | 
| Una pregunta que contestó mal (👎) | `cases.json` o `knowledge-evals.json` + el artículo | Igual; correr `talia-evals.ps1` antes del PR |
| Cómo contesta (tono, reglas) | `OwnerAssistant.Rules` / `PublicAssistant.Rules` | Despliegue de la API, con evaluaciones antes y después |
| El modelo o el razonamiento | `Talia:Panel` / `Talia:Public` en la Web App (y un despliegue nuevo en Bicep si es otro modelo) | Cambio de configuración, sin compilar |
| Pantallas nuevas del panel | `PanelHelp.cs` (mapa) y el artículo `panel-pantallas` | PR |

Para publicar, quien corre el script necesita el rol «Storage Blob Data Contributor» en `stabastalia…` (se asigna una vez; la API ya lo tiene por su identidad).

## 5. Lo que sigue (cuando haga falta)

Cada fase se activa por una señal medible, no por adelantado.

**Hecho en esta entrega:** base en el repositorio y en Blob con recarga validada, BM25 en memoria, precios de Paddle, vigía de la DGII, arnés y evaluaciones, telemetría sin preguntas, «Me sirvió». Costo fijo: US$0.

**Fase 1b — editar la base desde la consola de plataforma** (señal: más de 2 cambios de contenido por semana, o soporte quiere corregir respuestas sin Git).
- Pantalla para `PlatformAdmin`: editar un artículo, vista previa, «probar con Talia» y publicar al contenedor solo si `Problems()` está vacío. El refresco ya existe; falta la pantalla. Esfuerzo: ~2 días.

**Fase 2 — búsqueda semántica (RAG con embeddings)** (señal: recall@3 < 90 % en `knowledge-evals.json`, o > 300 artículos, o 👎 por «no encontró» > 10 %).
- `text-embedding-3-small` (ya está en eastus2; US$0.02 por millón de tokens: indexar toda la base cuesta menos de un centavo) y **búsqueda híbrida**: BM25 actual + similitud de vectores, mezcladas por rango (RRF). El refresco calcula los vectores cuando cambia la versión de la base.
- Dónde viven los vectores: **en memoria** mientras quepan (2,000 secciones × 1,536 floats ≈ 12 MB); después, columna `VECTOR` de Azure SQL (el SQL Basic actual ya la soporta) para no pagar un Azure AI Search (el más barato con vectores, Basic, ~US$75/mes).
- Se mide con las mismas evaluaciones: solo se activa si sube el recall sin subir el costo por pregunta.

**Fase 3 — documentos largos de la DGII** (señal: preguntas frecuentes sobre normas que la base no cubre).
- Ingesta de normas y guías públicas troceadas con su fuente y fecha, `audience: public`, con «según la DGII (fecha)…». El vigía ya avisa cuando cambian; la revisión humana antes de publicar sigue siendo obligatoria (un error ahí es un problema legal del cliente).

**Fase 4 — mejora continua con los datos de uso** (señal: > 1,000 preguntas por semana).
- Tabla `cloud.TaliaFeedback` (en vez del log) con la pregunta, la respuesta, la versión de la base y el modelo, solo de lo que la persona envió con «Me sirvió / No me sirvió»; retención 90 días; aviso en la política de privacidad.
- Tablero en la consola de plataforma: 👎 por tema, preguntas sin resultado en la base, costo por bot y por plan.
- Evaluación nocturna en CI contra el despliegue de evaluaciones (GitHub Actions programado, ~US$0.05 por corrida) que avisa si baja la nota. Necesita la clave del recurso de evaluaciones como secreto del repositorio (decisión del dueño: el repositorio es público).

**Otras decisiones para más adelante:**
- **Responses API** en vez de Chat Completions: los modelos `gpt-5.6-*` no aceptan herramientas con razonamiento en Chat Completions. Cambiar el cliente (`AzureOpenAIChatModel`) es ~1 día; vale la pena solo si las evaluaciones dicen que un 5.6 gana a igual costo.
- **Talia por WhatsApp** (ya previsto en `docs/mascota.md`): el mismo `PublicAssistant` detrás de la API de WhatsApp Cloud; requiere número y plantilla aprobada por Meta.
- **Respuestas en streaming:** baja la espera percibida (hoy p50 ~5 s); necesita SSE en la API y el chat. Solo si los 👎 hablan de lentitud.
- **Un segundo despliegue** si el sitio y el panel usan modelos distintos: `Talia:Public:Deployment` ya existe; falta el recurso en `main.bicep`.
- **Presupuesto del sitio con más de una instancia de la API:** hoy vive en memoria (`shortcut:` en `PublicAssistantBudget`); con dos instancias, pasarlo a `IQuota`.

## 6. Resultados completos de las evaluaciones

TABLA_COMPLETA

## 7. Lo que el dueño tiene que completar para que Talia conteste todo

Preguntas que la gente hará y que el repositorio todavía no contesta (Talia dirá «no lo sé con seguridad» y ofrecerá contacto):

- Un canal de contacto real (WhatsApp, teléfono o correo), horario de soporte y qué significa «soporte más rápido» del Pro. Hoy `/precios#contacto` dice «Canal de contacto por confirmar».
- La URL del instalador (`/descargar`, TK-303) y si se publica la zona de visitas y el precio de la sesión remota extra.
- Razón social y RNC de Abastalia, plazo de borrado de datos (borradores legales).
- Si la suscripción da NCF / crédito fiscal y si se puede pagar en pesos o por transferencia.
- Política de reembolsos más allá de «según Paddle».
- Modelos de balanza e impresora probados; precios del kit de equipos.
- Importar productos o clientes desde otro sistema.
- Estado de la certificación propia de Abastalia ante la DGII («¿ya está certificado?»).
- Monto público del descuento por recomendar.

Contradicciones encontradas al armar la base (se siguió el código y los términos; hay que corregir el texto del sitio):
- `/precios` («Reglas que no cambian») y el aviso «Pago pendiente» de Facturación dicen que la venta nunca se detiene; el código, `docs/licencias.md` y los términos tienen el estado **Bloqueada** 30 días después del vencimiento.
- `config.ts` dice «US$49 (≈ RD$3,015)» para el acompañamiento; con el redondeo del sitio da RD$3,010.
- El sitio dice «2/3/5 usuarios del panel»; el límite real son invitaciones por mes.

## 8. Recursos de Azure de esta entrega

- `rg-abastalia-dev` / `oai-abastalia-dev-evals` (eastus2, S0, acepta clave): despliegues `gpt-5-nano`, `gpt-5-mini`, `gpt-5.4-nano`, `gpt-5.6-luna`, `gpt-4.1-nano` (GlobalStandard, 50K TPM). Solo para evaluaciones; sin uso no cuesta. Se borra con `az group delete -n rg-abastalia-dev`.
- Producción no cambió: `oai-abastalia-prod-…` sigue con el despliegue `chat` y sin claves (solo la identidad de la API).
