# ADR-0003: Sesiones por PIN ligadas a un secreto de dispositivo (mTLS después)

- Fecha: 2026-10-15
- Estado: aceptada (temporal hasta TK-091)

## Decisión

- Cada caja emparejada guarda un **secreto de dispositivo** aleatorio (32 bytes) que envía en el encabezado `X-Dispositivo`. El servidor solo guarda su SHA-256.
- El cajero entra con **código + PIN** (4–8 dígitos, PBKDF2 con `PasswordHasher`). La sesión es un token aleatorio de 12 h, ligado al dispositivo, enviado como `Authorization: Bearer`.
- Cada petición valida sesión, dispositivo no revocado, usuario activo y permisos del rol en el servidor.
- Un PIN incorrecto es un comando **exitoso** que guarda el intento fallido (si fuera un error, el rollback borraría el contador de bloqueo); el endpoint lo convierte en 401.

## Pendiente

`arquitectura-v1-vsa.md` §8.1 pide certificados de cliente (mTLS) con una CA de la tienda. Queda en TK-091 antes del piloto. Mientras tanto la caja fija el certificado del servidor al emparejarse (ADR-0009, TK-122), que también trae el plan del mTLS. El resto del modelo (sesión, permisos, supervisor) no cambia.

## Tiempos de inactividad (TK-366, 17/10/2026)

| Dónde | Antes | Ahora | Configuración |
|---|---|---|---|
| Caja: quien puede autorizar (supervisor, dueño: anular, devolver, retirar, cerrar con diferencia, usuarios) | 5 min sin peticiones propias; el servidor cerraba la sesión sin aviso al siguiente clic | **5 min** sin tecla, clic o toque; aviso «¿Sigue ahí?» al minuto 4; vuelve al PIN | `Identity:IdleMinutes` (5) |
| Caja: los demás (cajero, inventario, contador) | Ninguno: la sesión duraba 12 h | **15 min**, con el mismo aviso un minuto antes | `Identity:RegisterIdleMinutes` (15) |
| Administración (en la PC de la tienda) | Igual que la caja (es la misma app) | Igual que la caja, según los permisos de quien entró | Las mismas |
| Sesión de la caja, máximo | 12 h | 12 h (sin cambio) | `Session.Duration` |
| Panel en la nube | Token de acceso 30 min, renovación 7 días, que se renueva con el uso | Sin cambio: no se cierra por inactividad mientras se use al menos una vez por semana | `Auth:AccessTokenMinutes` (30), `Auth:RefreshTokenDays` (7) |

- **Cómo cuenta la caja:** `escuadra.js` anota la última tecla, clic, toque o rueda (un escaneo son teclas; mover el ratón no cuenta). `IdleGuard.razor` mira cada 5 s: al faltar un minuto muestra «¿Sigue ahí?» (cualquier tecla o «Seguir aquí» lo quita); al llegar, cierra la sesión en el servidor y vuelve a la pantalla del PIN. Mientras alguien trabaja, le avisa al servidor cada 15 s (`GET /api/identity/me`), porque las consultas periódicas de la barra de estado no cuentan.
- **La venta abierta no se pierde:** queda en la PC para el cajero que la empezó (`Cart.HandOver`, TK-304); al volver con su PIN la encuentra. Otro usuario entra con la caja vacía. La pantalla del PIN lo dice.
- **El servidor es el respaldo:** rechaza la sesión 2 minutos después del límite (`IdleLock.Grace`), para una caja que se apagó o se colgó. Así nunca cierra antes que el aviso de la caja.
- **Panel:** guarda el token de renovación en el navegador; se cierra solo si pasa una semana sin usarse (nadie está mirando, así que no hace falta aviso) o con «Salir». Un cierre por inactividad en el panel no se propone para V1: el dueño lo usa sobre todo en su teléfono, que ya tiene su propio bloqueo, y la cuenta puede usar verificación en dos pasos. Si una tienda lo pide, se agrega como opción del dueño.
