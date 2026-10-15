# ADR-0009: La caja fija el certificado del servidor de la tienda al emparejarse (mTLS después)

- Fecha: 2026-10-15
- Estado: aceptada. Completa TK-122; TK-091 (mTLS) sigue pendiente con el plan de abajo
- Relacionada: ADR-0003 (secreto de dispositivo), TK-093 (HTTPS con certificado propio del nodo)

## Contexto

Las cajas (WPF + Blazor, `src/Client`) hablan con la PC principal (`Toki.Host.Local`) por la red de la tienda: HTTPS en 5443 con un certificado autofirmado que el servidor crea la primera vez (`NodeCertificate`, 5 años, almacén de Windows). Cada petición lleva el secreto del dispositivo (`X-Device`) y, con alguien dentro, la sesión del PIN.

Hasta ahora el cliente no verificaba ese certificado. Cualquiera en la misma red (otra PC, un router comprometido) podía hacerse pasar por el servidor y quedarse con el secreto del dispositivo y los PIN. Un certificado autofirmado no se puede validar contra una CA pública, y la tienda no tiene CA ni dominio.

## Decisión

**Confianza en el primer uso, con una huella para comparar a ojo.**

1. Antes de emparejarse, la caja acepta el certificado que le presente el servidor y lo recuerda (`NodeCertificatePin.Seen`, SHA-256 del certificado).
2. En "Unir a una tienda" la caja muestra la **huella del servidor**: los primeros 64 bits del SHA-256, como `AB12 CD34 EF56 7890`. El dueño ve la misma huella junto al código de 8 dígitos en Configuración → Equipos (su propia caja ya la tiene fijada). Si no coinciden, no se une.
3. Si el emparejamiento sale bien, la caja guarda la huella completa (64 hex) junto al secreto del dispositivo, en el mismo almacén local (WPF: DPAPI del usuario).
4. Desde ahí, cada conexión TLS (`SocketsHttpHandler.SslOptions.RemoteCertificateValidationCallback`) exige **ese** certificado, aunque otro fuera de confianza pública. Si no coincide, la conexión se corta antes de enviar nada y la caja dice "El servidor que respondió no es el de la tienda…" (`Problem.UnknownServer`, no es "incierto": no salió nada).
5. La huella se carga antes que el secreto: ninguna petición lleva el secreto a un servidor sin fijar.
6. Si el dueño revoca la caja (`Identity.DeviceNotPaired`), la caja olvida secreto y huella y se empareja de cero.

El protocolo del servidor no cambia: el código de 8 dígitos, `POST /api/identity/devices/pair` y el secreto siguen igual. En desarrollo (`Toki.Client.Web` y la API por `http`) no hay TLS y no se fija nada.

**Por qué no meter la huella dentro del código.** El código lo escribe una persona: 8 dígitos. Cualquier prueba derivada del código que la caja envíe (por ejemplo un HMAC del código con la huella) se rompe por fuerza bruta en menos de un segundo (10⁸ opciones). La huella de 64 bits comparada a ojo no tiene ese problema: fabricar un certificado con los mismos 64 bits cuesta ~2⁶⁴ intentos, mucho más que los 10 minutos que vale el código.

## Consecuencias

- Un atacante solo puede colarse si está activo en la red **durante** el emparejamiento y nadie compara la huella. Después, la caja ya no habla con nadie más.
- `shortcut:` si el certificado del servidor cambia (reinstalación, o renovación a 30 días de vencer a los 5 años), las cajas dejan de conectarse con el aviso de servidor desconocido. Hoy se arregla borrando los datos locales de la caja (`%LocalAppData%\Toki`) y emparejándola de nuevo. TK-091 trae el relevo.
- `shortcut:` la comparación de la huella es voluntaria; la pantalla la muestra pero no obliga a confirmarla.

## Pendiente: mTLS (TK-091)

1. La PC principal crea una **CA de la tienda** (autofirmada, en el almacén de la máquina) y la usa para firmar su certificado de servidor y los de las cajas.
2. Al emparejarse, la caja genera su par de llaves (no exportable, almacén del usuario o TPM) y manda un CSR junto con el código; el servidor devuelve el certificado firmado por la CA. La caja fija la CA en vez del certificado del servidor, así el servidor puede renovar el suyo sin re-emparejar.
3. Kestrel pide certificado de cliente (`ClientCertificateMode.RequireCertificate`) y la autenticación del dispositivo sale del certificado (huella → dispositivo, revocación igual que hoy). El secreto `X-Device` se retira.
4. El navegador de desarrollo (`Toki.Client.Web`) sigue por `http` en `localhost`, sin certificado.
