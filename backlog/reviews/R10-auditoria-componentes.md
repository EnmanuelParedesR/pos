# R10 · Auditoría de componentes (TK-238 a TK-243)

Alcance: `1310f9e..7426c11`, solo firma de nodos (TK-241), ambiente DGII en pantalla (TK-238), respaldos y restauración, instalador y actualizaciones (TK-242), acompañamiento (TK-233/234) y compra desde e-CF recibido y adjuntos (TK-240). Solo lectura. 12 hallazgos: 1 🔴, 5 🟡, 6 🟢.

Comprobado sin problema: la firma del nodo cubre `nodeId.segundos.nonce.MÉTODO ruta?consulta` igual en los dos lados, verifica antes de gastar el nonce y rechaza nodos revocados; el encabezado `Date` solo corre la marca de la firma, no el reloj de Windows; la llave del nodo está protegida con DPAPI de máquina en `keys`; restaurar no permite recorrer rutas y conserva el ambiente; el acompañamiento toma tenant y tienda de la identidad del nodo; los adjuntos limitan tipo, tamaño y nombre y se descargan como archivo; el XML se lee sin DTD.

## Alta

1. 🔴 **Cualquier usuario de Windows de la PC principal puede hacer que SYSTEM ejecute lo que quiera** (`deploy/installer/update-service.ps1:14-27`, `abastalia.iss:68`, `PrepareService.cs:19-21`). La tarea horaria corre como SYSTEM y lee `C:\ProgramData\Abastalia\update.json`, que cualquier usuario local puede escribir (hereda de `ProgramData`). Acepta cualquier firma Authenticode válida, `Version` sin limpiar entra en la ruta (`..\`), y `-AllowUnsigned` no exige firma. **Arreglo:** cerrar `ProgramData\Abastalia` (ver 6); validar `Version` (`^\d+\.\d+\.\d+$`) y que `SetupUrl` empiece con el `FeedUrl` instalado; exigir la huella del certificado de Abastalia o el SHA-256 publicado en el feed; descargar a una carpeta de SYSTEM.

## Media

2. 🟡 **Un comprobante emitido justo al cambiar de ambiente se envía al ambiente nuevo** (`EcfSubmissionQueue.cs:34-36,50`, `FiscalMode.cs:94,124-128,200-216`). **Arreglo:** la cola y las anulaciones usan el cliente del ambiente de cada documento (`provider.For(d.Environment)`); `Simulated` nunca se envía.
3. 🟡 **Una tienda en producción puede volver a Simulado con un clic** (`FiscalMode.cs:129`). **Arreglo:** con documentos `eCF` ya emitidos, bajar de producción responde 409 salvo con una bandera de soporte.
4. 🟡 **El servidor nunca se actualiza solo** (`update-service.ps1:19`, `abastalia.iss:43`): `CheckedUtc` se interpreta como hora local en PowerShell 5.1 y siempre parece viejo; sin firma, falla cada hora en silencio. **Arreglo:** `[DateTimeOffset]::Parse(...).UtcDateTime`; verificación por SHA-256 del feed mientras no haya certificado; errores al registro de eventos.
5. 🟡 **La ventana del secreto compartido no tiene salida** (`Nodes.cs:45,179-181`, `SyncModule.cs:113-115`). **Arreglo:** `POST /api/nodes/rekey` autenticado con el secreto que guarda la llave pública y borra el secreto; el `CloudUploader` lo llama una vez; el panel muestra qué nodos siguen con secreto; `Nodes:AcceptSharedSecret` explícito.
6. 🟡 **El `.bak` sin cifrar y los logs quedan legibles para cualquier usuario local** (`PrepareService.cs:19-21,50-51`, `Backups.cs:95-106`). **Arreglo:** ACL protegida en todo `ProgramData\Abastalia` (SYSTEM, Administradores, el servicio; la cuenta de SQL solo en `work`).

## Baja

7. 🟢 El cuerpo no va firmado y los nonces se pierden al reiniciar: agregar `SHA-256(body)` al texto firmado (`Nodes.cs:98-99,137-147`).
8. 🟢 El instalador ejecuta como administrador SQL Express y WebView2 descargados sin verificar la firma de Microsoft (`abastalia.iss:125-150`).
9. 🟢 "Borrar datos" no deja un último respaldo fuera de la PC (`remove-data.ps1:46,55`).
10. 🟢 `CopyTo` acepta cualquier ruta UNC y la propia carpeta de respaldos (`Backups.cs:452-453,122-131`).
11. 🟢 El tipo del adjunto no se compara con sus bytes y reemplazarlo borra el anterior (`PurchaseAttachment.cs:29-31,47-54`).
12. 🟢 En el acompañamiento, un `PUT` con fecha nula borra la cita, no se validan las fechas, y no hay índice único de solicitud abierta por tienda (`Accompaniment.cs:139-141`).

## Estado de las correcciones

TK-244 (issue #214):

1. ✅ `update-service.ps1` exige `Version` `x.y.z`, que `SetupUrl` sea exactamente `<FeedUrl instalado>/AbastaliaSetup-<versión>.exe` (el `FeedUrl` sale del `appsettings.json` instalado, no de `update.json`), y la firma de un certificado de `Updates:SignerThumbprints` o el SHA-256 que el servicio bajó del feed por HTTPS (`AbastaliaSetup-<versión>.exe.sha256`, lo genera `build.ps1`). Sin `-AllowUnsigned`. Descarga a `ProgramData\Abastalia\updates` (solo SYSTEM, recreada cada vez). `update.json` queda protegido por 6. Pruebas: `UpdateServiceScriptTests` (marcador con `Sha256`, el script rechaza versión, URL y falta de verificación).
2. ✅ La cola y las anulaciones usan `provider.For(d.Environment)`; `Simulated` va al proveedor simulado y nunca sale. Prueba: `EcfSubmissionEnvironmentTests`.
3. ✅ Salir de producción con `eCF` emitidos responde 409 (`Fiscal.ModeBlocked`) salvo con `Fiscal:AllowLeavingProduction=true`. Prueba en `StoreOperationsTests`.
4. ✅ `CheckedUtc` con `[DateTimeOffset]::Parse(...).UtcDateTime`; verificación por SHA-256 (1); errores al registro de eventos (Aplicación, origen `Abastalia`).
5. ✅ `POST /api/nodes/rekey { publicKey }` (solo con el secreto) guarda la llave; el secreto se borra en la primera llamada firmada del nodo, así una respuesta perdida no deja la tienda fuera. El `CloudUploader` lo llama mientras la credencial no sea de llave. "Tiendas" marca "Con clave compartida". `Nodes:AcceptSharedSecret: true` explícito. Pruebas: `NodeKeyTests`, `SyncTests`.
6. ✅ `prepare-service` pone ACL protegida en todo `ProgramData\Abastalia` (SYSTEM y Administradores control total, el servicio modificar); la cuenta de SQL solo en `backups\work`; `keys` igual que antes.
7. ✅ El texto firmado termina en `Base64Url(SHA-256(cuerpo))` en los dos lados (los nonces en memoria siguen como `shortcut:`).
8. ✅ `abastalia.iss` (`SignedByMicrosoft`): antes de ejecutar WebView2 o SQL Express descargados, `Get-AuthenticodeSignature` debe dar `Valid` y un firmante con `O=Microsoft Corporation`; si no, mensaje y la instalación se detiene.
9. ✅ `remove-data.ps1` copia primero el último `.abastalia-backup` (cifrado, se abre con la frase de los respaldos) a `-BackupTo` (el instalador pasa el Escritorio), carpeta "Abastalia - ultimo respaldo", fuera de `ProgramData`; sin respaldo o si la copia falla sale con 2 sin borrar nada, y el desinstalador pregunta si borrar sin respaldo (`-NoFinalBackup`). Al terminar dice dónde quedó. No hay comando de respaldo fuera del servicio, por eso es el último que hizo el servicio.
10. ✅ `CopyTo` dentro de la carpeta de respaldos o de `ProgramData\Abastalia` responde `Backup.CopyInsideData`; la pantalla explica los permisos de una carpeta compartida.
11. ✅ El tipo declarado se compara con los primeros bytes (`PurchaseFile.Matches`: `%PDF-`, PNG, JPEG, `RIFF…WEBP`, XML que empieza con `<`); si no coincide, `Purchasing.AttachmentTypeMismatch`. Reemplazar exige `?replace=true` (si no, 409 `Purchasing.AttachmentExists`; la pantalla de Compras lo manda porque el dueño eligió el archivo) y la auditoría registra `Replaced`. Prueba en `SupplierEcfPurchaseTests`.
12. ✅ El `PUT` solo cambia las fechas que trae, exige citas futuras y la segunda después de la primera, e índice único filtrado de una solicitud abierta por tienda (migración `AccompanimentOneOpen`; dos toques a la vez devuelven la misma).
