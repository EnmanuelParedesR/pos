# Licencias de Abastalia (TK-242)

Cómo es una licencia, cómo la emite el personal de Abastalia y cómo se instala en la tienda. El código está en `src/Modules/Settings/Abastalia.Settings/License.cs` y `src/BuildingBlocks/Abastalia.BuildingBlocks/Security/License.cs`.

## Formato

Una licencia es un **JWS compacto** (`encabezado.cuerpo.firma`, base64url) firmado con **ES256** (ECDSA P-256 + SHA-256). El encabezado es `{"alg":"ES256","typ":"JWT"}` y el cuerpo es el JSON de `LicenseData` (nombres en camelCase):

| Campo | Qué es |
|---|---|
| `store` | Nombre de la tienda (como sale en el contrato) |
| `plan` | Nombre del plan (`Esencial`, `Estándar`, `Pro`; antes `Varias sucursales`). `limits.Registers` 0 = solo la PC principal (Esencial) |
| `features` | Funciones pagas: `ButcherModule`, `WholesaleModule`, `CloudBackup`, `AiAssistant`, `SupplierInvoiceReading` (servicio adicional de lectura de facturas con IA, ver `docs/cuotas.md`) |
| `limits` | Límites por nombre: hoy solo `Registers` (cajas además de la PC principal) |
| `issuedUtc`, `expiresUtc` | Emisión y vencimiento, en UTC |
| `tenantId` | Opcional. Id del negocio en la nube (consola de la plataforma). La tienda no lo mira; la nube solo deja que la licencia active el lector de facturas con IA de ese negocio (R11 B-Low8, TK-268). Sin él, la licencia no cuenta en la nube (el negocio usa su suscripción o la prueba) |

Ejemplo de cuerpo:

```json
{"store":"Carnicería La Esquina","plan":"Pro","features":["ButcherModule","CloudBackup"],"limits":{"Registers":3},"issuedUtc":"2026-10-16T00:00:00Z","expiresUtc":"2027-10-16T00:00:00Z"}
```

La tienda solo tiene **llaves públicas** (dentro de `LicenseService`): la del dueño (licencias fuera de línea) y, desde TK-249, la de la nube (`AbastaliaCloudPublicKey`, licencias de una suscripción pagada con Paddle; vacía hasta crear la llave en Key Vault, ver `docs/cobro-paddle.md`). Acepta una licencia firmada por cualquiera de las dos y en producción no acepta otra. Las llaves privadas **nunca** entran al repositorio ni al producto.

## Estados

- **Prueba:** sin licencia, 14 días desde la primera venta, todo encendido.
- **Activa:** licencia válida y sin vencer.
- **Gracia:** venció hace menos de 15 días; todo sigue igual (tiendas sin internet).
- **Restringido:** terminó la prueba o la gracia: funciones pagas apagadas y una caja además de la PC principal. Vender, e-CF, exportar y respaldar **nunca** se detienen y no se pierden datos.

## Emitir una licencia (personal de Abastalia)

La llave privada y el script están **fuera del repositorio**, en la PC del dueño: `%USERPROFILE%\Abastalia-licencias\` con `issue-license.ps1`. Solo el dueño (o quien él autorice en esa PC) emite licencias.

1. Confirmar el pago y los datos: nombre de la tienda, plan, funciones, número de cajas y fecha de vencimiento; si el negocio está conectado a la nube, su id (`tenantId`).
2. Ejecutar `issue-license.ps1` en esa carpeta con esos datos (ver la ayuda del propio script). Produce un archivo `.jws` (o el texto de la licencia).
3. Enviar a la tienda el archivo o el texto por el canal acordado (correo o WhatsApp). La licencia no es secreta: solo sirve para la tienda que nombra y no se puede cambiar sin invalidar la firma.
4. Anotar la licencia emitida (tienda, plan, vencimiento) en el registro de ventas.
5. En la consola de la plataforma (cliente → **Plan y consumo**), poner el mismo plan y los servicios adicionales (`SupplierInvoiceReading`): las cuotas mensuales de la nube salen de ahí, no de la licencia (`docs/cuotas.md`).

Si la llave privada se pierde o se filtra: generar un par nuevo, cambiar la llave pública en `LicenseService`, publicar una actualización y volver a emitir las licencias vigentes.

## Instalar una licencia (en la tienda)

En la app: **Configuración → Licencia**, elegir el archivo `.jws` o pegar el código y pulsar **Activar licencia** (`PUT /api/settings/license`). Queda guardada en `C:\ProgramData\Abastalia\license.jws` y aplica al instante, sin reinstalar ni perder ventas. Una licencia inválida o de otra llave se rechaza con "La licencia no es válida o no la firmó Abastalia."

## Licencias de la nube (TK-249)

Con el cobro en línea (`docs/cobro-paddle.md`), la nube firma la licencia de cada tienda con su propia llave de Key Vault según la suscripción (plan, adicionales y "pagado hasta" + 3 días). La tienda conectada la pide por el canal del nodo en cada latido y la instala sola si es más nueva que la instalada. La emisión fuera de línea de arriba sigue igual: gana la licencia emitida más tarde.
