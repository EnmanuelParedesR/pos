# ADR-0003: Sesiones por PIN ligadas a un secreto de dispositivo (mTLS después)

- Fecha: 2026-10-15
- Estado: aceptada (temporal hasta TK-091)

## Decisión

- Cada caja emparejada guarda un **secreto de dispositivo** aleatorio (32 bytes) que envía en el encabezado `X-Dispositivo`. El servidor solo guarda su SHA-256.
- El cajero entra con **código + PIN** (4–8 dígitos, PBKDF2 con `PasswordHasher`). La sesión es un token aleatorio de 12 h, ligado al dispositivo, enviado como `Authorization: Bearer`.
- Cada petición valida sesión, dispositivo no revocado, usuario activo y permisos del rol en el servidor.
- Un PIN incorrecto es un comando **exitoso** que guarda el intento fallido (si fuera un error, el rollback borraría el contador de bloqueo); el endpoint lo convierte en 401.

## Pendiente

`arquitectura-v1-vsa.md` §8.1 pide certificados de cliente (mTLS) con una CA de la tienda. Queda en TK-091 antes del piloto. El resto del modelo (sesión, permisos, supervisor) no cambia.
