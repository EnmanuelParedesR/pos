# Licencias de Abastalia (TK-242)

Cómo es una licencia, cómo la emite el personal de Abastalia y cómo se instala en la tienda. El código está en `src/Modules/Settings/Abastalia.Settings/License.cs` y `src/BuildingBlocks/Abastalia.BuildingBlocks/Security/License.cs`.

## Formato

Una licencia es un **JWS compacto** (`encabezado.cuerpo.firma`, base64url) firmado con **ES256** (ECDSA P-256 + SHA-256). El encabezado es `{"alg":"ES256","typ":"JWT"}` y el cuerpo es el JSON de `LicenseData` (nombres en camelCase):

| Campo | Qué es |
|---|---|
| `store` | Nombre de la tienda (como sale en el contrato) |
| `plan` | Nombre del plan (`Esencial`, `Estándar`, `Pro`; antes `Varias sucursales`). `limits.Registers` 0 = solo la PC principal (Esencial) |
| `features` | Funciones pagas: `ButcherModule`, `WholesaleModule`, `CloudBackup`, `AiAssistant`, `SupplierInvoiceReading` (servicio adicional de lectura de facturas con IA, ver `docs/cuotas.md`) |
| `limits` | Límites por nombre: `Registers` (cajas además de la PC principal) y, desde TK-277, `Cashiers` (cajeros habilitados: usuarios activos que pueden vender o abrir turno; el Administrador no cuenta). Sin `Cashiers`, la tienda no limita los cajeros |
| `issuedUtc`, `expiresUtc` | Emisión y vencimiento, en UTC |
| `tenantId` | Opcional. Id del negocio en la nube (consola de la plataforma). La tienda no lo mira; la nube solo deja que la licencia active el lector de facturas con IA de ese negocio (R11 B-Low8, TK-268). Sin él, la licencia no cuenta en la nube (el negocio usa su suscripción o la prueba) |
| `blocked` | Opcional (TK-279). `true` = Abastalia bloqueó el negocio desde la consola: la tienda queda **Bloqueada** mientras sea su licencia instalada |
| `blockAfterDays` | Opcional (TK-279). Días después de `expiresUtc` hasta **Bloqueada**; sin él, 30. Solo lo cambia una licencia firmada, nunca un ajuste local |
| `rnc` | R12. RNC del negocio (con o sin guiones). La tienda rechaza al instalar, y descarta al arrancar, una licencia cuyo RNC no es el de Configuración → Negocio ("Esta licencia es de otro negocio"). Sin `rnc` (licencias emitidas antes de R12) se acepta hasta que venza: **toda licencia nueva debe llevarlo** |

Ejemplo de cuerpo:

```json
{"store":"Carnicería La Esquina","plan":"Pro","features":["ButcherModule","CloudBackup"],"limits":{"Registers":3,"Cashiers":8},"issuedUtc":"2026-10-16T00:00:00Z","expiresUtc":"2027-10-16T00:00:00Z"}
```

**Cajeros (TK-277):** `Cashiers` = los del plan (Esencial 2, Estándar 4, Pro 8; Varias sucursales 12) + 1 por cada cajero adicional + 2 por cada caja adicional + 4 por cada sucursal adicional. La tienda solo impide **habilitar** a uno más (crear, activar, darle un rol que vende); nunca entrar, vender ni el e-CF. En la prueba no hay tope; en Restringido, 2.

La tienda solo tiene **llaves públicas** (dentro de `LicenseService`): la del dueño (licencias fuera de línea) y, desde TK-249, la de la nube (`AbastaliaCloudPublicKey`, licencias de una suscripción pagada con Paddle; vacía hasta crear la llave en Key Vault, ver `docs/cobro-paddle.md`). Acepta una licencia firmada por cualquiera de las dos y en producción no acepta otra. Las llaves privadas **nunca** entran al repositorio ni al producto.

## Estados

- **Prueba:** sin licencia, 14 días desde la primera venta, todo encendido. 30 días si la nube dice que el negocio llegó recomendado (TK-276; la tienda lo guarda en `settings.Business.TrialDays`).
- **Activa:** licencia válida y sin vencer.
- **Gracia:** venció hace menos de 15 días; todo sigue igual (tiendas sin internet).
- **Restringido:** terminó la prueba o la gracia: funciones pagas apagadas y una caja además de la PC principal (2 cajeros). Todavía se vende y se factura; el aviso dice el día en que se detiene la venta.
- **Bloqueada (TK-279, cambia la regla de TK-220 "Restringido vende para siempre"):** 30 días (o `blockAfterDays`) después del vencimiento, 30 días después del fin de la prueba, o al instante con una licencia `blocked` de la nube. **No** se puede: vender (`POST /api/sales`), crear encargos, abrir turnos, anular o devolver (la nota de crédito es un e-CF nuevo). Responde **403 `License.Blocked`** "Su licencia está vencida…". **Sí** se puede (obligaciones fiscales y los datos son del cliente): entrar, cerrar el turno abierto, enviar a la DGII los e-CF ya emitidos o en cola (y reemplazar un rechazado), ver el historial y los reportes, exportar el 606/607/608, respaldar y restaurar, e instalar una licencia (Configuración → Licencia o la que trae sola la nube). La caja muestra a pantalla completa "Su licencia está vencida" con cómo pagar y los botones **Activar licencia** (dueño) y **Cerrar el turno**; el dueño ve la alerta en el Resumen y en Configuración → Licencia. Ningún dato se pierde y una licencia nueva lo levanta al momento.

Línea de tiempo con una licencia que vence el día 0: días 0–15 Gracia, 15–30 Restringido, desde el 30 Bloqueada. Con la prueba (14 días, o 30 si llegó recomendado): Prueba, luego Restringido 30 días, luego Bloqueada. Sin ninguna venta la prueba no empieza, así que nunca se bloquea una tienda que no ha vendido.

### La hora de la PC (TK-279)

La licencia firmada vence sola, aunque la tienda nunca se conecte. Para que atrasar el reloj de Windows no la estire, la licencia corre con su propio reloj (`LicenseClock`): guarda la hora **más tardía** que la tienda vio y nunca va hacia atrás. Esa hora sale de: el reloj de la PC en cada consulta, el último registro de auditoría (encadenado, R11 A18), la fecha de emisión de la licencia (firmada) y la hora de la DGII en su última respuesta (encabezado HTTP `Date`, TK-156). Se guarda cada 10 minutos y al cerrar en `settings.Business.ClockSeenUtc` con un HMAC (`ClockSeenSeal`) hecho con un secreto del servidor que no está en la base de datos (carpeta de llaves, como el PIN pepper de R11 A3): cambiar la fecha a mano en la base de datos no sirve (se ignora y quedan los otros pisos).

Si la PC está atrasada más de 5 minutos respecto de esa hora, la licencia cuenta desde ella y sigue avanzando con el contador de tiempo encendido de la máquina; la caja y el dueño ven "La hora de la PC está atrasada". Atajo: el tiempo con la PC apagada y el reloj atrasado no se cuenta hasta que vuelva la hora correcta (o hable con la DGII). Si una PC tuvo la hora **adelantada** y luego se corrige, la hora de la DGII (cuando responde) baja el reloj de la licencia si iba más de un día por delante (R12); una tienda que nunca habla con la DGII sigue contando desde la hora adelantada hasta que la nube envíe una hora firmada (pendiente). R12: la hora guardada tiene además una copia sellada en la carpeta de llaves (`license-clock.seen`), así que borrar la de la base de datos no sirve, y la prueba empieza con la hora de la licencia, no con la de la PC.

**Licencia más antigua (R12):** la tienda guarda, sellada en la carpeta de llaves (`license-issued.seen`), la fecha de emisión de la licencia más nueva que instaló y si era un bloqueo. Una licencia emitida antes se rechaza ("Esta licencia es más antigua que la última instalada"), y borrar `license.jws` o poner una vieja en su lugar no quita un bloqueo de la consola.

**Mensaje por causa (R12):** bloqueada por vencimiento ("Su licencia está vencida"), por fin de la prueba ("Terminó la prueba gratis") o por Abastalia ("Abastalia suspendió el servicio de su negocio"); el código sigue siendo `License.Blocked` y `/api/settings/features` trae `blockReason` (`Expired`, `Trial`, `Suspended`).

## Emitir una licencia (personal de Abastalia)

La llave privada y el script están **fuera del repositorio**, en la PC del dueño: `%USERPROFILE%\Abastalia-licencias\` con `issue-license.ps1`. Solo el dueño (o quien él autorice en esa PC) emite licencias.

1. Confirmar el pago y los datos: nombre de la tienda, plan, funciones, número de cajas, número de cajeros y fecha de vencimiento; si el negocio está conectado a la nube, su id (`tenantId`).
2. Ejecutar `issue-license.ps1` en esa carpeta con esos datos (ver la ayuda del propio script). Produce un archivo `.jws` (o el texto de la licencia). **TK-277:** el script necesita el parámetro **`-Cashiers <n>`**, que va a `limits.Cashiers` (como `-Registers` va a `limits.Registers`); el script vive fuera del repositorio y el dueño lo actualiza. Sin él la licencia no limita los cajeros. **R12:** también el parámetro **`-Rnc <rnc>`**, que va a `rnc` (obligatorio en licencias nuevas: la licencia solo sirve en el negocio con ese RNC).
3. Enviar a la tienda el archivo o el texto por el canal acordado (correo o WhatsApp). La licencia no es secreta: solo sirve para la tienda que nombra y no se puede cambiar sin invalidar la firma.
4. Anotar la licencia emitida (tienda, plan, vencimiento) en el registro de ventas.
5. En la consola de la plataforma (cliente → **Plan y consumo**), poner el mismo plan y los servicios adicionales (`SupplierInvoiceReading`): las cuotas mensuales de la nube salen de ahí, no de la licencia (`docs/cuotas.md`).

Si la llave privada se pierde o se filtra: generar un par nuevo, cambiar la llave pública en `LicenseService`, publicar una actualización y volver a emitir las licencias vigentes.

## Instalar una licencia (en la tienda)

En la app: **Configuración → Licencia**, elegir el archivo `.jws` o pegar el código y pulsar **Activar licencia** (`PUT /api/settings/license`). Queda guardada en `C:\ProgramData\Abastalia\license.jws` y aplica al instante, sin reinstalar ni perder ventas. Una licencia inválida o de otra llave se rechaza con "La licencia no es válida o no la firmó Abastalia."

## Licencias de la nube (TK-249)

Con el cobro en línea (`docs/cobro-paddle.md`), la nube firma la licencia de cada tienda con su propia llave de Key Vault según la suscripción (plan, adicionales y "pagado hasta" + 3 días). La tienda conectada la pide por el canal del nodo en cada latido y la instala sola si es más nueva que la instalada. La emisión fuera de línea de arriba sigue igual: gana la licencia emitida más tarde.

**Prueba de 30 días firmada (R12, seguridad B3):** la tienda ya no acepta el número `trialDays` suelto de la respuesta de `POST /api/nodes/license`. Solo alarga la prueba con el campo `trial`: un JWS con encabezado `{"alg":"ES256","typ":"abastalia-trial"}` y cuerpo `TrialGrant` (`{"tenantId":…,"days":30,"issuedUtc":…}`), firmado con la llave de licencias de la nube, para el negocio de esa tienda. El `typ` distinto impide usar una licencia como permiso de prueba y al revés.

**Bloquear desde la consola (TK-279):** cliente → tarjeta **Suscripción** → **Bloquear negocio** con motivo obligatorio (`PUT /api/platform/tenants/{id}/block`, `{ blocked, reason }`, auditado en `PlatformAudit`; el motivo queda en `TenantSubscriptions.BlockNote`). Desde ahí la nube entrega a **todas** las tiendas del negocio, aunque no tenga tiempo pagado, una licencia con `blocked: true` emitida en ese momento (más nueva que la instalada): la tienda se bloquea en su siguiente consulta (TK-287: con su próxima subida de datos, que trae la fecha del último cambio de la suscripción; si no sube nada, en su latido de 4 horas). Bloqueada, pregunta cada 5 minutos, así que el desbloqueo o el pago llegan en minutos. **Desbloquear negocio** (también con motivo) vuelve a la licencia de la suscripción. Un pago en Paddle **no** quita un bloqueo de la consola. Si el negocio no tiene tiempo pagado, al desbloquear ponga también un acuerdo ("pagado hasta"): sin él la nube no entrega licencia nueva y la tienda sigue con la bloqueada (o envíele una licencia fuera de línea, que gana por ser más nueva).
