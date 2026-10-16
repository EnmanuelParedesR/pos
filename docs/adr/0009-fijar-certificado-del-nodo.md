# ADR-0009: La caja fija el certificado del servidor de la tienda al emparejarse (mTLS después)

- Fecha: 2026-10-15
- Estado: aceptada. Completa TK-122 y TK-091 (mTLS con la CA de la tienda)
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
- Con TK-091 (abajo) la caja fija la CA de la tienda, no el certificado del servidor: renovarlo ya no rompe nada. Si se pierde la CA (reinstalación sin la carpeta de llaves), las cajas ven "El servidor cambió de certificado" y se vuelven a unir con un botón.
- `shortcut:` al unir una caja la comparación de la huella es voluntaria; la pantalla la muestra pero no obliga a confirmarla (en "Primera instalación" contra otra PC, sí).

## mTLS (TK-091, hecho)

1. **CA de la tienda.** La PC principal crea su CA la primera vez que la necesita, en la carpeta de llaves (`Toki:KeysFolder`, por defecto `C:\ProgramData\Toki\keys`): `store-ca.pem` (el certificado) y `store-ca.key` (la llave, cifrada con Data Protection, que en Windows usa DPAPI de la máquina, como la llave de los respaldos). ECDSA P-256, 30 años. `StoreCertificateAuthority` en Identity.
2. **Certificado del servidor.** Lo emite la CA en memoria al arrancar (1 año) y se renueva solo a 30 días de vencer (`ServerCertificateSelector` de Kestrel). Al arrancar, el registro anota su huella ("Huella del servidor para unir cajas").
3. **Emparejar.** La caja genera su par de llaves (ECDSA P-256) y manda el CSR con el código de 8 dígitos (`POST /api/identity/devices/pair`, y `POST /api/identity/setup` para la PC principal). El servidor valida la firma del CSR, ignora su sujeto y firma un certificado `CN=Toki Caja {id}` de uso "autenticación de cliente" (10 años), guarda su huella SHA-256 en `Devices.CertificateThumbprint` y responde el certificado y la CA. Con CSR no se entrega secreto.
4. **La caja fija la CA**, no el certificado del servidor, después de comprobar que el certificado que vio (y cuya huella mostró) lo emitió esa CA. Así el servidor renueva el suyo sin re-emparejar. La caja acepta solo certificados de servidor de esa CA con uso "autenticación de servidor" (el de otra caja no sirve); no comprueba el nombre, porque la IP de la PC principal puede cambiar. La llave de la caja y su certificado se guardan en el almacén de la caja (WPF: DPAPI del usuario de Windows, `%LocalAppData%\Toki\client.dat`).
5. **Kestrel pide certificado de cliente** (`AllowCertificate`, no `Require`, porque `/health`, el emparejamiento y la instalación vienen antes de tenerlo). Un certificado que no emitió la CA corta el saludo TLS. Todo lo demás exige certificado (middleware en `Program.cs`, responde `Identity.DeviceNotPaired`), y el dispositivo sale de la huella del certificado (`PairedDevices.IdOfAsync`).
6. **Revocar** marca el dispositivo; su certificado solo vale mientras su dispositivo no esté revocado (la tabla `Devices` es la lista de denegación por huella). La caja recibe `Identity.DeviceNotPaired`, olvida todo y se empareja de cero.
7. **El secreto `X-Device` se retira** por https. Sigue solo por `http` (el navegador de desarrollo `Toki.Client.Web`, las pruebas en memoria y E2E), donde no hay TLS ni certificado.
8. **R8 #9.** Si el certificado del servidor no es el fijado, la caja no lo confunde con "sin conexión": muestra "El servidor cambió de certificado: vuelva a unir este equipo" con un botón que olvida el emparejamiento. Con la CA fijada, renovar el certificado del servidor ya no lo provoca.
9. **R8 #11.** La caja recuerda la huella que mostró y `PairAsync` la recibe: si la última conexión no fue a ese mismo certificado, no guarda nada ("El servidor cambió desde que se mostró la huella"). En "Primera instalación", si el servidor no está en la misma PC (`localhost`), la caja muestra la huella y no manda el nombre, el código ni el PIN del dueño hasta que se marca "La huella es igual a la de la PC principal".

### Sin modo doble (TK-251)

Había un modo doble (`Toki:LegacyDeviceSecrets`) para cajas emparejadas antes de TK-091: el servidor seguía mostrando su certificado autofirmado viejo (`NodeCertificate`) y aceptaba el secreto por https. Ninguna tienda real se instaló antes de TK-091, así que se quitó: por https solo vale el certificado de la caja; el secreto `X-Device`, solo por `http` (desarrollo y pruebas).

### Pendiente

- `shortcut:` el certificado de la caja vence a los 10 años y no se renueva solo: hay que re-emparejar.
- `shortcut:` la llave de la caja vive en el almacén DPAPI del usuario, no en el almacén de certificados de Windows ni en el TPM (no exportable).
- `shortcut:` la llave de la CA está protegida con DPAPI de la máquina: cualquier proceso con permisos en esa PC puede leerla, igual que la llave de los respaldos.
