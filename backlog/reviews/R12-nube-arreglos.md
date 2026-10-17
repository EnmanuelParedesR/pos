# R12 — Arreglos de la nube (rama `r12-cloud`)

Hallazgos verificados de la revisión V1 y de la auditoría Ponytail, para la API de la nube, el panel, el despliegue, el instalador y la subida de la tienda. Cada punto: qué se arregló, cómo y con qué prueba.

## Seguridad

| # | Hallazgo | Arreglo | Prueba |
|---|---|---|---|
| 1 | La autenticación del nodo leía y hasheaba el cuerpo entero (hasta 1 GB) antes de comprobar la firma; descompresión antes de autenticar; límite por PC después | `NodeGate` antes de descomprimir y autenticar: por IP, `Nodes:ConcurrentPerIp` (8) y `Nodes:RequestsPerMinutePerIp` (300); exige Content-Length (411), lo compara con el límite de la ruta (413, con el mensaje de respaldo) y rechaza `Content-Encoding` en el respaldo (415). El nodo desconocido o revocado se rechaza antes de leer el cuerpo. La tienda manda el hash firmado en `X-Body-Sha256`: se verifica la firma primero y después se hashea el cuerpo; sin cabecera (tiendas viejas) sigue como antes, salvo en el respaldo, donde es obligatoria | `R12BackupTests` (413 antes del nodo, 415, cabecera obligatoria, cuerpo cambiado), `R12LimitTests` (429 por IP con Retry-After); `FakeCloud.SignedByNode` exige la cabecera |
| 2 | Los códigos 2FA equivocados en `/manage/2fa` no contaban | Cuentan en `LoginThrottle` (5 en 15 min por cuenta), tope diario `Auth:TwoFactorFailuresPerDay` (20) y `AccessFailedAsync`; luego 429 `Auth.TooManyCodes` | `R12TwoFactorTests.R12_2_…` |
| 3 | Códigos de respaldo y clave del autenticador en claro | `CloudUserStore`: los códigos se guardan como `sha256:…` (idempotente) y la clave como `dp:` + Data Protection. Sin migración: un código viejo en claro sirve una vez y el resto queda en hash; una clave vieja se lee tal cual. Un hash tecleado como código no sirve | `R12_3_…` |
| 4 | Un código TOTP servía otra vez dentro de su ventana | `OneTimeAuthenticatorTokenProvider`: guarda el último paso usado (token de Identity, sin migración) y rechaza ≤; la clave nueva reinicia la cuenta. El guardado usa el sello de concurrencia | `R12_4_…`; `Totp.Code` de las pruebas usa el paso siguiente |
| 5 | `reset-2fa` sin motivo ni aviso | Exige `--reason` (va a la auditoría), `--force` para cuentas del equipo, y avisa por correo a la cuenta | `R12_5_…`, `TwoFactorTests` |
| 6 | Regex del nombre con `$`; retención confiaba en la fecha del nombre; descarga sin tope | `\z`; nombre con fecha > ahora + 1 día = `Backup.InvalidName`; enlaces de descarga `CloudBackup:DownloadsPerHourPerUser` (10) por hora | `R12_6_…` (dos pruebas) |

## Código

| # | Hallazgo | Arreglo | Prueba |
|---|---|---|---|
| 7 | El panel ocultaba "Respaldos en la nube" sin cajas (justo tras quitar una PC robada) | Se muestra a todo Dueño | `StoresPage.test.tsx` R12 #7 |
| 8 | La regla de 60 días borraba el blob y la fila quedaba (descarga 500) | Archivo ausente → se borra la fila y `Backup.NotFound`; nunca se poda el más nuevo (`KeepDaily` ≥ 1); regla del contenedor a 400 días | `R12_8_…` |
| 9 | Cuota gastada antes de guardar; orden de borrado; 429 del respaldo como rechazo; Retry-After 0; métrica `ecfRelay`; descarga de 1 GB en memoria | Se devuelve la cuota si falla el guardado; filas primero y luego archivos; 429 con Retry-After = reintentar; Retry-After mínimo 1 s y nunca menos que el intervalo; métrica `ecfLive` (eCF o Dgii); el panel pide un enlace de 2 minutos (`POST …/link` → `GET /api/backups/download/{token}`) | `R12_9_…`, `CloudBackupTests`, `SyncTests` (Retry-After), `ProductMetricsTests`, `StoresPage.test.tsx` |
| 10 | Recomendaciones: código guardado en el checkout sin la ventana de 14 días; precios mensuales al anual; Reinstate sucio; monto combinado; prueba fija de 14 días; licencias sin negocio | Ventana también en el checkout; `yearly ? annual : monthly`; Reinstate limpia descuento, suscripción y fechas; la tarjeta muestra el total combinado; `LicenseCheck` da `ReferredTrialDays` al recomendado; licencias sin negocio emitidas antes de TK-268 (16/10/2026) cuentan hasta vencer (transición) | `ReferralTests` (3 nuevas), `BillingPage.test.tsx`, `ReferralCards.test.tsx`, `R11SecurityTests` |
| 11 | La lectura de facturas gastaba mes y día aunque fallara el modelo | Se devuelven ambos si el modelo falla | `R12_11_…` |
| 12 | Panel: lookbehind (Safari < 16.4), recarga infinita, código 2FA con espacios, pregunta pendiente repetida, tema en el render, texto de "Cambiar de teléfono", caché de confirmación con fallos, código en la barra, canonical en 404, CSP del logo, `sharp` | Grupo capturado (el build no tiene `(?<`); recarga una vez cada 10 s; espacios fuera y respaldo en mayúsculas (panel y API); la pregunta se borra al enviarla; tema "system" y luego el guardado; aviso claro y aviso de sesiones cerradas con "Entrar de nuevo ahora"; el fallo no queda en caché y 429/5xx dicen "ábralo otra vez"; `replaceState` quita `#…`; el 404 quita el canonical; `img-src` con el origen de la API; `sharp` 0.35.5 exacto | `reload.test.ts`, `prerender.test.tsx` (tema guardado, 404), `SignIn.test.tsx`, `EmailLinkPages.test.tsx`, `swa-config.test.mjs` |
| 13 | `-AllowUnsignedUpdates` en cualquier canal | Solo con `-Channel pilot` | `StoreSettingsScriptTests.R12_13_…` |

## Auditoría Ponytail

| # | Hallazgo | Arreglo | Prueba |
|---|---|---|---|
| A | Base que crece | `Archive:HotDays` 7, `KeepApplied` 7 días, `databaseMaxGigabytes` 250 (parámetro; ver costo en `docs/despliegue-azure.md`). Solo el archivo frío sirve XML viejo | `InboxPurgeTests` |
| B | Una sola instancia | Plan B1 con `capacity: 1`; lista de lo que vive en memoria en `docs/despliegue-azure.md`; un choque de clave de un evento ya aplicado no cuenta como fallo | (guarda barata, sin prueba) |
| C | Acuses tardíos bloqueados por 50 filas | Solo negocios con certificado activo y firmante en línea, paginado por Id | `FeReceiverTests.R12_C_…` |
| D | Productos más vendidos y ventas por día | Tope de 92 días; `ToLookup` una vez | `PanelTests.R12_D_…` |
| E | Eventos agotados nunca se reintentan | Al arrancar, `RetryFailedAsync` pone `Attempts = 0` | `InboxPurgeTests.R12_E_…` |
| F | Capacidad de OpenAI | `openAiCapacity` 50 (parámetro y `main.parameters.json`) | — |
| G | Métricas recorrían todas las ventas | Se arman desde `DailyUsage` | `PlatformTests`, `ProductMetricsTests` |
| H | Alertas en cada lote | Como mucho cada `Alerts:MinutesBetweenRuns` (5) | `AlertsTests` (0 en pruebas) |
| I | `custom_data.tenantId` sin verificar | Primero la suscripción conocida; si no, el id solo con `tenantSig` (HMAC con el secreto del webhook) | `BillingTests.R12_I_…` |
| J | Código muerto | Fuera `IWhatsAppSender`, `BackfillOwnersAsync`, su prueba y su línea del ADR-0008 | — |

## Diseño (regla del dueño)

Capturas de Tiendas (escritorio y teléfono), Seguridad y Facturación con una API simulada. Arreglado: el riesgo de "Cambiar de teléfono" pasó a un aviso ámbar con título corto, y el aviso de sesiones cerradas dice qué pasa con esta sesión. Sin hallazgos altos en Tiendas ni Facturación.

## Verificación

Build 0 advertencias; `dotnet test Abastalia.slnx` en verde; panel `tsc -b`, `vitest run` y `npm run build` (sin `(?<` en `dist`); `dotnet ef migrations has-pending-model-changes` sin cambios.
