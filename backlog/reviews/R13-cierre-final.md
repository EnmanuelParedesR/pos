# R13 · Revisión final de V1 (17/10/2026)

Delta revisado: `git diff 8f3c50b..main` (56 commits, ~320 archivos: arreglos de R12, TK-293 a TK-309). Tres pasadas: seguridad, código y Ponytail. Cada hallazgo se comprobó leyendo el código. Cada arreglo es el cambio más chico posible y trae su prueba. Rama `r13-final`.

## 1. Seguridad

| Sev. | Dónde | Hallazgo | Arreglo · prueba |
|---|---|---|---|
| Baja | `Host.Cloud/Backups/StoreBackups.cs:255` | El enlace de descarga de un respaldo (2 min) servía varias veces. Su URL queda en el registro de peticiones (Serilog) y en el historial del navegador. | Ahora sirve una sola vez. Se reclama con un único `UPDATE` (`LastDownloadedUtc < emitido`), así que dos GET a la vez no pasan los dos. El vencimiento se guarda en ms. · `StoreBackupTests` (segundo uso = `Backup.LinkExpired`, un enlace nuevo funciona) |
| Prueba faltante | `BuildingBlocks/Security/License.cs` (`typ`) | La separación de claves ya estaba bien: licencia `JWT`, prueba `abastalia-trial`, hora `abastalia-time`. Faltaba probar el sentido inverso. | `CloudLicenseTests`: una `TrialGrant` o una `ServerTime` firmadas nunca verifican como licencia. Ya se probaba que una licencia no pasa por prueba ni por hora. |
| Prueba faltante | `BuildingBlocks/ShiftedClock.cs` | `Clock:Shift` está atado a `IsEnvironment("E2E")`. Producción lo ignora y Azure fija `ASPNETCORE_ENVIRONMENT=Production` (`main.bicep:399`). Faltaba la prueba. | `UnitTests/Platform/ShiftedClockTests` cubre Production, Development, Staging, otro nombre y E2E. |

### Sin hallazgos

- **Firma de la prueba gratis y de la hora.** El nonce es nuevo en cada consulta y queda atado al negocio, así que no se puede reutilizar. El `typ` va dentro de la parte firmada, así que no se cambia una cosa por otra. El JSON se serializa, no se arma a mano.
- **Puerta antes de autenticar (`NodeGate`).** Funciona igual que el manejador de nodos: comparación `Ordinal` de `Node `. Exige `Content-Length` y respeta el tope de cada ruta antes de leer el cuerpo. En la ruta nueva, la firma se revisa antes de hacer el hash. Un respaldo sin la cabecera se rechaza.
- **2FA.**
  - Los códigos de un paso ya usado se rechazan; dos peticiones con el mismo código chocan por el sello de concurrencia.
  - Límites por cuenta: 5 en 15 min y 20 al día, más el bloqueo de Identity.
  - Los códigos de respaldo se guardan como hash (escribir el hash no sirve).
  - La clave del autenticador va cifrada con Data Protection. En Linux, App Service guarda el anillo de claves en `/home`.
  - `reset-2fa` exige `--reason` y avisa al dueño.
- **Paddle.** El `tenantId` del navegador solo se acepta con su HMAC.
- **Aislamiento entre negocios.** Cada consulta nueva de la nube filtra por el `TenantId` del claim o del nodo.
- **Inyecciones.** No hay SQL crudo nuevo. No hay `dangerouslySetInnerHTML`; el único `MarkupString` usa textos fijos.
- **CSP.** Solo agrega el origen de la API a `img-src`.
- **Dependencias.** No hay nuevas: `sharp` quedó fijo en 0.35.5, solo para desarrollo.
- **Secretos.** No hay secretos en el código; las contraseñas que aparecen son de las pruebas.

## 2. Código

| Sev. | Dónde | Hallazgo | Arreglo · prueba |
|---|---|---|---|
| Media | `Client.UI/Components/Frame.razor:254` | El reinicio automático para actualizar solo miraba el carrito en pantalla. Una venta apartada para su cajero (en memoria, quizás con un cobro incierto y su `SaleId`) se perdía. | `IdleAsync` exige también `!Cart.HasSetAside`. · `RegisterAuditFixesTests` |
| Media | `Host.Cloud/Assistant/SupplierInvoiceReader.cs:143` | Una factura ilegible respondía 503. La tienda lo toma como "IA caída" y para todo el lote. | Ahora responde `Ai.Unreadable` con 422 y la tienda sigue con la siguiente. · `SupplierInvoiceReaderTests` (422) y `SupplierInvoiceBatchTests` (422 → `Purchasing.AiRefused`) |
| Media | `Fiscal/DgiiClient.cs:125` | Un RFCE rechazado sin `codigo` ni `estado` (`{"error": …}`) se leía como "no encontrado". Quedaba `Sent` y se consultaba para siempre, sin aviso. | Se trata como rechazo, igual que un e-CF. · `DgiiClientTests` |
| Media | Sitio · `LegalPages.tsx:261` | La política de privacidad decía "nada pasa de 60 días". R12 #8 subió la regla del contenedor a 400 días. | Texto corregido: los últimos respaldos se guardan hasta 400 días si la tienda deja de enviarlos. También el comentario de `StoreBackups.cs:73`. *Debe verlo el abogado con el resto (TK-302).* |
| Baja | `Fiscal/EcfSubmissionQueue.cs:174` | El archivo en la nube toma 20 documentos por pasada. Si hubiera 20 rechazados con 4xx, se repetirían siempre y los nuevos nunca se archivarían. | Un documento rechazado espera un día antes de volver a ofrecerse (en memoria). · `EcfArchiveTests` |
| Baja | `Inventory/Features/StockCounts.cs:138` | Un solo `UPDATE` de `NeedsCount` para todo el conteo: con 5,000 productos o más, SQL Server escala a un bloqueo de toda la tabla de stock hasta el commit. | Se hace en tandas de 1,000, ordenadas por producto. · `InventoryTests` (la bandera se limpia, haya diferencia o no). Corrió también en SQL Express. |
| Baja | `Settings/Backups.cs:281` | Los respaldos "antes de restaurar" no se borraban nunca, y cada uno es la base completa. | Se guardan los 3 más nuevos. · `BackupsTests` |
| Baja | `Host.Cloud/Fe/FeRemoteSigning.cs:134` | Los acuses tardíos no tienen tope por pasada. Si la PC de un negocio se apaga a mitad de la pasada, cada documento suyo espera el tiempo límite del broker. | Un negocio que falla una vez espera a la próxima pasada. Además se limpia el seguimiento de cambios en cada tanda. · Siguen pasando las pruebas de `FeReceiverTests`. No hay prueba propia porque montar el tiempo límite del broker es caro. |
| Baja | `Host.Cloud/Assistant/SupplierInvoiceReader.cs:130` | Si la IA fallaba cruzando la medianoche, la devolución caía en el día nuevo. | El día se toma una sola vez, antes de llamar al modelo. · `SupplierInvoiceReaderTests` (el reloj cruza la medianoche durante la llamada) |
| Baja | Panel · `SalesPage.tsx` | "Lo más vendido" lee como mucho los últimos 92 días (R12 D), pero no lo decía. | Si el rango es más largo, la tarjeta dice "· últimos 92 días". · `ShiftReportPage.test.tsx` |

### Revisado y aceptado sin cambio

- **Traspaso reintentado tras una respuesta incierta con el formulario editado** (`Transfers.razor:136`). No duplica: `AlreadyMovedAsync` busca por `ReferenceId` y tipo, no por almacén. Lo editado se ignora en silencio. Generar un id nuevo sería peor, porque podría mover dos veces.
- **"Ver más productos" con exactamente 50, 100… productos.** Cuesta un toque de más y luego el botón desaparece.
- **Código escaneado desconocido con letras.** Se busca como nombre y, si hay un solo resultado, entra. Que el nombre de un producto contenga un código de barras es improbable, y quien teclea un nombre gana con esto.
- **"Último contacto" con la DGII** (`EcfOperations.cs:60`). Cuenta cualquier respuesta del servidor de la DGII, incluso la de su firewall. Eso es literalmente "contacto"; se deja.
- **Instalador, tiempo límite de 60 min de SQL Setup** (`abastalia.iss:322`). Si se corta a medias, el reintento puede creer que la instancia ya existe. Hay que comprobarlo en una VM; no se puede hacer desde el código.

### Sin hallazgos

- **Migraciones.** Índices no únicos en Fiscal y Sales. La intercalación de Customers se aplica solo en SQL Server, como en Inventory. `StoreRnc` agrega una columna. Sqlite crea las tablas desde el modelo.
- **Tiempo.** `BusinessDay` (UTC−4) se usa de forma consistente.
- **Dinero.** Todo en `decimal` con `Money.Round`.
- **Claves de idempotencia.** Gaveta (el id es la PK y se reproduce bajo el bloqueo del turno), ajustes, abonos, carrito apartado con su `SaleId`.
- **Purga del outbox.** Se borra solo lo procesado y subido hace más de 90 días, en tandas.
- **Regresiones de R12.** Siguen pasando todas sus pruebas.

## 3. Ponytail

- **Duplicado justificado:** `ShiftedClock` está repetido en `Client.Web/Program.cs:35`. Ese host no referencia BuildingBlocks y es solo de desarrollo; agregar la referencia costaría más que las 3 líneas.
- **Código muerto quitado en el delta, bien quitado:** `ExpectedCashAsync`, `InvalidDenomination`, `BackfillOwnersAsync`, `Talia.razor` en Blazor y los `rename-map*.md`.
- **Código riesgoso sin prueba propia:** solo el salto por negocio de los acuses tardíos (ver arriba).

## Para el dueño

1. **La hora de la DGII baja el reloj de licencia** (`Settings/License.cs:271`, `LicenseClock.Correct`, de R12). Lo baja si va más de un día adelantado. Un dueño con permisos de administrador en su PC podría instalar un certificado raíz, interceptar `ecf.dgii.gov.do` con un proxy y poner una fecha vieja en la cabecera `Date`. Así alargaría una licencia vencida. Opciones:
   - (a) Dejarlo: cuesta parecido a parchear el binario.
   - (b) Que solo la hora firmada por la nube (`ServerTime`) pueda bajar el reloj y que la DGII solo lo suba. Una tienda sin nube que alguna vez arrancó con la fecha en el futuro quedaría bloqueada hasta que la conecten.
2. **Límites por IP de `NodeGate`:** 8 conexiones a la vez y 300 por minuto. Con el CGNAT de los proveedores móviles, varias tiendas pueden salir por la misma IP. Cada tienda hace unas 15–20 llamadas por minuto, así que el límite alcanza para unas 15 tiendas por IP, y un respaldo ocupa una conexión varios minutos. Un 429 solo retrasa la subida (la tienda respeta `Retry-After`). Los límites se cambian con `Nodes:ConcurrentPerIp` y `Nodes:RequestsPerMinutePerIp` en la configuración. Conviene mirar los 429 en el piloto.
3. **Instalador:** probar en una VM una instalación de SQL Express cortada por el tiempo límite y luego reintentada.

## Estado

- Compilación sin advertencias.
- `dotnet test Abastalia.slnx`: 937 correctas y 4 omitidas, dos corridas seguidas.
- Integración en SQL Express: 345 correctas y 2 omitidas.
- Panel: `tsc -b` limpio y 207/207 en vitest.
- `Abastalia.UiTests` (Release): 4 correctas; 2 omitidas (el video, solo con `ABASTALIA_VIDEO=1`).
- Revisión de diseño de los cambios visibles:
  - Texto de privacidad: es una frase más dentro de una lista que ya existía.
  - Nota de "Lo más vendido": cabe en `esc-card__aside` (13 px, `--text-3`, la cabecera se reparte en dos líneas si no cabe) sin colores nuevos ni tokens fijos. A 1024 px pasa a una segunda línea bajo el título, como las otras notas.
  - Sin hallazgos.
