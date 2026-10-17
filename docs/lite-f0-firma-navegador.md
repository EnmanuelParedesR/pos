# Lite F0: firmar el e-CF en el navegador (TK-317, issue #286)

Fecha: 17/10/2026. Prueba técnica aislada en `src/Cloud/lite-poc/`. No toca el sitio, el panel ni la tienda.

> **TK-322 (F1-4):** la PoC pasó a ser el módulo `src/Client/lite-signer/` (`signer.js` ahora es `xmldsig.js`, `importP12` de aquí se llama `openP12`). Lo nuevo está en [la sección F1-4](#f1-4-el-firmador-del-navegador-tk-322-issue-291), al final.

## Resultado

**Sí se puede.** El navegador abre el `.p12` del dueño, guarda la llave como `CryptoKey` **no exportable** y firma el e-CF igual que la tienda. El archivo y la llave no salen del dispositivo.

| Qué | Resultado |
|---|---|
| Leer el `.p12` (PKCS#12) | ✅ Con node-forge. Probado con AES-256 (PBES2, como OpenSSL 3 y .NET `Pbes2Aes256Sha256`) y con 3DES-SHA1 (como Windows y los `.p12` viejos). Una contraseña equivocada se rechaza |
| Llave no exportable | ✅ `importKey('pkcs8', …, extractable: false, ['sign'])`: `exportKey` falla y la llave solo sirve para firmar |
| Firma de la 31, la 32, su RFCE y la semilla | ✅ **Byte por byte iguales** a `CertificateEcfSigner` con la misma llave (RSA PKCS#1 v1.5 es determinista), con el mismo código de seguridad |
| XSD y verificador de la nube | ✅ Pasan el XSD oficial (`EcfXml.Validate`) y `FeXml.Signer`, el que revisa los e-CF recibidos |
| Semilla con `xmlns:xsi` y `xmlns:xsd` en la raíz | ✅ El `SignedInfo` canónico hereda esos espacios de nombres, como hace .NET (es la trampa típica de C14N) |
| Envío a TesteCF | ⏸ No se llamó (falta el acceso de TK-141). `sendToDgii()` en `signer.js` dice qué falta: semilla → `validarsemilla` → token → `recepcion` o `recepcionfc` |

Pruebas (verdes):
- `tests/Abastalia.Cloud.Tests/LiteBrowserSigningTests.cs`: .NET crea un certificado autofirmado de prueba y su `.p12` (las dos cifras), arma la 31, la 32, el RFCE y la semilla con `EcfXml`, Node los firma con `signer.js` y .NET compara con su firmador, valida el XSD y la firma. Se salta si no hay `node` o falta `npm ci` en la PoC; en CI se instala.
- `src/Cloud/lite-poc/test/signer.test.mjs` (`npm test`): llave no exportable, firma verificada con la llave pública, contraseña equivocada y C14N.

Ningún certificado ni llave queda en el repositorio: se crean en cada prueba.

## Cómo está hecho

- **`importP12(bytes, contraseña)`**: forge descifra el `.p12`, elige el certificado que tiene la misma llave pública (un `.p12` puede traer la cadena) y pasa la llave a WebCrypto como PKCS#8 no exportable.
- **`signXml(xml, firmante)`**: quita los espacios entre etiquetas (la tienda firma con `PreserveWhitespace = false`), calcula el SHA-256 de la forma canónica, arma el `Signature` como .NET (envuelta, C14N 1.0, RSA-SHA256, `Reference URI=""`, certificado en `KeyInfo/X509Data`), firma el `SignedInfo` con `crypto.subtle.sign` y escribe el XML como lo escribe `XmlDocument.OuterXml`.
- **C14N propia** (~25 líneas) en lugar de una librería de XMLDSig. Cubre los documentos de la DGII: atributos ordenados, espacios de nombres heredados en el `SignedInfo`, elementos vacíos expandidos y escapes. **No cubre** atributos `xml:*` heredados (los documentos de la DGII no los usan). La prueba byte por byte contra .NET es la red de seguridad.
- Funciona igual en el navegador (`DOMParser`, `crypto.subtle`) y en Node (con `@xmldom/xmldom`, solo para las pruebas).

## Librería elegida

| Opción | Licencia | Tamaño en el navegador | Mantenimiento | PKCS#12 con 3DES/RC2 | Veredicto |
|---|---|---|---|---|---|
| **node-forge 1.4.0** (solo `pkcs12` y `pki`) | BSD-3-Clause (doble con GPL-2.0; se usa BSD) | **~206 KB minificado, ~55 KB gzip** (forge completo: ~284 / ~79 KB). Medido con esbuild | Última versión 03/2026; lento pero activo | Sí (JavaScript puro) | **Elegida** |
| PKI.js 3.4.1 + asn1js | BSD-3-Clause | Más dependencias (asn1js, pvtsutils, bytestreamjs, @noble/hashes); no se midió | Activo (09/2026) | Descifra con WebCrypto, que no tiene 3DES ni RC2: un `.p12` de Windows o de una CA vieja podría no abrir (no se probó) | Descartada por ese riesgo |
| xmldsigjs 2.8.8 | MIT | Incluye PKI.js, asn1js, xml-core y xpath | Activo | No lee PKCS#12 (usa PKI.js) | No hace falta: la C14N de la DGII cabe en 25 líneas |

**Aviso de `npm audit`:** forge tiene un aviso alto (GHSA-86w9-cpqp-85rv) en la **verificación** de firmas RSA. La PoC nunca verifica con forge: firma WebCrypto y verifica .NET. Igual, F1 debe importar solo los módulos que usa y revisar `npm audit` en cada versión.

## Tiempos

Node 24 en un Ryzen 5 5600X (`npm run bench`, mediana; llave RSA de 2048 bits):

| Paso | Escritorio | Celular barato (estimado) |
|---|---|---|
| Importar el `.p12` (una sola vez) | 12–21 ms | 0,1–0,3 s |
| Firmar una 31 (C14N + SHA-256 + RSA) | ~1 ms | 5–15 ms |

Estimado para un Android de gama baja (Cortex-A53): JavaScript de 5 a 10 veces más lento y RSA nativo de 3 a 5 veces. La firma no se nota en la venta: la 32 lleva dos firmas (el e-CF y el RFCE), unos 10–30 ms. La importación depende de las iteraciones del `.p12` (las pruebas usan unas 2,000; algunas CA usan más). Se hace una vez y puede tardar un segundo sin problema. Falta medir en teléfonos reales.

## Riesgos

1. **iPhone (Safari) puede borrar la llave.** Safari borra IndexedDB de los sitios que no se abren en 7 días. Las apps puestas en la pantalla de inicio tienen su propio contador, pero hay reportes de borrados. En navegación privada no queda nada. Mitigación: pedir `navigator.storage.persist()`, pedir instalar la PWA, revisar al abrir que la llave sigue ahí y, si no, pedir otra vez el `.p12`.
2. **Guardar la `CryptoKey` en IndexedDB** (clon estructurado) lo permite la especificación y lo hacen Chrome, Firefox y Safari, pero no se probó en esta PoC: Node no tiene IndexedDB. Hay que probarlo en Safari 17/18 y en Android viejo antes de F1.
3. **"No exportable" no es una caja fuerte.** Impide copiar la llave, no usarla: un XSS o una versión maliciosa de la PWA convierte el navegador en firmador. Mitigación: CSP estricta, nada de scripts de terceros, SRI, despliegue con dos aprobaciones y una lista de firmas visible para el dueño.
4. **Al importar, la llave y la contraseña pasan por la memoria de JavaScript.** No se pueden borrar a mano y el recolector las limpia cuando quiere. Es un momento corto, una sola vez.
5. **Los `.p12` reales de Avansi o Digifirma no se probaron.** Puede variar la cifra (AES, 3DES o RC2) y las iteraciones. forge soporta las tres, pero RC2-40 no se probó. forge vuelve a codificar el certificado al ponerlo en `KeyInfo`: con los de prueba sale idéntico; con uno real hay que comprobar que también.
6. **La C14N propia solo cubre lo que arma Abastalia.** Si F1 firma XML de otro origen (por ejemplo, un archivo de la DGII para la certificación), hay que ampliar la prueba byte por byte con ese archivo.
7. **No se probó contra la DGII.** Que .NET valide la firma no garantiza que TesteCF la acepte. Se cierra con TK-141.

## Recomendación para F1

**Seguir.** La firma en el navegador ya no es el riesgo: sale igual que la de la tienda.

1. Usar este `signer.js` (forge con solo `pkcs12` y `pki`, más la C14N propia). La PWA lo llama por interop como un `IEcfSigner` "de dispositivo". No hace falta RSA en WebAssembly.
2. Antes de escribir el producto, probar en dispositivos reales: un iPhone con Safari 17/18 (instalado y sin instalar), un Android barato con Chrome, persistencia de la llave tras cerrar y tras 7 días, y tiempos reales.
3. Importar un `.p12` real del dueño en su equipo (nunca subirlo al repositorio) y, con TK-141, cerrar el paso 5: semilla → token → 31 y RFCE en TesteCF, directo desde el navegador.
4. Poner en el firmador del dispositivo la regla de la tienda: solo e-CF, RFCE, ANECF, ACECF y ARECF del propio RNC, y la semilla solo para sí mismo (hoy `signXml` firma cualquier cosa; está marcado como `shortcut`).
5. Endurecer la PWA (CSP, SRI, sin terceros) y guardar en la nube la huella del certificado, para avisar al dueño si cambia.

## F1-4: el firmador del navegador (TK-322, issue #291)

Fecha: 17/10/2026. Módulo ES en `src/Client/lite-signer/` (sin empaquetador; forge sigue limitado a `pkcs12` y `pki`). Lo llamará la PWA del Ligero (TK-324).

| Archivo | Qué hace |
|---|---|
| `index.js` | La API pública: importar el `.p12`, guardar y borrar la llave, firmar con lista blanca y registro |
| `xmldsig.js` | La firma de F0 (`openP12`, `parseXml`, `signXml`, `canonical`), sin cambios en los bytes. Ahora también devuelve el `DigestValue` |
| `allowlist.js` | Qué se firma y qué no |
| `store.js` | IndexedDB (`indexedDbStore`) y la misma interfaz en memoria (`memoryStore`, para Node y las pruebas) |
| `dgii.js` | El cliente de la DGII: semilla, token, recepción, RFCE y consultas |

**API:**
- `importP12(archivo, contraseña, store)`: lee el `.p12` (un `File`, `Blob` o bytes), guarda en IndexedDB el **objeto `CryptoKey` no exportable** (nunca los bytes de la llave) con el certificado, y pide `navigator.storage.persist()`. Devuelve `{ certificate, persisted }`. Si `persisted` es `false`, el navegador puede borrarla: la PWA debe pedir instalarla.
- `loadSigner(store)` devuelve la llave o `null`, y `removeSigner(store)` la borra.
- `deviceSigner({ rnc, types, store })` → `{ sign(xml), log() }`. `sign` carga la llave cada vez. Si no está (nunca se importó o el navegador la borró), lanza `KeyMissing` con el mensaje "Vuelva a importar el archivo .p12". Si el documento no es del negocio, lanza `SignRefused`. Si firma, devuelve `{ signedXml, securityCode, digest, document, type, encf }`.
- `log()` lista lo firmado: `{ document, type, encf, digest, signedAt }`, sin el XML.
- `dgiiClient({ environment: 'TesteCF' | 'CerteCF' | 'eCF', signer })` → `token()`, `sendEcf(rnc, eNCF, xml)` (TrackId), `getResult(trackId)`, `sendRfce(rnc, eNCF, rfce)` (respuesta inmediata) y `getRfceStatus(rnc, eNCF, código)`. Cada respuesta es `{ state, trackId, sequenceUsed, messages }`. Un fallo pasajero (5xx, 429, autenticación, una respuesta que no es de la DGII) lanza `DgiiError` y la cola de la PWA reintenta. Es una copia del `DgiiClient` de la tienda: el token vive solo en memoria y se renueva 5 minutos antes de `expira` o ante un 401 (un solo reintento), con *timeout* de 30 s y `multipart` con el campo `xml` llamado `RNC + e-NCF + .xml`.

**Lista blanca** (regla de ADR-0011 del lado del dispositivo; lee solo hijos directos, así que un elemento repetido o anidado no se cuela):
- `ECF` y `RFCE`: `RNCEmisor` igual al RNC del negocio, `TipoeCF` en `types` y un e-NCF de ese tipo. El RFCE, solo de la 32.
- `ANECF`: `RncEmisor` propio y cada tipo anulado en `types`.
- `ARECF`: `RNCComprador` propio. Un rechazo (`Estado` 1) puede nombrar a otro comprador (motivo 4), igual que en la tienda (`RemoteSigning`).
- `ACECF`: `RNCComprador` propio.
- `SemillaModel`: solo con `valor` y `fecha`.
- Todo lo demás se niega: otra raíz, una raíz con espacio de nombres o un documento ya firmado.

**Pruebas** (`npm test`, 18, verdes):
- `test/signer.test.mjs`: la llave no exportable se guarda, se carga y se borra; una contraseña equivocada no guarda nada; con la llave borrada se pide volver a importar; lo permitido se firma y queda en el registro sin XML; y 15 casos negados que no quedan en el registro.
- `test/dgii.test.mjs`: una DGII falsa con `fetch` que copia las respuestas documentadas (el mismo contrato que `FakeDgii`). Cubre las URL por ambiente, el ciclo de la 31 con un solo token, la renovación a 5 minutos y ante un 401, llamadas simultáneas con una sola autenticación, el RFCE y `consultarfce`, los rechazos como respuesta y los fallos como error.
- `test/xmldsig.test.mjs`: la de F0.
- `LiteBrowserSigningTests` (.NET) ahora pasa por la API pública (`sign-cli.mjs`: importar → lista blanca → firmar → registro). Sigue siendo byte por byte igual a `CertificateEcfSigner` y comprueba que el `digest` del registro es el `DigestValue`.

IndexedDB no se probó en Node. `fake-indexeddb` es Apache-2.0 y no una dependencia MIT pequeña, así que se usa `memoryStore` con la misma interfaz. `indexedDbStore` queda marcado como `shortcut`: se prueba en Safari y Chrome con TK-324.

**`npm audit`:** sigue el aviso alto de node-forge (GHSA-86w9-cpqp-85rv, verificación de firmas RSA PKCS#1 v1.5), sin arreglo publicado. No nos afecta: el módulo nunca verifica con forge (firma WebCrypto y verifica el servidor .NET). Hay que revisarlo en cada versión.

**Sin probar contra la DGII** (TesteCF espera el acceso de TK-141). Cuando llegue, falta:
1. Que TesteCF acepte la semilla firmada en el navegador y devuelva el token. Ver la forma real de `expira`.
2. Que el preflight CORS admita `Authorization` y `multipart/form-data` desde el origen de la PWA (el 16/10 solo se vio `Access-Control-Allow-Origin: *`).
3. Mandar una 31 (TrackId → resultado) y una 32 por RFCE. Ver la forma real de cada respuesta (`codigo` como número, `mensajes` como lista de `{ valor, codigo }`).
4. Los límites por minuto y la vida real del token.
5. Que el certificado real (Avansi o Digifirma) salga idéntico en `KeyInfo`.

**Queda para otros tickets:** la cola, el plazo de 72 h y `consultatrackids` antes de reenviar tras un *timeout* (TK-323/TK-324); el envío del ACECF y del ANECF a la DGII (no lo pide F1-4); recortar el registro de firmas (marcado como `shortcut`) y mostrarlo al dueño (TK-324).
