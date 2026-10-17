# R12 — arreglos en la tienda (rama r12-store)

Hallazgos verificados de la revisión V1 y de la auditoría Ponytail que tocan la tienda. Cada uno: qué se arregló, cómo y con qué prueba.

## Reloj y licencia (`Settings/License.cs`)

| # | Arreglo | Prueba |
|---|---|---|
| 1 ALTO | `LicenseClock.Observe` sube la hora conocida y reinicia el contador de encendido: el intervalo ya no se cuenta dos veces (tienda sin nube encendida toda la noche). | `LicenseClockTests.An_outside_time_ahead_of_the_clock_counts_the_interval_once` |
| 2 MEDIO | La última hora vista y su contador solo cambian cuando la hora cambia: atrasar el reloj 4 min cada 4 min ya no congela la licencia. | `A_clock_set_back_a_little_again_and_again_does_not_freeze_the_time` (y `Small_corrections…` sigue verde) |
| 3 MEDIO | `LicenseClock.Correct`: la hora de la DGII (cada respuesta nueva) baja el reloj si iba más de 1 día adelantado, y lo sube si iba atrás. La nube aún no manda una hora firmada (pendiente, ver abajo). | `A_trusted_time_brings_back_down_a_clock_far_ahead_and_raises_one_behind` |
| 4 BAJO | La prueba empieza con `license.Time.UtcNow`, no con la hora de la PC. | `TrialClockTests` |
| 5 BAJO | Copia sellada (HMAC con el secreto `license-clock`) de la última hora en la carpeta de llaves (`license-clock.seen`); se usa como piso junto con la base, la auditoría y la licencia: borrar o romper el sello en la base no da nada. | `LicenseClockCopyTests` |
| 6 MEDIO | `PUT /api/settings/license` rechaza una licencia emitida antes que la más nueva instalada (`License.Older`). La más nueva y si era bloqueo quedan selladas en `license-issued.seen`: borrar `license.jws` o poner una vieja no quita un bloqueo. | `OlderLicenseTests` |
| 7 BAJO | La tienda ya no acepta `trialDays` suelto: solo un `TrialGrant` firmado (JWS `typ: abastalia-trial`, `{tenantId, days, issuedUtc}`) para su negocio. **Falta el lado nube** (ver abajo). | `ReferredTrialTests` (sin firma, firma ajena, otro negocio y licencia como permiso: ignorados) |
| 8 BAJO | En el diálogo "Hay una venta sin cobrar" (también sobre la pantalla de bloqueo) hay **Comprobar el último cobro**: si se guardó, vacía la venta; si no, deja ponerla en espera o anularla. | bUnit `R12_an_uncertain_checkout_is_resolved_from_the_block_screen` (guardada y no guardada) |
| 9 BAJO | Mensaje por causa: vencida, terminó la prueba, suspendida por Abastalia (servidor `BlockedError`, `blockReason` en `/api/settings/features`, caja, Resumen y nota). | bUnit `R12_the_block_screen_says_why`, `TrialTests` (texto del 403), `OlderLicenseTests` |
| A (auditoría) | `LicenseData.Rnc`: se rechaza al instalar y se ignora al arrancar si no es el RNC del negocio; sin RNC se acepta hasta vencer. `docs/licencias.md` pide `-Rnc` al script. | `LicenseRncTests` |

## Cajeros (`Identity`)

| # | Arreglo | Prueba |
|---|---|---|
| 10 MEDIO | Cuenta todo usuario activo que puede vender (rol efectivo con `sales.create` o `register.open`), Administradores incluidos, menos uno (el dueño). Crear o reactivar un Administrador pasa por `RoomForAsync`. | `CashierSeatsTests` (segundo Administrador → `CashierLimitReached`) |
| D | Al restaurar un respaldo de otra tienda, el instalador que toma el usuario con su mismo código conserva también su rol. | `RestoreForeignUsersTests` |
| F | Aprobación de supervisor: el PIN se verifica igual (mismo tiempo) aunque la persona no tenga el permiso, sin contarle intentos. | (sin prueba: tiempo constante) |

## Ventas, clientes, compras

| # | Arreglo | Prueba |
|---|---|---|
| 11 ALTO | El recibo de la venta que el cajero acaba de cobrar (10 min) muestra pagos y cambio: la venta incierta recupera su cambio y la reimpresión sus pagos. Después vuelve a ocultarlos con el turno abierto. | `SecurityReviewTests.A2_…` |
| 12 MEDIO | Abono en caja y en Clientes: la referencia es obligatoria según `RequirePaymentReference`, como en el cobro. | bUnit `RegisterCustomerPaymentTests`, `AdminCustomersTests` |
| 13 MEDIO | La nube con 403 o 5xx da `Purchasing.AiUnavailable`, que detiene el lote (también `Purchasing.TooManyDrafts`). | `SupplierInvoiceBatchTests`, bUnit `R12_the_cloud_AI_unavailable_or_too_many_drafts_stops_the_batch` |
| 14 BAJO | El mismo archivo otra vez abre su borrador sin leerlo de nuevo (se compara el contenido con los adjuntos de borradores; no hizo falta clave del cliente ni migración). | `SupplierInvoiceBatchTests` |
| 15 BAJO | Logo: si falla la descarga de una versión nueva, no se muestra el viejo. | bUnit `R12_a_new_logo_version_that_cannot_be_fetched…` |
| 16 BAJO | Logo: tope de 6 MP antes de decodificar (`Settings.LogoTooLarge`). | `LogoTests.R12_a_small_file_with_a_huge_canvas…` |
| 17 BAJO | Borradores desde facturas: 20 por minuto por persona (`AiBusy`) y 200 sin recibir (`TooManyDrafts`), antes de llamar a la nube. | `SupplierInvoiceBatchTests` |

## Respaldos, servidor, hardware

| # | Arreglo | Prueba |
|---|---|---|
| B | Restaurar descifra primero (frase equivocada: no se hace ningún respaldo previo), luego respalda "antes-de-restaurar" y restaura. Esos respaldos no cuentan ni se borran en la poda diaria. | `RestoreBackupTests` |
| C | Un `SemaphoreSlim(1)` estático para respaldar y restaurar (comparten la carpeta `work`). La prueba de restaurar ahora corre también en SQL Server de esta PC (SQL Express → `RestoreSqlServerAsync`). | `BackupGateTests`, `RestoreBackupTests` en SQL Express |
| E | `/health` solo dice que el servidor responde (como la nube); las pruebas de estado leen `/api/settings/health`. | `PlatformTests`, `AuditTests`, `BackupOperationTests` |
| G | Toledo: trama en blanco → sin lectura (no `IndexOutOfRange`). | `ScaleProtocolTests` |
| H | `decrypt-backup` borra la salida parcial ante cualquier error. | (sin prueba: comando de consola) |

## Pendiente del lado nube (otro agente)

✅ Hecho en TK-308 (7, A y 3; ver `docs/licencias.md` → Licencias de la nube). Lo que se pedía:

- **7:** `POST /api/nodes/license` debe mandar `trial`: `Jws.Sign(new TrialGrant(tenantId, referrals.ReferredTrialDays, now), …, Jws.TrialType)` firmado con la llave de licencias (Key Vault: `Jws.SigningInput(grant, Jws.TrialType)` + `Compose`), en lugar de `trialDays`. Hasta entonces la prueba de 30 días por referido no llega a la tienda.
- **A:** `CloudLicenses.IssueAsync` debe poner `Rnc` en la licencia; hoy la nube solo guarda `RncSet` del latido, así que necesita recibir el RNC (latido o registro de la tienda).
- **3:** una hora firmada de la nube en la respuesta de licencia permitiría corregir el reloj de tiendas que no hablan con la DGII.
