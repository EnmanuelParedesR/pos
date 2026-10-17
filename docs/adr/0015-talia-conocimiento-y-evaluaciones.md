# ADR-0015: Talia sabe de Abastalia por una base en el repositorio, y se mide antes de cambiarla

- Fecha: 2026-10-17
- Estado: aceptada (TK-315, TK-347, TK-348)
- Relacionadas: `docs/talia.md` (plan completo), ADR-0008 (nube), ADR-0012 (Talia), `docs/cuotas.md`

## Contexto

Talia del panel solo conocía los datos del negocio y un mapa fijo del panel (`PanelHelp`): no sabía contestar «¿funciona sin internet?» ni «¿qué balanza sirve?». El dueño pidió además una Talia en el sitio para prospectos (TK-315), con un modelo barato pero bueno, y un arnés para afinarla. El costo tiene que seguir en centavos: el sitio es anónimo y cualquiera puede preguntar.

## Decisión

1. **La base de conocimiento son archivos Markdown en el repositorio** (`src/Hosts/Abastalia.Host.Cloud/Assistant/Knowledge`), compilados en la API: un tema por archivo, una respuesta por sección `##`, con `audience: public | panel`. El sitio nunca ve los artículos `panel`. Se revisa en PR como el código y las pruebas la vigilan (precios iguales al sitio, nada interno, ningún «fiado» en lo público, enlaces válidos).
2. **Búsqueda BM25 en memoria**, con normalización en español, como herramienta `search_knowledge` que el modelo llama cuando la necesita. Nada de embeddings ni índice externo por ahora: con ~40 artículos el recall@3 es 96 % sin costo ni latencia. El paso a búsqueda híbrida con embeddings está planeado con su señal de activación (`docs/talia.md` §5).
3. **Un solo bucle para las dos Talias** (`TaliaLoop`): reglas fijas primero (Azure las cachea), contexto del día después, máximo de vueltas con la última sin herramientas, limpieza de Markdown, uso de tokens detallado. El sitio usa `PublicAssistant`: solo la base pública, límite por IP y presupuesto diario global en memoria.
4. **Ajuste por configuración** (`Talia:Panel`, `Talia:Public`: despliegue, razonamiento, verbosidad), no por código.
5. **Ningún cambio de reglas, base o modelo sin evaluar**: pruebas deterministas en CI (incluido el recall de la búsqueda) y evaluaciones con modelos reales (`TaliaLiveEvals`, `deploy/azure/talia-evals.ps1`) contra un recurso de Azure aparte que acepta clave, con juez y costo por 1,000 preguntas.
6. **Telemetría sin preguntas**: una línea por respuesta con tokens, herramientas y versión de la base. La pregunta y la respuesta solo se guardan si la persona toca «Me sirvió / No me sirvió», y el chat lo dice.

## Consecuencias

- Lo que Talia sabe se cambia sin desplegar la API: `talia-knowledge-publish.ps1` sube la base a Blob después de sus pruebas y la API la toma en ≤ 10 min solo si pasa las mismas reglas (`TaliaKnowledge.Problems()`); si no, sigue con la última buena. Con Paddle encendido, los precios salen del catálogo. Un vigía diario avisa al personal cuando cambia una página de la DGII de la que depende un artículo; nunca edita la base solo. Todo dentro de la API (`BackgroundService`), sin Azure Functions (`docs/talia.md` §4).
- El presupuesto diario del sitio vive en memoria: con más de una instancia de la API se multiplica (`shortcut:` en `PublicAssistantBudget`).
- Los modelos `gpt-5.6-*` no usan herramientas con razonamiento en Chat Completions; probarlos con razonamiento pide migrar el cliente a la Responses API.
- Producción no recibe claves ni roles nuevos: las evaluaciones corren en `rg-abastalia-dev`.
