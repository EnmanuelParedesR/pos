# Licencias de Abastalia (TK-242)

Cómo es una licencia, cómo la emite el personal de Abastalia y cómo se instala en la tienda. El código está en `src/Modules/Settings/Abastalia.Settings/License.cs` y `src/BuildingBlocks/Abastalia.BuildingBlocks/Security/License.cs`.

## Formato

Una licencia es un **JWS compacto** (`encabezado.cuerpo.firma`, base64url) firmado con **ES256** (ECDSA P-256 + SHA-256). El encabezado es `{"alg":"ES256","typ":"JWT"}` y el cuerpo es el JSON de `LicenseData` (nombres en camelCase):

| Campo | Qué es |
|---|---|
| `store` | Nombre de la tienda (como sale en el contrato) |
| `plan` | Nombre del plan (`Esencial`, `Estándar`, `Pro`; antes `Varias sucursales`). `limits.Registers` 0 = solo la PC principal (Esencial) |
| `features` | Funciones pagas: `ButcherModule`, `WholesaleModule`, `CloudBackup`, `AiAssistant`, `SupplierInvoiceReading` (servicio adicional de lectura de facturas con IA, ver `docs/cuotas.md`), `AssistantActions` (Talia propone acciones que el usuario confirma, TK-338; en todos los planes durante el piloto) |
| `limits` | Límites por nombre: `Registers` (cajas además de la PC principal) y, desde TK-277, `Cashiers` (cajeros habilitados: usuarios activos que pueden vender o abrir turno, también los Administradores salvo uno, el dueño; R12). Sin `Cashiers`, la tienda no limita los cajeros |
| `issuedUtc`, `expiresUtc` | Emisión y vencimiento, en UTC |
| `tenantId` | Opcional. Id del negocio en la nube (consola de la plataforma). La tienda no lo mira; la nube solo deja que la licencia active el lector de facturas con IA de ese negocio (R11 B-Low8, TK-268). Sin él, la licencia no cuenta en la nube (el negocio usa su suscripción o la prueba) |
| `blocked` | Opcional (TK-279). `true` = Abastalia bloqueó el negocio desde la consola: la tienda queda **Bloqueada** mientras sea su licencia instalada |
| `blockAfterDays` | Opcional (TK-279). Días después de `expiresUtc` hasta **Bloqueada**; sin él, 30. Solo lo cambia una licencia firmada, nunca un ajuste local |
| `rnc` | R12. RNC del negocio (con o sin guiones). La tienda rechaza al instalar, y descarta al arrancar, una licencia cuyo RNC no es el de Configuración → Negocio ("Esta licencia es de otro negocio"). Sin `rnc` (licencias emitidas antes de R12) se acepta hasta que venza: **toda licencia nueva debe llevarlo**. TK-368: desde que la licencia instalada lo nombra, el RNC del negocio ya no se cambia en la tienda (ver "RNC atado a la licencia") |
| `replacesRnc` | Opcional (TK-368). Soporte cambió el RNC del negocio: la tienda cuyo RNC es este instala la licencia y pasa al RNC de `rnc`. Es la única forma de cambiar un RNC que una licencia ya nombra |

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

### Instalar sin código: la prueba y sus abusos (TK-369)

**Así es a propósito.** La instalación no pide código, cuenta ni tarjeta: la prueba es sin fricción (`docs/competencia-glecf.md` §7) y dura 14 días **desde la primera venta real** (30 con un `TrialGrant` firmado de la nube, TK-276), con todo encendido y 2 cajas además de la PC principal. Después, Restringido 30 días (sigue vendiendo y facturando) y luego Bloqueada. No se vende la prueba: lo que la limita es el tiempo, y el tiempo corre con el reloj de la licencia (no se atrasa con el de Windows).

**Abusos revisados (17/10/2026):**

| Abuso | Antes | Ahora |
|---|---|---|
| Borrar o mover la fecha de la primera venta en la base (el dueño es administrador de la PC y de SQL Server), o darse más días (`TrialDays`) | Prueba nueva, o tantos días como escribiera | **Cerrado (TK-369):** el inicio y los días tienen copia sellada en la carpeta de llaves (`license-trial.seen`, HMAC con el secreto del servidor, como `license-clock.seen`). Gana el inicio más temprano y los días sellados; la base sola no cambia nada. Una tienda de antes (sin copia) se cree la base, con 90 días como máximo, y desde ahí queda sellada |
| Restaurar un respaldo de antes de la primera venta en la misma PC | Prueba nueva | **Cerrado:** misma copia sellada |
| Desinstalar con "borrar datos" y volver a instalar | Prueba nueva | **Abierto:** se borra todo (carpeta de llaves incluida), también las ventas, el inventario y los clientes. Es caro para quien lo hace cada 44 días; lo cierra el ticket propuesto E |
| Instalar en otra PC (o en varias) | Cada instalación es una tienda con su prueba | **Abierto, a propósito:** cada PC principal es una tienda con sus datos. Varias PC con el mismo RNC conectadas a la nube las vería el ticket E |
| Quedarse sin conexión a la nube | La prueba corre igual | **Bien:** el reloj de la licencia nunca va hacia atrás (TK-279) y la prueba vence sola sin internet |
| Vender siempre en el ambiente TesteCF o CerteCF de la DGII | Esas ventas no empiezan la prueba (R9 #12, para no gastarla en la certificación) | **Abierto:** una tienda que nunca pasa a producción nunca empieza la prueba. Pide configurar el proveedor DGII y un certificado: bajo. Ticket propuesto F |

**Lo que no se hizo sin el dueño: registrar el negocio para probar.** Pedir correo y RNC en la nube antes de la primera venta (o para emitir e-CF reales) cerraría casi todo lo abierto, porque la nube recordaría la prueba por RNC aunque se reinstale. El costo:

- **A favor:** la prueba queda atada al RNC del negocio (no se reinicia reinstalando ni en otra PC); Abastalia sabe quién está probando y puede ayudarle; el dueño ya tiene su cuenta del panel cuando compra.
- **En contra:** un paso más justo cuando el dueño quiere ver la caja funcionando; pide internet el día de la instalación (muchos colmados lo tienen inestable); pide el RNC antes de que confíe en el producto; se pierde el "instale y venda" que nos diferencia; y quien quiera abusar igual puede inventar otro correo (el RNC sí es difícil de inventar si va a emitir e-CF reales, porque el certificado digital lo nombra).
- **Término medio propuesto (ticket E):** no pedir nada al instalar; la **primera vez** que la tienda se conecte a la nube (o al emitir su primer e-CF real, que ya nombra el RNC) la nube recuerda el inicio de la prueba de ese RNC y la tienda adopta el más temprano (un `TrialGrant` firmado con el inicio). Así el que reinstala y se conecta sigue con su prueba vieja, y el que nunca se conecta pierde la nube, el respaldo en línea, el panel y Talia.

**Tickets propuestos (TK-369):**

- **E. La prueba por RNC en la nube.** La tienda manda en `POST /api/nodes/license` el inicio de su prueba; la nube guarda el más temprano por RNC (entre todas las cuentas) y responde un `TrialGrant` firmado con `startedUtc`; la tienda adopta el inicio más temprano (nunca uno más tardío). Opcional después: exigir la conexión para emitir e-CF en el ambiente `eCF` (decide el dueño, ver arriba).
- **F. Prueba que empieza aunque la tienda nunca salga de TesteCF/CerteCF.** Que la prueba empiece igual 30 días después de la primera venta de prueba, si para entonces no hubo una real.

### La hora de la PC (TK-279)

La licencia firmada vence sola, aunque la tienda nunca se conecte. Para que atrasar el reloj de Windows no la estire, la licencia corre con su propio reloj (`LicenseClock`): guarda la hora **más tardía** que la tienda vio y nunca va hacia atrás. Esa hora sale de: el reloj de la PC en cada consulta, el último registro de auditoría (encadenado, R11 A18), la fecha de emisión de la licencia (firmada) y la hora de la DGII en su última respuesta (encabezado HTTP `Date`, TK-156). Se guarda cada 10 minutos y al cerrar en `settings.Business.ClockSeenUtc` con un HMAC (`ClockSeenSeal`) hecho con un secreto del servidor que no está en la base de datos (carpeta de llaves, como el PIN pepper de R11 A3): cambiar la fecha a mano en la base de datos no sirve (se ignora y quedan los otros pisos).

Si la PC está atrasada más de 5 minutos respecto de esa hora, la licencia cuenta desde ella y sigue avanzando con el contador de tiempo encendido de la máquina; la caja y el dueño ven "La hora de la PC está atrasada". Atajo: el tiempo con la PC apagada y el reloj atrasado no se cuenta hasta que vuelva la hora correcta (o hable con la DGII). Si una PC tuvo la hora **adelantada** y luego se corrige, la hora de la DGII (cuando responde) baja el reloj de la licencia si iba más de un día por delante (R12); TK-308: una tienda conectada a la nube la corrige igual con la hora firmada que la nube manda en cada consulta de licencia (ver "Licencias de la nube"); solo una tienda sin DGII ni nube sigue contando desde la hora adelantada. R12: la hora guardada tiene además una copia sellada en la carpeta de llaves (`license-clock.seen`), así que borrar la de la base de datos no sirve, y la prueba empieza con la hora de la licencia, no con la de la PC.

**Licencia más antigua (R12):** la tienda guarda, sellada en la carpeta de llaves (`license-issued.seen`), la fecha de emisión de la licencia más nueva que instaló y si era un bloqueo. Una licencia emitida antes se rechaza ("Esta licencia es más antigua que la última instalada"), y borrar `license.jws` o poner una vieja en su lugar no quita un bloqueo de la consola.

**Mensaje por causa (R12):** bloqueada por vencimiento ("Su licencia está vencida"), por fin de la prueba ("Terminó la prueba gratis") o por Abastalia ("Abastalia suspendió el servicio de su negocio"); el código sigue siendo `License.Blocked` y `/api/settings/features` trae `blockReason` (`Expired`, `Trial`, `Suspended`).

## Emitir una licencia (personal de Abastalia)

La llave privada y el script están **fuera del repositorio**, en la PC del dueño: `%USERPROFILE%\Abastalia-licencias\` con `issue-license.ps1`. Solo el dueño (o quien él autorice en esa PC) emite licencias.

1. Confirmar el pago y los datos: nombre de la tienda, plan, funciones, número de cajas, número de cajeros y fecha de vencimiento; si el negocio está conectado a la nube, su id (`tenantId`).
2. Ejecutar `issue-license.ps1` en esa carpeta con esos datos (ver la ayuda del propio script). Produce un archivo `.jws` (o el texto de la licencia). **TK-277:** el script necesita el parámetro **`-Cashiers <n>`**, que va a `limits.Cashiers` (como `-Registers` va a `limits.Registers`); el script vive fuera del repositorio y el dueño lo actualiza. Sin él la licencia no limita los cajeros. **R12:** también el parámetro **`-Rnc <rnc>`**, que va a `rnc` (obligatorio en licencias nuevas: la licencia solo sirve en el negocio con ese RNC). **TK-368:** para corregir el RNC de un negocio, **`-ReplacesRnc <rnc anterior>`** va a `replacesRnc` (verifique antes con el dueño que el RNC nuevo es el suyo, como en la consola).
3. Enviar a la tienda el archivo o el texto por el canal acordado (correo o WhatsApp). La licencia no es secreta: solo sirve para la tienda que nombra y no se puede cambiar sin invalidar la firma.
4. Anotar la licencia emitida (tienda, plan, vencimiento) en el registro de ventas.
5. En la consola de la plataforma (cliente → **Plan y consumo**), poner el mismo plan y los servicios adicionales (`SupplierInvoiceReading`): las cuotas mensuales de la nube salen de ahí, no de la licencia (`docs/cuotas.md`).

Si la llave privada se pierde o se filtra: generar un par nuevo, cambiar la llave pública en `LicenseService`, publicar una actualización y volver a emitir las licencias vigentes.

## Instalar una licencia (en la tienda)

En la app: **Configuración → Licencia**, elegir el archivo `.jws` o pegar el código y pulsar **Activar licencia** (`PUT /api/settings/license`). Queda guardada en `C:\ProgramData\Abastalia\license.jws` y aplica al instante, sin reinstalar ni perder ventas. Una licencia inválida o de otra llave se rechaza con "La licencia no es válida o no la firmó Abastalia."

## Licencias de la nube (TK-249)

Con el cobro en línea (`docs/cobro-paddle.md`), la nube firma la licencia de cada tienda con su propia llave de Key Vault según la suscripción (plan, adicionales y "pagado hasta" + 3 días). La tienda conectada la pide por el canal del nodo en cada latido y la instala sola si es más nueva que la instalada. La emisión fuera de línea de arriba sigue igual: gana la licencia emitida más tarde.

**Prueba de 30 días firmada (R12, seguridad B3):** la tienda ya no acepta el número `trialDays` suelto de la respuesta de `POST /api/nodes/license`. Solo alarga la prueba con el campo `trial`: un JWS con encabezado `{"alg":"ES256","typ":"abastalia-trial"}` y cuerpo `TrialGrant` (`{"tenantId":…,"days":30,"issuedUtc":…}`), firmado con la llave de licencias de la nube, para el negocio de esa tienda. El `typ` distinto impide usar una licencia como permiso de prueba y al revés. TK-308: la nube lo firma con la misma llave de Key Vault que las licencias (`Jws.SigningInput(grant, Jws.TrialType)` + `Compose`) cuando el negocio llegó recomendado; si no, `trial` va vacío.

**RNC en la licencia de la nube (TK-308):** la tienda manda en el cuerpo de `POST /api/nodes/license` el RNC de Configuración → Negocio (`{ "rnc": …, "nonce": … }`; es el número fiscal público del negocio, pero no se escribe en los registros). La nube guarda sus dígitos (9 u 11; otra cosa cuenta como sin RNC) en `cloud.Stores.Rnc` y lo firma en el campo `rnc` de la licencia de esa tienda (TK-368: solo el primero; ver "RNC atado a la licencia"). Una tienda que aún no tiene RNC recibe la licencia sin él (la acepta hasta que venza); la que lo lleva se instala con la siguiente emisión (renovación, cambio de plan o bloqueo: la tienda solo instala una licencia emitida después de la suya). Atajo: no se emite una nueva solo porque llegó el RNC. Una tienda de antes de TK-308 manda el cuerpo vacío: su RNC guardado no cambia.

### Varios negocios por cuenta, una licencia por negocio (TK-379)

Una cuenta del panel puede ser dueña de **varios negocios** (Panel → *Mis negocios* → *Agregar negocio*, `POST /api/tenants`; en el celular, tocando el nombre del negocio arriba). **Cada negocio es un cliente aparte:** su suscripción y su plan en Paddle, sus cuotas, su licencia firmada con su RNC (TK-368), su prueba de 14 días y sus tiendas. Cambiar de negocio no cambia nada de los otros. Un negocio con otro RNC va en otro negocio, no como otra tienda del mismo: la licencia de una tienda nombra un solo RNC y la salud de la plataforma marca "Varias empresas".

**Por qué así (investigación del 17/10/2026):** en la región, una segunda razón social paga su propio plan. Alegra RD da un selector "Multiempresa" con un solo login, pero cada empresa lleva su plan; Shopify, Clover (un *merchant ID* por negocio) y Bsale hacen lo mismo; Square, Shopify, Lightspeed, Clover y Bsale cobran además por sucursal o por caja. La excepción es Odoo, que no cobra por empresa pero obliga a subir de plan. Cada RNC trae su propio costo: comprobantes y secuencias, certificado, certificación con la DGII y soporte. El dueño con varios negocios es el cliente más valioso, y cobrar por negocio mantiene el margen. Un descuento por el segundo negocio sería un diferenciador (ningún competidor revisado lo ofrece); queda como decisión comercial del dueño.

**Contra el abuso de pruebas:** una cuenta tiene hasta **10** negocios y abre como mucho **3 por hora** (409 `Tenants.Limit` / `Tenants.TooFast`). La prueba sigue siendo de la tienda: un negocio nuevo con el mismo RNC de otro todavía tendría prueba nueva; lo cierra el ticket propuesto E (la prueba por RNC en la nube).

### RNC atado a la licencia (TK-368)

Una licencia es de **un** negocio. Antes de TK-368 había cuatro huecos: (1) Configuración → Negocio dejaba cambiar el RNC cuando se quisiera, y la licencia seguía activa hasta reiniciar el servidor; (2) la nube guardaba **el último** RNC que la tienda mandaba y lo firmaba en la siguiente licencia, así que una suscripción podía pasar de una empresa a otra (o varias empresas compartir una cuenta) cambiando el RNC en la tienda; (3) restaurar el respaldo de otro negocio o editar la base dejaba emitir e-CF con un RNC que la licencia no nombra; (4) reinstalar con otro RNC sin licencia lo deja usar la prueba otra vez (eso es TK-369). Ahora:

- **En la tienda.** Si la licencia instalada nombra un RNC, `PUT /api/settings/business` solo acepta ese mismo RNC (con o sin guiones); otro, o borrarlo, responde **409 `Settings.RncBound`** "El RNC … está atado a la licencia de este negocio. Para cambiarlo, escríbale a soporte de Abastalia…". Sin licencia con RNC, el RNC queda fijo cuando la tienda emitió su primer comprobante real (un e-CF del ambiente `eCF` o un comprobante B en papel): **409 `Settings.RncInUse`**. Los simulados y los de TesteCF/CerteCF no lo fijan. La pantalla muestra el campo de solo lectura con "Atado a su licencia. Para cambiarlo, escríbale a soporte de Abastalia." (`GET /api/settings/business` trae `licensedRnc`).
- **Ningún e-CF con otro RNC.** Si el RNC del negocio no es el que nombra la licencia instalada (la base editada, el respaldo de otro negocio), la tienda no emite comprobantes: **409 `Fiscal.RncNotLicensed`** "La licencia de esta PC es del RNC …". Se compara con la licencia del archivo aunque no cuente por ese motivo. Los e-CF ya emitidos se siguen enviando a la DGII (obligación fiscal), igual que con Bloqueada.
- **En la nube.** El **primer** RNC válido que manda la tienda queda en `cloud.Stores.Rnc` y es el que se firma; uno distinto después solo se guarda en `Stores.ReportedRnc` como alerta, nunca se firma. Un RNC inválido o vacío no cambia nada.
- **Cambio por soporte.** Consola de la plataforma → tienda → tarjeta **RNC de la licencia**: RNC nuevo y motivo obligatorio (`PUT /api/platform/tenants/{tenantId}/stores/{storeId}/rnc`, `{ rnc, reason }`, auditado en `PlatformAudit`; el motivo queda en `Stores.RncNote` y el anterior en `Stores.PreviousRnc`). Mueve `TenantSubscriptions.UpdatedUtc`, así que la siguiente licencia es más nueva; lleva `rnc` = el nuevo y `replacesRnc` = el anterior. La tienda con el RNC anterior la instala y pasa al nuevo en la misma transacción (auditoría `settings.license` con `rnc` y `replacesRnc`). Mientras tanto, que la tienda siga mandando el RNC anterior no es alerta. Fuera de línea: el script con `-Rnc` y `-ReplacesRnc`. Después del cambio puede hacer falta importar el certificado digital del RNC nuevo.
- **Alertas.** Salud de la plataforma → **RNC por revisar**: tiendas que mandan otro RNC que el de su licencia ("Otro RNC") y negocios con tiendas de RNC distintos ("Varias empresas"). La tarjeta de la tienda muestra el RNC firmado, el que mandó y el último cambio.

`shortcut:` si la transacción de la instalación falla después de escribir `license.jws`, el archivo nombra el RNC nuevo y la base el anterior: la licencia se ignora al arrancar hasta que se instale otra vez (sigue sirviendo, porque reemplaza el RNC que la base tiene).

**Hora firmada de la nube (TK-308, R12 #3):** con el `nonce` (16 bytes al azar, nuevo en cada consulta) la nube responde además `time`: un JWS `{"alg":"ES256","typ":"abastalia-time"}` con cuerpo `ServerTime` (`{"tenantId":…,"nonce":…,"utc":…}`), firmado con la misma llave. La tienda lo acepta solo para su negocio y con el nonce de esa misma consulta (una respuesta vieja no sirve para atrasar el reloj) y corrige el reloj de la licencia como con la hora de la DGII: lo sube si iba atrás y lo baja si iba más de un día adelantado. Lo hace antes de instalar la licencia de la misma respuesta.

**Bloquear desde la consola (TK-279):** cliente → tarjeta **Suscripción** → **Bloquear negocio** con motivo obligatorio (`PUT /api/platform/tenants/{id}/block`, `{ blocked, reason }`, auditado en `PlatformAudit`; el motivo queda en `TenantSubscriptions.BlockNote`). Desde ahí la nube entrega a **todas** las tiendas del negocio, aunque no tenga tiempo pagado, una licencia con `blocked: true` emitida en ese momento (más nueva que la instalada): la tienda se bloquea en su siguiente consulta (TK-287: con su próxima subida de datos, que trae la fecha del último cambio de la suscripción; si no sube nada, en su latido de 4 horas). Bloqueada, pregunta cada 5 minutos, así que el desbloqueo o el pago llegan en minutos. **Desbloquear negocio** (también con motivo) vuelve a la licencia de la suscripción. Un pago en Paddle **no** quita un bloqueo de la consola. Si el negocio no tiene tiempo pagado, al desbloquear ponga también un acuerdo ("pagado hasta"): sin él la nube no entrega licencia nueva y la tienda sigue con la bloqueada (o envíele una licencia fuera de línea, que gana por ser más nueva).
