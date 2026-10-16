# ADR-0011: Quién firma por el receptor de comprobantes en la nube

- Fecha: 2026-10-16
- Estado: aceptada (TK-246, issue #216)
- Relacionadas: ADR-0008 (nube, nodos y su firma), `docs/ecf-propio.md` §10.3, §10.13 y §11, TK-151, TK-152, TK-154, TK-159

## Contexto

La DGII exige que todo emisor electrónico reciba los e-CF de sus suplidores las 24 horas y responda un acuse firmado (ARECF) con su certificado. La PC de la tienda no tiene IP pública y se apaga de noche, así que las URL del receptor viven en la nube (`/{amb}/{rnc}/fe/…`, fijas para el directorio de la DGII). TK-151 resolvió la firma con una copia no exportable del certificado en Key Vault. El dueño pidió que la nube **no** guarde el certificado y que **toda** llamada a la DGII salga de la PC de la tienda (como ya pasaba con la emisión).

## Decisión

Dos modos por tenant, elegidos al activar el receptor (pantalla de la tienda; el panel los muestra) y cambiables (una activación nueva reemplaza la anterior):

| | **A. StoreSigned** (por defecto) | **B. CloudCustody** (TK-151) |
|---|---|---|
| Certificado en la nube | Ninguno: solo sujeto, huella y vencimiento de la prueba | Copia no exportable en Key Vault, con consentimiento |
| Prueba de que el RNC es suyo | La tienda firma un reto de la nube (`ActivacionReceptor`: RNC + reto de 5 minutos, solo de ese tenant); la nube revisa la cadena (`Fe:TrustedRoots`, revocación) y el RNC | El `.p12` subido: cadena y RNC |
| Quién firma el ARECF | La PC que activó, por el canal del nodo: la nube deja la solicitud en una cola y espera hasta `Fe:SignTimeoutSeconds` (10); la PC mantiene un sondeo largo (`POST /fiscal/v1/receiver/sign-requests/next`, firmado como nodo con el hash del cuerpo) y devuelve el XML firmado | Key Vault (RS256) |
| Qué revisa la nube de la firma | Que sea el mismo documento que pidió (solo transformaciones envuelta y C14N, `Reference URI=""`) y de un certificado de confianza del RNC del receptor | (la hace ella) |
| Qué firma la tienda a pedido de la nube | **Solo** ARECF del negocio (Estado 0 solo con su RNC de comprador); todo lo demás se niega y se audita | — |
| Aprobación comercial (ACECF) y su envío | En la tienda: la firma, la entrega al suplidor (directorio, su semilla firmada en la tienda) y la copia a la DGII. La nube la ve por el outbox (`CommercialApprovalRecordedV1`) | Igual: en la tienda (ver "consejo") |
| Cambiar de modo | A → B pide el consentimiento y el `.p12` | B → A borra la llave del vault (borrado suave de 30 días por la protección de purga) |

`Fe:DefaultSigningMode` (StoreSigned) decide qué ofrece primero la pantalla. El firmador del receptor (`FeSigner`) solo acepta `ARECF` y `ACECF` en los dos modos; la nube ya no llama a la DGII ni a ningún suplidor (se quitó `FeOutbound`).

**Sin conexión (modo A).** Si la PC no sondeó en el último minuto, o no contesta a tiempo, o contesta otra cosa, el receptor responde **503 con `Retry-After: 60`** y el emisor reintenta. Lo recibido que pasó las validaciones (XSD, cadena, RNC, duplicados, límites: R8/R9 sin cambios) **se guarda pendiente de acuse** (`FeReceived.AcknowledgedUtc` nulo) y baja a la tienda igual. El reintento del emisor con los mismos bytes se acusa como recibido (no como duplicado). Cuando la PC vuelve, `FeLateAcknowledger` firma el ARECF de lo pendiente y lo **guarda sin enviarlo** al emisor hasta que la DGII diga si un acuse tardío vale (`docs/ecf-propio.md` §11, pregunta 44). Cada tramo sin firma queda en `FeReceiverOutages` y el panel muestra las horas "receptor sin conexión" por PC. Los pasos 8 a 11 de la certificación piden la PC encendida.

**Atajo:** la cola de solicitudes vive en memoria (una instancia de App Service, como los nonces de ADR-0008).

## Consejo de ingeniería (16/10/2026)

- **Seguridad:** A por defecto. Una semilla firmada en la nube es un token completo de la DGII para ese RNC: la nube no firma semillas nunca.
- **Producto:** A por defecto; B como opción por tienda, cuando se cumplan las condiciones de abajo.
- **Tercer voto (minoría):** B por defecto. A se acepta con mitigaciones de disponibilidad (503 con `Retry-After`, lo recibido guardado, horas sin conexión en el panel, aviso en la certificación).

**Decisión:** A por defecto (2 de 3). B queda en el código pero **apagado** por `Fe:AllowCloudCustody` (false; true solo en desarrollo y pruebas): la API se niega a activarlo (`Fe.CloudCustodyUnavailable`) y las pantallas lo muestran como "próximamente". La nube no firma `SemillaModel` en ningún modo; todo lo que va a la DGII sale de la tienda.

**Condiciones para encender B:**
1. Respuesta escrita de la DGII a §11 pregunta 1 (¿custodiar certificados y alojar receptores hace a Toki "proveedor de servicios de FE"?).
2. Ninguna firma de semillas en la nube (hecho en TK-246).
3. Un servicio de firma separado de la API, con "Key Vault Crypto User" por llave (no sobre todo el vault), llaves en HSM (Key Vault Premium o Managed HSM) y límite de firmas por tenant.
4. PIM y alertas en cada asignación de rol sobre el vault.
5. Registros de Key Vault visibles para el dueño (cada uso de su llave).
6. Un mandato notarizable del dueño y del titular, y seguro de ciberriesgo.
7. El texto del consentimiento dice que Azure puede recuperar la llave borrada durante 30 días (borrado suave), que la copia vive fuera del país (eastus2, transferencia internacional, Ley 172-13) y que el titular puede revocar el certificado con su entidad de certificación.

Los roles de Key Vault del Bicep (TK-151) se quedan para B.

## Consecuencias

- El certificado no sale de la tienda en el modo por defecto; robar la nube no da firmas ni tokens de la DGII.
- Con la PC apagada el receptor no acusa: el emisor reintenta y lo recibido no se pierde, pero la disponibilidad depende de la tienda (riesgo que el consejo aceptó).
- La aprobación comercial necesita la PC principal (el panel la muestra, no la decide).
- TK-159 (receptor sin custodia) queda hecho con este diseño; E41 de compras sigue para después de V1.
