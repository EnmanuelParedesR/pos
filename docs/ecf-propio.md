# e-CF propio: Abastalia habla directo con la DGII (TK-140)

> 15 de octubre de 2026. Diseño, sin código. Reemplaza el borrador anterior de este archivo.
> Decisión del dueño: **sin proveedor de terceros**. Abastalia llama a los servicios web de la DGII con la información y el certificado digital de cada contribuyente.
> Fuentes primarias: documentación oficial de la DGII (lista al final). Esas guías dicen "publicación informativa sin validez legal": la Ley 32-23, el Decreto 587-24 y las normas generales son las que mandan. Lo marcado **(verificar)** no aparece en una fuente primaria o hay que probarlo en TesteCF.

## 1. Resumen

- La DGII expone una API REST con XML firmado. Se autentica con una **semilla firmada** que se cambia por un **token de 1 hora** (`Authorization: Bearer`). Hay tres ambientes: **TesteCF** (pre-certificación), **CerteCF** (certificación) y **eCF** (producción).
- La **factura de consumo (32) menor de RD$250,000** no se envía completa: se manda un **resumen firmado (RFCE)** a otro servicio (`fc.dgii.gov.do`), que responde en el acto. Todo lo demás (31, 33, 34 y la 32 de RD$250,000 o más) se envía completo, recibe un **TrackId** y se consulta su resultado.
- Firma: **XMLDSig envuelta, RSA-SHA256, C14N, `Reference URI=""`**, con el certificado de **persona física para procedimientos tributarios** de alguien delegado como **Firmante** del RNC en la Oficina Virtual (OFV). El **código de seguridad** son los primeros 6 caracteres del `SignatureValue`.
- Cada emisor electrónico es también **receptor**: tiene que publicar en internet, 24/7, un servicio de **Recepción** (que responde un **acuse de recibo firmado**) y uno de **Aprobación Comercial**. Esto no puede vivir en la PC de la tienda.
- **Recomendación** (§10.7): la PC principal firma y le habla **directo a la DGII**, también en contingencia. La nube publica los servicios de receptor de cada tienda y firma los acuses con una **copia del certificado en Azure Key Vault, solo con el consentimiento expreso del dueño** y limitada a acuses, aprobaciones comerciales y semillas. El relevo e-CF deja de enviar a la DGII y pasa a ser el archivo en la nube.
- **Riesgo mayor:** el plazo de los pequeños contribuyentes vence el **15 de noviembre de 2026**. La primera tienda tendría que estar certificada en un mes.

---

## 2. Qué expone la DGII

### 2.1 Ambientes

| Ambiente | Ruta | Para qué |
|---|---|---|
| Pre-certificación | `testecf` | Pruebas libres. Guarda los envíos 60 días. Cada contribuyente habilitado tiene, por tipo, la secuencia 1 a 10,000,000 (la 32, de 1 a 50,000,000), con vencimiento 31-12-2028; la 32 y la 34 no vencen. Tiene un **simulador de otro contribuyente** (Emisor-Receptor). |
| Certificación | `certecf` | El set de pruebas oficial del proceso de certificación. |
| Producción | `ecf` | Todo envío tiene validez fiscal. |

En cada URL base hay un documento Swagger/OpenAPI con los recursos. Los servicios responden en JSON o XML según el `accept`.

### 2.2 Servicios de la DGII

`{amb}` = `testecf`, `certecf` o `ecf`. Todos, menos la semilla y las consultas de timbre (QR), piden `Authorization: Bearer {token}`. Los envíos son `multipart/form-data` con el campo `xml` (`type=text/xml`), y el archivo se llama `RNC + e-NCF + .xml` (por ejemplo `101672919E310000000001.xml`).

| Servicio | Método y recurso | URL base | Ambientes publicados |
|---|---|---|---|
| Autenticación: semilla | `GET /api/autenticacion/semilla` | `https://ecf.dgii.gov.do/{amb}/autenticacion` | los 3 |
| Autenticación: validar semilla | `POST /api/autenticacion/validarsemilla` (campo `xml`) | ídem | los 3 |
| Recepción de e-CF | `POST /api/facturaselectronicas` | `https://ecf.dgii.gov.do/{amb}/recepcion` | los 3 |
| Recepción de RFCE (32 < RD$250,000) | `POST /api/recepcion/ecf` | `https://fc.dgii.gov.do/{amb}/recepcionfc` | los 3 |
| Consulta de RFCE | `GET /api/Consultas/Consulta?RNC_Emisor=&ENCF=&Cod_Seguridad_eCF=` | `https://fc.dgii.gov.do/{amb}/consultarfce` | los 3 |
| Consulta de resultado (por TrackId) | `GET /api/consultas/estado?trackid=` | `https://ecf.dgii.gov.do/{amb}/consultaresultado` | los 3 |
| Consulta de estado (receptor o emisor) | `GET /api/consultas/estado?rncemisor=&ncfelectronico=&rnccomprador=&codigoseguridad=` | `https://ecf.dgii.gov.do/{amb}/consultaestado` | TesteCF, eCF |
| Consulta de TrackIds de un e-NCF | `GET /api/trackids/consulta?rncemisor=&encf=` | `https://ecf.dgii.gov.do/{amb}/consultatrackids` | TesteCF, eCF |
| Recepción de aprobación comercial | `POST /api/aprobacioncomercial` | `https://ecf.dgii.gov.do/{amb}/aprobacioncomercial` | los 3 |
| Anulación de e-NCF | `POST /api/operaciones/anularrango` | `https://ecf.dgii.gov.do/{amb}/anulacionrangos` | TesteCF, eCF |
| Directorio de facturadores | `GET /api/consultas/listado` y `GET /api/consultas/obtenerdirectorioporrnc?RNC=` | `https://ecf.dgii.gov.do/{amb}/consultadirectorio` | TesteCF, eCF |
| Timbre (QR del e-CF) | página pública con parámetros | `https://ecf.dgii.gov.do/{amb}/consultatimbre` | los 3 |
| Timbre FC (QR de la 32 < RD$250,000) | página pública con parámetros | `https://fc.dgii.gov.do/{amb}/consultatimbrefc` | los 3 |
| Emisor-Receptor (simulador) | `/fe/autenticacion/api/semilla`, `/fe/autenticacion/api/validacioncertificado` y endpoints de emisión, recepción y consulta de acuses | `https://ecf.dgii.gov.do/testecf/emisorreceptor` | solo TesteCF |
| Estatus de servicios y ventanas de mantenimiento | existe (Informe Técnico §4.9 e Instructivo de Contingencia) | **no está en la Descripción Técnica de 2026 (verificar URL)** | — |

Las URL que la Descripción Técnica no publica para CerteCF (consulta de estado, TrackIds, anulación y directorio) se asumen no disponibles allí.

### 2.3 Autenticación

1. `GET …/autenticacion/api/autenticacion/semilla` devuelve `<SemillaModel><valor>…</valor><fecha>…</fecha></SemillaModel>` (XSD `Semilla v.1.0`).
2. Se firma ese XML (misma firma que un e-CF, §4) y se envía a `POST …/api/autenticacion/validarsemilla`.
3. Respuesta: `{ "token", "expira", "expedido" }` (fechas `yyyy-MM-ddTHH:mm:ssZ`). Hoy dura **1 hora** ("por el momento", dice la DGII): no hay que asumirla, hay que leer `expira`.
4. El token es de la **persona** dueña del certificado. Sirve para los RNC en los que esa persona está delegada (§4.2). Si no lo está, los envíos fallan con "El RNC del certificado no está delegado para realizar transacciones para el RNC".

### 2.4 Envío y resultado

**e-CF completo** (31, 33, 34 y 32 de RD$250,000 o más):
- `POST …/recepcion/api/facturaselectronicas` → `{ trackId, error, mensaje }`.
- `GET …/consultaresultado/api/consultas/estado?trackid=` → `{ trackId, codigo, estado, rnc, eNCF, secuenciaUtilizada, fechaRecepcion, mensajes[{valor, codigo}] }`.
- Códigos: **0** No encontrado, **1** Aceptado, **2** Rechazado, **3** En proceso (promedio de validación: 200 ms), **4** Aceptado condicional.
- Con un rechazo, `secuenciaUtilizada = false` significa que el e-NCF **se puede volver a usar** (certificado o firma inválida, XML que no cumple el XSD, firmante no delegado, e-NCF no autorizado o vencido, RNC que no es emisor electrónico, que no existe o está inactivo). Con `true`, el número quedó gastado.
- Si se envía dos veces el mismo e-NCF, la DGII da dos TrackIds: `consultatrackids` los devuelve todos. Sirve para saber si un envío que dio *timeout* llegó.

**Resumen de factura de consumo (RFCE)** (32 de menos de RD$250,000):
- `POST https://fc.dgii.gov.do/{amb}/recepcionfc/api/recepcion/ecf` → respuesta inmediata `{ codigo, estado, mensajes[], encf, secuenciaUtilizada }` con estado Aceptado, Aceptado condicional o Rechazado. No hay TrackId.
- `consultarfce` responde por RNC + e-NCF + código de seguridad.

### 2.5 Los demás servicios

- **Aprobación comercial:** el **receptor** de un e-CF manda su `ACECF` firmado al emisor y una copia a la DGII. La DGII solo recibe aprobaciones de e-CF ya aceptados. Respuesta: `{ mensaje[], estado, codigo }`.
- **Anulación de e-NCF (`ANECF`):** anula rangos **no usados** y e-CF **firmados que no se enviaron** ni a la DGII ni al receptor. Hasta 10 líneas por tipo y hasta 10,000 rangos por línea. Para la 32 verifica que el número no se usó por ninguna de las dos vías. Lo que ya se envió se corrige con nota de crédito.
- **Directorio:** lista de emisores electrónicos con `urlRecepcion`, `urlAceptacion` y `urlOpcional` (autenticación). En TesteCF devuelve las URL del simulador.
- **Timbre (QR):** la página que abre el cliente al escanear. No lleva token (§5).
- **Estatus de servicios:** disponibilidad y mantenimientos programados. La DGII también los publica en la página "Mantenimientos" del portal.

### 2.6 Lo que cada contribuyente tiene que publicar (rol de receptor)

Según la "Descripción Técnica Servicios Emisores Electrónicos", con la misma ruta para todos y solo el host distinto (`https://host/…`):

| Servicio | Recurso | Obligatorio | Respuesta |
|---|---|---|---|
| Recepción de e-CF | `POST /fe/recepcion/api/ecf` (campo `xml`) | **Sí** | XML `ARECF` **firmado**: `Estado` 0 recibido o 1 no recibido, con motivo 1 error de especificación, 2 error de firma, 3 envío duplicado o 4 RNC comprador no corresponde |
| Aprobación comercial | `POST /fe/aprobacioncomercial/api/ecf` (campo `xml`, `ACECF`) | **Sí** | HTTP 200 o 400 |
| Autenticación | `GET /fe/autenticacion/api/semilla` y `POST /fe/autenticacion/api/validacioncertificado` | Opcional | Token como el de la DGII. Si existe, los otros dos piden `Bearer` |

Reglas: SSL, puertos estándar, accesibles desde internet, **sin distinguir mayúsculas de minúsculas** y con un dominio que no esté en listas negras ni marcado como *proxy avoidance*. Las URL se registran en la certificación (pasos 1, 7 y 12) y luego en "Mantenimiento Directorio FE" de la OFV.

---

## 3. Formatos XML

### 3.1 Tipos de e-CF (todos con XSD versión 1.0)

| Tipo | Nombre | ¿V1 de Abastalia? |
|---|---|---|
| 31 | Factura de Crédito Fiscal Electrónica | **Sí** |
| 32 | Factura de Consumo Electrónica (completa si es de RD$250,000 o más; si es menor, se guarda completa y se envía el **RFCE**) | **Sí** |
| 33 | Nota de Débito Electrónica | **Sí** (poco uso; el tipo ya existe en el código) |
| 34 | Nota de Crédito Electrónica | **Sí** |
| 41 | Comprobante Electrónico de Compras | Después de V1. Útil para la carnicería que compra reses a productores sin RNC (hoy sería B11) |
| 43 | Gastos Menores | No |
| 44 | Regímenes Especiales | No (solo si un cliente lo pide) |
| 45 | Gubernamental | No |
| 46 | Exportaciones | No |
| 47 | Pagos al Exterior | No |

Otros formatos: `RFCE` (resumen de consumo), `ARECF` (acuse de recibo), `ACECF` (aprobación comercial), `ANECF` (anulación) y `SemillaModel`. El XSD del RFCE no declara `Signature`: la firma entra por el `xs:any` final.

**(verificar)** Si el set de pruebas de certificación pide tipos que la tienda no usa (41, 43, 44…). Por eso el generador del set (TK-155) se guía por las columnas del Excel de la DGII y no solo por los tipos de la caja.

### 3.2 Reglas que afectan al generador

- Raíz `ECF`, con `Encabezado` (`Version` 1.0, `IdDoc`, `Emisor`, `Comprador`, `InformacionesAdicionales`, `Transporte`, `Totales`, `OtraMoneda`), `DetallesItems`, `Subtotales`, `DescuentosORecargos`, `Paginacion`, `InformacionReferencia`, `FechaHoraFirma` y `Signature`. Cada campo tiene una obligatoriedad por tipo: 0 no va, 1 obligatorio, 2 condicional, 3 opcional.
- **No se envían etiquetas vacías**: provocan rechazos.
- UTF-8. Los caracteres `" ' < > &` (y `© € ®`) se escapan.
- Montos en DOP, punto decimal y sin separador de miles. Dos decimales, redondeando hacia arriba desde 5. El precio unitario admite 4 decimales.
- Tolerancia de ±1 por línea y global igual al número de líneas. Un descuadre mayor hace al e-CF **aceptado condicional**.
- Máximo 1,000 líneas, o 10,000 en la 32 de menos de RD$250,000. **Ojo:** el XSD de la 32 limita `Item` a 1,000; las notas 33 y 34 admiten 10,000 (§10.8).
- `FechaVencimientoSecuencia` es obligatoria en la 31 y la 33, y **no va** en la 32 ni en la 34.
- `TipoIngresos` (01 = ingresos por operaciones) es obligatorio en la 31, la 32, la 33 y la 34. `IndicadorMontoGravado` (1 si los precios de las líneas incluyen ITBIS) es condicional: va cuando hay ítems gravados. Los precios de Abastalia incluyen ITBIS, así que irá en 1.
- `FechaHoraFirma` en formato `dd-MM-AAAA HH:mm:ss`, hora de RD (GMT-4), y nunca posterior a la hora de la DGII: el reloj de la PC importa.
- Las notas de crédito llevan `InformacionReferencia`: `NCFModificado`, `FechaNCFModificado` y `CodigoModificacion` (1 anula, 2 corrige texto, 3 corrige montos, 4 reemplaza un NCF emitido en contingencia). Llevan además `IndicadorNotaCredito` = 1 si pasaron más de 30 días: entonces no devuelven ITBIS.
- `IndicadorEnvioDiferido` es solo para contribuyentes autorizados a vender con dispositivos móviles sin conexión. No aplica.

---

## 4. Firma digital y certificado

### 4.1 Cómo se firma (instructivo "Firmado de e-CF" y Descripción Técnica de Emisores)

- XMLDSig **envuelta**: el `Signature` es el último hijo de la raíz.
- `CanonicalizationMethod` = `http://www.w3.org/TR/2001/REC-xml-c14n-20010315` (C14N 1.0 sin comentarios).
- `SignatureMethod` = `http://www.w3.org/2001/04/xmldsig-more#rsa-sha256` y `DigestMethod` = `http://www.w3.org/2001/04/xmlenc#sha256`.
- Una sola `Reference URI=""` con la transformación `enveloped-signature`.
- `KeyInfo/X509Data/X509Certificate` con el certificado del firmante.
- Se firma **sin preservar espacios** (`PreserveWhitespace = false`). Una vez firmado, el XML **no se toca**: ni se reformatea ni se le agrega nada.
- El campo **SN** (*serialNumber*) del sujeto del certificado debe ser la cédula, el RNC o el pasaporte del dueño del certificado.
- La DGII publica un ejemplo en C# con `SignedXml`. Abastalia usa `System.Security.Cryptography.Xml` (MIT, ya listado en la arquitectura) **sin** la bandera `Exportable` que usa ese ejemplo: con una llave no exportable del almacén de Windows firma igual.

Se firman con el mismo método el e-CF, el RFCE, el ANECF, el ACECF, el ARECF y la semilla.

### 4.2 El certificado: de quién es, quién lo emite y cuánto cuesta

- Es un **Certificado Digital de Persona Física para Procedimientos Tributarios** (NG 10-2021 art. 13; Informe Técnico §3). No hay certificado "de empresa": la factura sale a nombre del RNC y la firma una **persona**.
- Esa persona debe estar **vinculada al RNC como representante** y tener el rol de **Firmante Autorizado**, que el **Administrador e-CF** delega en la OFV ("Delegación de Roles", firmando un XML de delegación). Los roles son Administrador, Solicitante, Firmante y Aprobador Comercial.
- Lo emiten las entidades de certificación autorizadas por INDOTEL. Con resolución vigente están **Avansi S.R.L.** (tecnología Viafirma) y la **Cámara de Comercio y Producción de Santo Domingo (Digifirma)**. Validan la identidad con biometría. Vigencia de 1 o 2 años.
- **Costo (verificar):** no encontramos tarifas oficiales. Una fuente secundaria estima entre US$30 y US$70 al año. La DGII regala certificados por 1 año, pero **solo para su Facturador Gratuito**: no sirven con Abastalia.
- **(verificar)** Que el emisor entregue un **archivo .p12/.pfx** con la llave. Un certificado en token USB o en la nube del emisor no serviría para firmar sin conexión ni como servicio de Windows.
- La DGII no cobra por certificar ni por documento enviado.

---

## 5. Código de seguridad, QR y representación impresa (RI)

- **Código de seguridad:** los primeros **6 caracteres** del `SignatureValue` del e-CF, en Base64. Se imprime "en palabras" debajo del QR. Para la 32 de menos de RD$250,000 es el del **e-CF completo** y viaja dentro del RFCE (`CodigoSeguridadeCF`).
- **QR del e-CF** (31, 33, 34 y 32 de RD$250,000 o más): `https://ecf.dgii.gov.do/{amb}/consultatimbre?RncEmisor=…&RncComprador=…&ENCF=…&FechaEmision=dd-MM-AAAA&MontoTotal=…&FechaFirma=dd-MM-AAAA%20HH:mm:ss&CodigoSeguridad=…`
- **QR de la 32 de menos de RD$250,000:** `https://fc.dgii.gov.do/{amb}/consultatimbrefc?RncEmisor=…&ENCF=…&MontoTotal=…&CodigoSeguridad=…`
- El código de seguridad se escapa en la URL. Base64 puede traer `+`, `/` y `=`, y la DGII da su tabla de reemplazos.
- QR **versión 8** de al menos **22 × 22 mm**, con 3 mm de margen, abajo a la izquierda (en dispositivos *handheld*, abajo al centro) y a 2 cm del borde.
- Debajo del QR van el código de seguridad y la **fecha y hora de firma**. Arriba a la derecha: el tipo de e-CF en palabras, el e-NCF, la **fecha de vencimiento de la secuencia** (31 y 33) y, en las notas, el e-NCF modificado y el código de modificación en palabras. Arriba a la izquierda, los datos del emisor: nombre comercial, razón social, RNC, dirección, municipio, provincia y fecha de emisión. Después, el cliente (en la 32 de menos de RD$250,000, el RNC del comprador es opcional).
- La RI de un e-CF que la DGII no ha recibido o rechazó **no sirve para crédito fiscal**.
- **Contingencia:** la RI lleva la leyenda "e-CF emitido en modalidad de Contingencia, el cual podrá ser consultado para su validez fiscal, a partir de las setenta y dos (72) horas."
- La DGII publica modelos ilustrativos (PDF), incluida la 32 en **papel continuo**, que es el caso del rollo térmico.
- **Logo del negocio (TK-280):** los modelos ilustrativos dejan el logo del emisor arriba a la izquierda, sobre sus datos. Abastalia lo pone ahí en la RI A4 (máx. 60 × 20 mm) y arriba y al centro en el rollo térmico, antes del nombre. No reemplaza ni mueve ningún campo obligatorio ni el QR. Es el logo de hoy, no el del día de la emisión: la RI se arma con el XML firmado, y el logo no va en el XML.

---

## 6. Contingencia y plazo de 72 horas (Decreto 587-24, arts. 40–43)

| Caso | Qué hace el emisor | Plazo |
|---|---|---|
| **Sin conexión** (puede firmar, no puede enviar) | Emite e-CF "offline", entrega la RI con la leyenda de contingencia y los envía al volver la conexión | **72 horas** desde la emisión |
| **No puede emitir e-CF** (sin certificado, PC dañada, software caído) | Lo declara en la OFV (contingencia total o parcial), usa **comprobantes no electrónicos serie B** autorizados y declara la salida al terminar | Máximo **15 días calendario** |
| Regularización después de la anterior | Emite e-CF que **reemplazan** los B (`CodigoModificacion` 4) y los envía **solo a la DGII** | **30 días calendario** desde la salida |
| **La DGII no está disponible** | Guarda los e-CF y los envía al volver el servicio | Si pasa de 15 días hábiles, la OFV habilita reportes alternos |

Para Abastalia:
- La cola de 72 horas ya existe (`EcfSubmissionQueue`, arquitectura §7.3). Hay que sumarle el ciclo TrackId → resultado y el RFCE.
- **Certificado vencido o PC principal dañada** es "imposibilidad de emitir". Hoy la venta fallaría con `Fiscal.CertificadoNoDisponible`. Hace falta un **modo serie B** con su regularización (TK-157). El aviso de 30 días antes del vencimiento del certificado es la defensa principal.
- No existe un modo "vender sin firmar y firmar después": el código de seguridad sale de la firma y va impreso. Con certificado, la tienda **siempre firma al vender**.

---

## 7. Factura de consumo: resumen o e-CF completo

| Monto total de la 32 | Qué se envía | Servicio | QR | Comprador |
|---|---|---|---|---|
| **Menos de RD$250,000** | **RFCE** firmado (encabezado, totales, formas de pago y código de seguridad del e-CF) | `fc.dgii.gov.do/{amb}/recepcionfc` (respuesta inmediata) | `consultatimbrefc` | Opcional |
| **RD$250,000 o más** | e-CF completo | `recepcion` + TrackId | `consultatimbre` | Obligatorio (ya validado en `FiscalDocuments.IssueAsync`) |

Aunque se mande el resumen, **el e-CF completo firmado se conserva 10 años** y debe estar disponible para consulta. Abastalia ya lo guarda en `fiscal.EcfDocuments.SignedXml` (gzip) y nunca lo borra.

Flujo: armar el e-CF 32 completo → firmarlo → código de seguridad → armar el RFCE con ese código → firmar el RFCE → enviarlo. Las dos firmas son locales.

---

## 8. Certificación: los pasos y qué automatiza Abastalia

Requisitos del contribuyente: RNC activo, clave de la OFV, alta de NCF, certificado del representante que actuará como **Usuario Administrador e-CF**, estar al día y tener un software de e-CF. En el formulario el software se declara "adquirido a un proveedor externo", con los datos de Abastalia como proveedor.

| # | Paso del portal de certificación | Quién | Qué hace Abastalia |
|---|---|---|---|
| 0 | Solicitud FI-GDF-016 en la OFV; la DGII responde por el buzón con el acceso al portal | Dueño | Lista de pasos con enlaces |
| — | Pruebas libres en **TesteCF** (antes de postularse) | Abastalia | Carga automática del rango de pruebas, prueba de humo: autenticación, una 31, una 32 por RFCE y una 34 |
| 1 | Postulación: nombre y versión del software, URL de Recepción, Aprobación y Autenticación (opcional); se descarga un XML, se firma y se sube | Dueño + Abastalia | Muestra las URL de su tienda; **firma el XML descargado** con el certificado instalado (herramienta "Firmar archivo de la DGII") |
| 2 | Pruebas de datos e-CF: se descarga un **Excel** con el set y se generan y envían los XML **con los mismos campos y en el mismo orden**; un rechazo reinicia el set | Abastalia | Importa el Excel, genera, firma y envía cada fila a CerteCF (o el RFCE cuando corresponde) y sigue cada TrackId hasta el final |
| 3 | Pruebas de datos de aprobaciones comerciales (otro Excel) | Abastalia | Genera, firma y envía los `ACECF` |
| 4 | Simulación: e-CF con datos reales del negocio | Abastalia | Emite un lote representativo en CerteCF |
| 5–6 | Subir el **PDF de la RI** de cada e-CF de la simulación (máximo 10 MB) | Dueño + Abastalia | Genera los PDF con el mismo diseño del recibo |
| 7 | Confirmar las URL de prueba | Dueño | Las muestra |
| 8–9 | Comunicación: la DGII manda e-CF a la URL de Recepción y espera **acuses firmados** | Nube de Abastalia | Servicio de receptor en el ambiente `certecf` |
| 10–11 | La DGII manda aprobaciones comerciales a la URL de Aprobación | Nube de Abastalia | Las recibe, valida y responde |
| 12 | URL de producción | Dueño | Las muestra |
| 13 | **Declaración jurada**: descargar el XML, firmarlo y subirlo | Dueño + Abastalia | Lo firma con "Firmar archivo de la DGII" |
| 14 | La DGII verifica el estado del RNC | DGII | — |
| — | Autorización: se habilita el menú FE en la OFV. El dueño pide secuencias e-NCF de producción y registra las URL en "Mantenimiento Directorio FE" | Dueño | Asistente para cargar los rangos (ya existe) y cambiar el ambiente a `eCF` |

Abastalia **no** entra al portal ni a la OFV con las credenciales del dueño: los pasos marcados "Dueño" los hace él en su navegador, y Abastalia prepara y firma los archivos. Meta: que una tienda pequeña complete los pasos 2 a 11 en una tarde.

Más adelante, si Abastalia se certifica como **Proveedor de Servicios de FE (NG 10-2021)**, sus clientes solo harían las pruebas de simulación (art. 15). Requiere que Abastalia sea emisor electrónico, el SLA, la mesa de ayuda y recertificar con cada versión nueva del XML. Queda fuera de este diseño: ver §11, pregunta 1.

---

## 9. Obligaciones legales y operativas

- **Conservación:** e-CF, RFCE, acuses y aprobaciones durante **10 años** (Código Tributario art. 50 h; NG 01-2020 art. 16), legibles y exhibibles. La RI en papel, legible 10 años.
- **e-NCF:** `E` + tipo (2 dígitos) + secuencial (10 dígitos). La DGII los autoriza por tipo **en la OFV** y vencen el 31 de diciembre del año siguiente. Los sobrantes se anulan con ANECF. No se reutilizan, salvo un rechazo con `secuenciaUtilizada = false`.
- **Correcciones:** solo con notas de crédito o débito. Un e-CF **rechazado por la DGII no es válido**: no se corrige con nota de crédito, se emite **un e-CF nuevo que lo sustituye**. La arquitectura actual (§7.3, "bandeja de rechazos") dice lo contrario y hay que corregirla.
- **Rechazo comercial** del comprador: el emisor anula con nota de crédito a la DGII y al receptor.
- **Receptor:** acusar recibo de todo e-CF recibido. La aprobación comercial es opcional.
- **Contingencia:** declararla en la OFV cuando no se pueda emitir, con un máximo de 15 días.
- **Sanciones:** Ley 32-23 art. 27 y Código Tributario art. 257.
- **Plazo de adopción:** pequeños, micro y no clasificados hasta el **15 de noviembre de 2026**, después de una prórroga de 6 meses que dio la DGII en mayo de 2026 (verificar el número del aviso).

---

## 10. Cómo encaja en Abastalia

### 10.1 Qué tiene hoy el código y qué falta

| Pieza actual | Estado | Falta |
|---|---|---|
| `EcfXml` (antes `FiscalDocuments.BuildXml`) | ✅ TK-143: formato oficial, validado contra el XSD antes de firmar (§10.8) | Municipio y provincia (opcionales en el XSD) |
| `SimulatedEcfSigner` | Hash, no firma; sigue siendo el valor por defecto | ✅ TK-142: `CertificateEcfSigner` con XMLDSig (§4.1, §10.8). ✅ TK-176: importación del `.p12` y aviso de vencimiento (§10.10) |
| `IEcfProvider.SubmitAsync` | Una llamada devuelve el estado final | TrackId → consulta de resultado ("En proceso"), RFCE inmediato, consulta antes de reenviar y anulación |
| `RelayEcfProvider` y `/relay/v1/ecf` | La nube enviaría a la DGII | Pasa a ser archivo (§10.4) |
| `SubmissionStatus` | `Pending, Accepted, AcceptedConditional, Rejected, Simulated` | `Sent` (con TrackId, esperando resultado); además, en el documento: ambiente, canal (e-CF o RFCE), código y mensajes de la DGII, `secuenciaUtilizada` y entrega al receptor |
| `NcfSequence.ExpiresOn` | Obligatorio | Opcional en la 32 y la 34; rango por **ambiente** |
| `QrUrl` | Siempre `…/ecf/ConsultaTimbre` con todos los parámetros | Por ambiente y por tipo: `consultatimbrefc` para la 32 de menos de RD$250,000 (cierra el pendiente 14 de R3) |
| Bandeja de rechazos | Propone una nota de crédito | Propone un **e-CF de reemplazo** |
| Receptor (recepción y aprobación comercial) | No existe | Servicio en la nube por tenant (§10.3) |
| Configuración del negocio | RNC y nombre | Dirección, municipio y provincia (códigos de la Tabla III del formato) y nombre comercial |

### 10.2 Dónde se firma

**En la PC principal (nodo), dentro de la transacción de la venta**, como ya decide la arquitectura (§7.2). Es la única opción que cumple la contingencia "sin conexión": la RI necesita el código de seguridad en el momento, y ese código sale de la firma. Firmar en la nube dejaría a la tienda sin factura válida cada vez que se cae el internet, que es justo lo que pasa en un colmado.

La nube firma solo lo que la tienda no puede atender 24/7: los **acuses de recibo** (ARECF), las **aprobaciones comerciales** (ACECF) que pida el dueño y las **semillas** de autenticación de esas operaciones.

### 10.3 Dónde viven el certificado y su contraseña

| Opción | Emitir sin internet | Receptor 24/7 | Custodia por Abastalia | Riesgo |
|---|---|---|---|---|
| **A.** Solo en la PC (almacén `LocalMachine\My`, llave no exportable) | Sí | **No**: la PC se apaga de noche y no tiene IP pública. Habría que pasar cada acuse por un túnel a la tienda | No | Si la tienda está cerrada o sin internet, los suplidores no pueden entregar e-CF y la prueba de comunicación de la certificación depende de que la PC esté encendida |
| **B.** Solo en Key Vault | **No** | Sí | Sí | Pierde la contingencia: inaceptable en una tienda |
| **C.** **En los dos**: la PC firma la emisión; Key Vault firma acuses, aprobaciones y semillas de la nube | Sí | Sí | Sí, con consentimiento | Dos lugares donde vive la llave. Se mitiga abajo |

**Contraseña:** el dueño la escribe **una sola vez**, en la pantalla de Configuración → Comprobantes de la PC principal, al importar el `.p12`. Abastalia importa la llave como **no exportable**, le da permiso de lectura solo a la cuenta del servicio y descarta el archivo y la contraseña. Nunca se guardan: el `appsettings` no tiene secretos (arquitectura §5). Con consentimiento, el mismo paso sube el `.p12` por el canal autenticado del nodo (TLS) y la nube lo importa en Key Vault, uno por tenant y no exportable. Tampoco ahí se guarda el archivo ni la contraseña.

**Consentimiento (opción C):** una pantalla clara, en español, que firman el dueño y el titular del certificado. Dice que Abastalia guarda una copia **no exportable** del certificado, que **solo** firma acuses de recibo, aprobaciones comerciales que el dueño pida y semillas de autenticación, y **nunca** facturas, notas ni anulaciones; que cada firma queda registrada y el dueño puede verla en el panel; y que puede retirar el permiso cuando quiera (la llave se borra; con la protección de purga de Key Vault queda en borrado suave e inaccesible durante su periodo de retención **(verificar el periodo que se configura)**). Base: Ley 172-13 (datos personales), Ley 126-02 (el titular responde por su firma) y NG 10-2021 art. 14 d (almacenamiento seguro de certificados).

**Mitigaciones:** un firmador de nube (`KeyVaultEcfSigner`) que **rechaza en código** cualquier raíz distinta de `ARECF`, `ACECF` y `SemillaModel`; identidad administrada; una llave por tenant; registro de cada firma (fecha, tipo, e-NCF y hash) replicado al panel; alerta si cambia el certificado. En la PC: llave no exportable; como endurecimiento opcional, importarla al proveedor TPM (`Microsoft Platform Crypto Provider`) **(probar)**.

**Si el dueño no da el consentimiento:** opción A con túnel. La nube recibe el e-CF del suplidor, lo pasa por la conexión saliente del nodo, la PC firma el acuse y la nube lo devuelve. Con la tienda apagada, la nube responde HTTP 503 y el suplidor reintenta o entrega la RI en papel. Se construye solo si algún dueño lo pide (TK-159).

> **TK-246 (16/10/2026, ADR-0011): esta sección queda superada.** La opción A con firma por el canal del nodo ("StoreSigned") es la de **por defecto**; la C (copia en Key Vault, "CloudCustody") queda en el código, apagada por `Fe:AllowCloudCustody` hasta que se cumplan las condiciones del consejo de ingeniería (ADR-0011). En los dos modos la nube solo firma ARECF y ACECF (nunca `SemillaModel`) y toda llamada a la DGII o a un suplidor sale de la tienda. Detalle en §10.13.
>
> **Texto del consentimiento en StoreSigned (`2026-10-tienda`):** la nube recibe los comprobantes de los suplidores y le pide a la PC principal que firme **solo** el acuse de recibo de cada uno con el certificado instalado allí; el certificado **no sale** de la tienda; la PC firma una prueba de que el RNC es del negocio; las aprobaciones comerciales las firma y las envía la PC; con la PC apagada el suplidor recibe "intente de nuevo". Lo aceptan el dueño y el titular del certificado.

### 10.4 Quién habla con la DGII

**La PC principal, directo**, con su propio token (semilla firmada localmente). Motivos:
- Tiene el certificado. La API key de un proveedor era la razón del relevo (ADR-0008); sin proveedor, esa razón desaparece.
- El plazo de 72 horas depende solo del internet de la tienda y de la DGII, **no de que nuestra nube esté arriba**. Una caída de Abastalia no deja a todas las tiendas fuera de plazo.
- Hay un salto menos y un solo emisor por documento, sin riesgo de doble envío.

La nube habla con la DGII solo para su papel de receptor (y para mandar a la DGII la copia de una aprobación comercial).

El **relevo** (`/relay/v1/ecf`) cambia de papel: ya no reenvía, **archiva**. La PC sube el XML firmado, el RFCE y la respuesta de la DGII, y así la nube guarda la copia de 10 años fuera de la tienda y el panel muestra el estado fiscal. Se renombra para que no confunda (`/fiscal/v1/archive`).

### 10.5 Cambios en el código (para los tickets)

- **`IEcfSigner`**: `Sign(XDocument) → (SignedXml, SecurityCode)` se queda. Se suman `CertStoreEcfSigner` (nodo) y `KeyVaultEcfSigner` (nube, con lista blanca de raíces). El `RSA` de Key Vault se usa con `SignedXml` a través de una subclase de `RSA` que delega `SignHash` a `CryptographyClient`.
- **XML**: `EcfXmlBuilder` por tipo según el formato, `RfceBuilder` a partir del e-CF firmado y `AnecfBuilder`. Los XSD oficiales van embebidos y se valida **antes de firmar**: un XML que no cumple el XSD no se firma ni gasta número. Las pruebas de snapshot y XSD ya están previstas en `Abastalia.Fiscal.Tests`.
- **`DgiiClient`** (nodo): una tabla de URL por ambiente (`Fiscal:Environment` = `TesteCF` | `CerteCF` | `eCF`), una caché de token por ambiente que renueva 5 minutos antes de `expira` o al recibir un 401, y envíos `multipart` con el nombre `RNC+eNCF.xml`. Cliente HTTP con *timeouts* cortos y sin reintentos propios: los reintentos son de la cola.
- **`IEcfProvider`** pasa a cubrir enviar un e-CF (→ TrackId), consultar el resultado, enviar un RFCE (→ resultado), buscar TrackIds o RFCE por e-NCF (para dudas por *timeout*) y anular rangos. Implementaciones: `SimulatedEcfProvider` (demo, pruebas y tiendas aún no certificadas) y `DgiiEcfProvider`. `RelayEcfProvider` y `ICloudEcfProvider` se retiran.
- **Cola** (`EcfSubmissionQueue`): `Pending` → (RFCE) `Accepted` / `AcceptedConditional` / `Rejected`, o → (e-CF) `Sent` con TrackId → consulta cada pocos segundos → estado final. Ante un *timeout*, primero se consulta por e-NCF y después se reenvía. Sigue ordenando por `SubmitDeadlineUtc` y con los mismos reintentos. Después de `Accepted`, una 31 a un comprador electrónico pasa a **entrega al receptor**: directorio por RNC, `POST` a su `urlRecepcion` y se guarda el `ARECF`.
- **Rechazos**: con `secuenciaUtilizada = false`, se corrige y se vuelve a emitir **con el mismo e-NCF** (la RI ya entregada queda con otro código: caso raro si se valida el XSD antes de firmar). Con `true`, se emite un **e-CF de reemplazo** con número nuevo para la misma venta. Un aceptado condicional se guarda como válido y avisa.
- **Secuencias**: una columna `Environment`. `ExpiresOn` opcional en la 32 y la 34. En TesteCF, rango 1–10,000,000 cargado solo. Al pasar a `eCF`, lo emitido en pruebas queda marcado como no fiscal y nunca se envía a producción.
- **Nube**: módulo `Fe` en `Abastalia.Host.Cloud` con `/{amb}/{rnc}/fe/recepcion/api/ecf` y `/{amb}/{rnc}/fe/aprobacioncomercial/api/ecf` (la DGII deja libre el host y el prefijo: **verificar en TesteCF** que acepta un prefijo de ruta; si no, un subdominio por tenant). XML endurecido (sin DTD, sin resolver), límite de tamaño, verificación de la firma del emisor, `RNCComprador` = RNC del tenant, duplicados por (RNC emisor, e-NCF) y acuse firmado. Lo recibido se guarda y **baja a la tienda** por Sync como "comprobantes recibidos de suplidores", la base para llenar compras y el 606.
- **Configuración del negocio**: dirección, municipio, provincia y nombre comercial para el emisor y la RI.

### 10.6 Qué funciona sin internet

| Operación | Sin internet |
|---|---|
| Reservar e-NCF, armar, validar y firmar e-CF y RFCE | Sí |
| Imprimir la RI con QR, código de seguridad y leyenda de contingencia | Sí |
| Emitir notas de crédito y débito | Sí |
| Armar y firmar un ANECF | Sí (se envía después) |
| Pedir token, enviar a la DGII, consultar resultados | No: cola de 72 h |
| Entregar una 31 a un comprador electrónico | No: se hace después de `Accepted` |
| Recibir e-CF de suplidores y aprobaciones comerciales | Sí, **en la nube**, independiente de la tienda (opción C) |
| Emitir con el certificado vencido o la PC dañada | No: contingencia serie B (TK-157) |

**Cuándo imprimir la leyenda de contingencia:** la venta nunca espera a la DGII. El nodo lleva un estado "conectado con la DGII" según el último envío o token: si está desconectado o la cola tiene pendientes vencidos, la RI sale con la leyenda. Para la **31** (el cliente quiere el crédito fiscal), la caja espera hasta unos 5 segundos el resultado antes de imprimir, y si no llega imprime con la leyenda **(validar con el dueño)**.

### 10.7 Recomendación

1. **Firma de emisión en la PC principal**, con el `.p12` importado no exportable en `LocalMachine\My`. Es la única forma de cumplir la contingencia sin conexión que exige el Decreto 587-24.
2. **La PC le habla directo a la DGII.** Sin proveedor no hay API key que proteger, y el plazo de 72 horas no debe depender de nuestra nube.
3. **Servicios de receptor en la nube**, uno por tenant y por ambiente, con una **copia del certificado en Key Vault, solo con consentimiento** y firma limitada por código a acuses, aprobaciones y semillas. Sin esto la tienda no pasa las pruebas de comunicación ni puede recibir e-CF de sus suplidores.
4. **El relevo pasa a archivo** de 10 años y alimenta el panel.
5. **Certificación asistida** dentro de Abastalia (Excel → XML → CerteCF → resultados → PDF), con el dueño haciendo solo lo que exige su clave de la OFV.
6. **No ser PSFE por ahora.** Reevaluar cuando haya más de ~20 tiendas: acorta la certificación de cada cliente, pero trae SLA, mesa de ayuda y la responsabilidad del art. 14.
7. Mantener `SimulatedEcfProvider` y `SimulatedEcfSigner` como valores por defecto hasta que cada tienda esté certificada, igual que hoy.

### 10.8 Implementado: firma (TK-142) y XML (TK-143)

> 15 de octubre de 2026. Sin llamadas a la DGII (eso es TK-144) y sin cambios en el QR ni en la RI (TK-148).

**XSD.** Los cuatro XSD oficiales (`e-CF 31 v.1.0.xsd` a `e-CF 34 v.1.0.xsd`) están en `src/Modules/Fiscal/Abastalia.Fiscal/Schemas/`, con su nombre original, la URL de descarga y el SHA-256 en el `README.md` de esa carpeta. Van embebidos y se compilan una vez por tipo (cada XSD repite los mismos tipos globales, así que no caben en un solo `XmlSchemaSet`).

**XML (`EcfXml.Build`).** Sale del mismo `DocumentToIssue` de siempre, con el orden de elementos del XSD y sin etiquetas vacías:
- `Version` 1.0; `IdDoc` con `FechaVencimientoSecuencia` (solo 31 y 33, del rango usado), `IndicadorNotaCredito` (solo 34: 1 si pasaron más de 30 días desde el e-CF modificado), `IndicadorMontoGravado` = 1 si hay ítems gravados, `TipoIngresos` = 01, `TipoPago` y `TablaFormasPago` (no en la 34).
- `Emisor`: RNC, razón social, **dirección** (obligatoria en el XSD: se toma de Configuración → Negocio) y `FechaEmision`.
- `Totales` por tasa (`MontoGravadoI1/I2/I3`, `MontoExento`, `ITBIS1/2/3`, `TotalITBIS1/2/3`). El monto gravado es sin ITBIS. El ITBIS es **el de la venta** y no `gravado × tasa`, para que el e-CF cuadre al centavo con la venta; la diferencia, si existe, es de centavos y cae en la tolerancia de la DGII.
- Ítems con `IndicadorFacturacion`, `IndicadorBienoServicio` = 1, `PrecioUnitarioItem` hasta 4 decimales y el descuento en `DescuentoMonto` + `TablaSubDescuento` (`$`).
- 33 y 34: `InformacionReferencia` con `NCFModificado`, `FechaNCFModificado` (se busca en `fiscal.EcfDocuments`; si el e-NCF no existe, error `Fiscal.ReferenceNotFound`) y `CodigoModificacion`.
- `FechaHoraFirma` = el momento de la emisión en hora de RD (`dd-MM-yyyy HH:mm:ss`), dentro del contenido firmado.
- Las formas de pago de la venta pasan al e-CF: efectivo 1, transferencia 2, tarjeta 3, fiado 4 y saldo a favor 8 (`DocumentToIssue.Payments`, nuevo).
- Si la 31 no trae nombre del comprador, se toma del padrón de la DGII (`fiscal.Taxpayers`).

**Validación antes de firmar.** Con el firmador real, un XML que no cumple el XSD devuelve `Fiscal.InvalidXml`: no se firma y la venta hace *rollback* (el número vuelve). Sin dirección del negocio, `Fiscal.MissingIssuerAddress`. Con el firmador simulado no se valida, para que una tienda sin dirección siga vendiendo; sus documentos nunca se envían.

**Firma (`CertificateEcfSigner`).** `Fiscal:Signer` = `Simulated` (por defecto) | `Certificate`. El certificado sale de `LocalMachine\My` por `Fiscal:Certificate:Thumbprint`, o, solo para desarrollo y pruebas, de un `.p12`/`.pfx` con `Fiscal:Certificate:Path` y `Fiscal:Certificate:Password`. Firma XMLDSig envuelta con `SignedXml`: C14N 1.0, RSA-SHA256, digest SHA-256, una `Reference URI=""` con `enveloped-signature`, `KeyInfo/X509Data/X509Certificate`, sin preservar espacios, y agrega la declaración `<?xml version="1.0" encoding="utf-8"?>` (fuera de la canonicalización). El código de seguridad son los 6 primeros caracteres del `SignatureValue`. Falta de TK-142: la pantalla de importación del `.p12` (contraseña una vez, llave no exportable) y el aviso de vencimiento a 30 días.

**Pruebas.** `Abastalia.UnitTests/Fiscal`: una 32 con exento + 18 % + 16 %, descuento y dos formas de pago; una 32 de RD$250,000 o más con comprador; una 31 con RNC y un peso de 3 decimales; la 34 con 10 y 45 días (indicador 0 y 1) y una anulación sin comprador; una 33; la 31 sin comprador que falla el XSD; escape y corte de textos; firma de una 32 y una 31 con un certificado autofirmado (`CertificateRequest`), verificada con `SignedXml.CheckSignature`, con el código de seguridad y el XSD después de firmar; una firma rota al cambiar un monto; carga desde `.pfx`. `Abastalia.IntegrationTests/Fiscal/CertificateSigningTests`: `IssueAsync` con el firmador real (dirección, firma, fecha del e-NCF modificado y referencia inexistente).

**Diferencias encontradas en los XSD reales** (frente a §3 y al PDF del formato):

| # | Qué dice el XSD | Qué hace Abastalia |
|---|---|---|
| 1 | La firma es **obligatoria** en el XSD: el último elemento es `xs:any minOccurs="1"` (sin namespace fijo). Un XML sin firmar no pasa el XSD | Antes de firmar se valida una copia con un `<Signature>` vacío en su lugar (`EcfXml.ValidateUnsigned`); después de firmar se valida el XML real |
| 2 | `CantidadItem` admite **2 decimales** (`Decimal18D1or2`). La carnicería pesa con 3 | El peso se redondea a 2 decimales y el precio unitario (4 decimales) se recalcula para que `Precio × Cantidad − Descuento = MontoItem`. El recibo conserva el peso exacto. **Preguntar a la DGII** |
| 3 | En la 31 y la 32 el área `Comprador` es obligatoria, aunque en la 32 todos sus campos son opcionales | Una 32 a consumidor anónimo lleva `<Comprador />` vacío: la única etiqueta vacía. Esa 32 no viaja completa (va por RFCE, TK-145); de RD$250,000 en adelante el comprador es obligatorio. **Confirmar con la DGII** |
| 4 | El XSD de la 32 limita `Item` a **1,000** líneas (el formato habla de 10,000 para la 32 de menos de RD$250,000); 33 y 34 admiten 10,000 | Sin cambio: una carnicería no llega a 1,000 líneas |
| 5 | En la 33 `FechaVencimientoSecuencia` es obligatoria; en la 33 y la 34, `InformacionReferencia` y su `FechaNCFModificado` también (el formato la describe como condicional al reemplazo de contingencia) | Se envían siempre en las notas |
| 6 | En la 33 y la 34 `TipoIngresos` y `Comprador` son opcionales en el XSD (el PDF marca `TipoIngresos` obligatorio) | `TipoIngresos` = 01 siempre; `Comprador` solo si hay datos |
| 7 | La 34 no tiene `TablaFormasPago` ni `TerminoPago` | No se envían |
| 8 | `Municipio` y `Provincia` del emisor son opcionales (Tabla III, 582 códigos); `TelefonoEmisor` exige `xxx-xxx-xxxx` | No se envían todavía: hacen falta para la RI (TK-148), no para el XSD |
| 9 | `FechaLimitePago` es condicional a `TipoPago` = 2 (crédito) | No se envía: los clientes no tienen plazo de pago. **Preguntar** si su ausencia da aceptado condicional |
| 10 | Los XSD no tienen `targetNamespace`: los elementos van sin namespace | Así se generan |

### 10.9 Implementado: envío directo a la DGII (TK-144 a TK-150)

> 15 de octubre de 2026. **Todo probado contra una DGII simulada** (`FakeDgii` en `Abastalia.IntegrationTests`), hecha con los contratos de §2. No tenemos acceso a TesteCF ni certificado real (TK-141): nada de esto ha hablado todavía con la DGII de verdad. Lo marcado **(probar en TesteCF)** es lo primero que hay que confirmar cuando llegue el acceso.

**Configuración.** `Fiscal:Provider` = `Simulated` (por defecto) | `Dgii`. Con `Dgii` hace falta `Fiscal:Signer` = `Certificate` (la autenticación es la semilla firmada; si no, la API no arranca). `Fiscal:Dgii:Environment` = `TesteCF` (por defecto) | `CerteCF` | `eCF` elige las URL de §2.2 (`ecf.dgii.gov.do/{testecf|certecf|ecf}` y `fc.dgii.gov.do/{…}`). `Fiscal:Dgii:TimeoutSeconds` (30). El valor `Relay` ya no existe.

**TK-144 · `DgiiClient`** (`Abastalia.Fiscal/DgiiClient.cs`).
- Semilla (`GET …/autenticacion/api/autenticacion/semilla`) → se firma con el mismo `CertificateEcfSigner` del e-CF → `POST …/validarsemilla` (multipart, campo `xml`) → `{ token, expira }`. El token se guarda en memoria y se renueva **5 minutos antes de `expira`** o cuando la DGII responde 401 (se reintenta una sola vez). Nunca se escribe el token ni el XML en los logs.
- Operaciones: recepción de e-CF, consulta de resultado por TrackId, recepción de RFCE, consulta de RFCE, TrackIds por e-NCF, anulación de rangos, directorio por RNC y copia de la aprobación comercial (estas dos últimas quedan listas para TK-153 y TK-154, sin uso todavía).
- Envíos `multipart/form-data`, campo `xml`, `text/xml`, archivo `RNC + e-NCF + .xml`.
- Errores: red, *timeout*, 5xx, 429 o un 401 que se repite **lanzan** excepción y la cola reintenta; un 4xx con mensaje de la DGII es un **rechazo** con su código y mensajes (`codigo: valor`, unidos en una línea).
- CerteCF no publica TrackIds por e-NCF ni anulación: allí `FindAsync` no busca y la anulación responde rechazada.

**TK-145 · RFCE y umbral.** Una 32 de menos de RD$250,000 se firma completa y, en la misma transacción, se arma el **RFCE** copiando del e-CF firmado `IdDoc` (tipo, e-NCF, `TipoIngresos`, `TipoPago`, formas de pago), `Emisor` (RNC, razón social, fecha), `Comprador` (vacío si es anónimo), los totales que admite el XSD y `CodigoSeguridadeCF` = código del e-CF. Se valida contra `RFCE 32 v.1.0.xsd` antes de firmarlo y se guarda en `fiscal.EcfDocuments.RfceXml` (gzip) junto al e-CF completo, que se conserva igual. Canal en `EcfDocuments.Channel` (`Ecf` | `Rfce`). La 32 de RD$250,000 o más va completa por recepción.

**TK-146 · Cola directa y archivo.** `EcfSubmissionQueue`:
- `Pending` → e-CF a recepción → `Sent` con TrackId → consulta cada 5 s, 10 s, 20 s… (máximo 5 minutos) mientras la DGII diga "en proceso" o "no encontrado" → `Accepted` / `AcceptedConditional` / `Rejected` con `DgiiCode`, `DgiiMessages`, `SequenceUsed`, `SentUtc` y `ResultUtc`.
- RFCE: `Pending` → recepcionfc → estado final en la misma llamada.
- Un fallo espera 1, 2, 4… hasta 60 minutos. **Antes de reenviar después de un fallo**, pregunta a la DGII si ya lo tiene (TrackIds por e-NCF; consultarfce para el RFCE): un *timeout* que sí llegó no se manda dos veces.
- Orden por plazo de 72 horas, un `SaveChanges` por documento, nunca bloqueado por la licencia.
- **Archivo en la nube:** con el proveedor `Dgii`, cada documento con resultado final se sube a `POST /fiscal/v1/archive` (identidad del nodo): `{ encf, environment, status, trackId, dgiiCode, messages, signedUtc, signedXmlGzip, rfceGzip, replacesENcf, replacedByENcf }`. La nube lo guarda (tabla `EcfRelays`, que conserva su nombre) y un segundo envío del mismo e-NCF actualiza la fila. Sin conexión a la nube o con la nube caída no pasa nada: se intenta en la siguiente vuelta y nunca frena el envío a la DGII. `RelayEcfProvider`, `ICloudEcfProvider` y `/relay/v1/ecf` se retiraron (ADR-0008 marcado).
- Salud `ecf-queue`: pendiente de más de 24 h → Degraded; de más de 60 h → Unhealthy; rechazado sin reemplazo en los últimos 30 días o enviado hace más de 1 h sin resultado → Degraded.
- `GET /api/fiscal/queue` suma `sent`, el ambiente actual y cuenta como rechazados solo los que no tienen reemplazo.

**TK-147 · Rechazos → reemplazo.** `GET /api/fiscal/documents?rejectedOnly=true` es la lista "Rechazados por la DGII" (con código, mensajes y `secuenciaUtilizada`). `POST /api/fiscal/documents/{id}/replace` emite un **e-CF nuevo con los mismos datos** (comprador, líneas, totales, pagos, referencia) y un **e-NCF nuevo**; cambian solo el número, la fecha de emisión, la fecha y hora de firma y el vencimiento del rango. Se valida, se firma (y su RFCE si es una 32 pequeña), entra a la cola y queda ligado en los dos sentidos (`ReplacesENcf` / `ReplacedByENcf`), con auditoría `fiscal.replace-rejected`. El evento `FiscalDocumentReplacedV1` hace que la venta (o la anulación, o la devolución) con el número rechazado pase al nuevo: la reimpresión y el 607 usan el e-CF vigente. **No hay nota de crédito.** Pendiente: la pantalla en Escuadra Admin (hoy solo API), el aviso de aceptado condicional y que el cargo de fiado del cliente (módulo Clientes) también pase al e-NCF nuevo (hoy conserva el rechazado).

**TK-148 · QR y representación impresa.** El QR depende del canal y del ambiente: `https://ecf.dgii.gov.do/{amb}/consultatimbre?RncEmisor&RncComprador&ENCF&FechaEmision&MontoTotal&FechaFirma&CodigoSeguridad` para el e-CF completo y `https://fc.dgii.gov.do/{amb}/consultatimbrefc?RncEmisor&ENCF&MontoTotal&CodigoSeguridad` para la 32 de menos de RD$250,000; el código de seguridad y la fecha de firma van escapados. Un comprobante simulado apunta a `testecf`, nunca a producción. El modelo del recibo (`Receipt`) suma `SignedAt` (fecha y hora de firma bajo el QR), `SequenceExpiresOn` ("Válido hasta", solo en la 31) y `ContingencyLegend`. La leyenda sale cuando, al firmar, hay en la cola un documento con un envío fallido o con más de 5 minutos sin enviar; queda guardada en la venta para la reimpresión. Ya se muestra en el diálogo del recibo de la caja. Pendiente: dibujar el QR (versión 8, 22 mm) en el ESC/POS (TK-118), el PDF de la RI para la certificación (TK-155) y esperar hasta 5 s el resultado de una 31 antes de imprimir (validar con el dueño).

**TK-149 · Secuencias por ambiente.** `Environment` (`Simulated` | `TesteCF` | `CerteCF` | `eCF`) en `EcfSequences` y `EcfDocuments`; la migración `DgiiDirect` deja lo existente como `Simulated`. El ambiente actual es `Simulated` si el firmador o el proveedor lo son; si no, el de `Fiscal:Dgii:Environment`. Solo se reservan números del ambiente actual. `ExpiresOn` es opcional en la 32 y la 34 y obligatorio en la 31 y la 33 (`Fiscal.ExpiryRequired`). En TesteCF, el primer uso de un tipo carga solo el rango de la DGII (1 a 10,000,000; la 32 hasta 50,000,000; vence 31-12-2028 la 31 y la 33). `GET /api/fiscal/sequences` dice el ambiente de cada rango y si es el actual. El 607 deja fuera lo emitido en TesteCF y CerteCF (el 606 es de compras: no aplica).

**TK-363 · Cuándo se piden las secuencias.** Las secuencias reales las autoriza la DGII en la OFV, y solo después de la certificación; por eso no se piden al instalar ni en medio de una venta. Flujo:
- **Simulado y TesteCF** (tienda nueva, práctica, pruebas): el primer uso de cada tipo carga solo su rango. La caja vende desde el primer día y el Resumen no avisa de tipos que nunca se usaron.
- **Pasar a CerteCF o Producción:** el cambio de ambiente (Comprobantes electrónicos → Ambiente) se bloquea hasta que estén cargadas la 32 (cada venta) y la 34 (devoluciones y anulaciones) de ese ambiente; es el único momento en que se piden, y lo hace el dueño. La 31 no bloquea: solo hace falta para clientes con RNC.
- **Durante el uso:** el Resumen avisa con tiempo (menos del 20 % o 30 días para vencer) y lleva a Configuración → Comprobantes. Si aun así se acaban en la caja, la venta no se pierde: sin 31 el cajero puede cobrarla como consumo (F6); sin 32 la venta queda abierta y el mensaje dice que el dueño la carga en Administración → Configuración → Comprobantes.

**TK-150 · ANECF.** `POST /api/fiscal/sequences/{id}/void` (cuerpo opcional `{ "to": … }`) anula desde el próximo número sin usar hasta `to` (por defecto, el final del rango): mueve el rango más allá de esos números en la misma operación atómica (si una venta tomó el número entretanto, falla en vez de anular uno usado), arma el ANECF según `ANECF v.1.0.xsd`, lo valida, lo firma y lo deja a la cola, que lo envía a `anulacionrangos`. Auditoría `fiscal.void-sequence`; `GET /api/fiscal/voids` lista los envíos con la respuesta. Pendiente: la pantalla con vista previa y la anulación de e-CF firmados que nunca salieron.

**Pruebas.** Unitarias: RFCE, reemplazo y ANECF validados contra sus XSD, QR por canal y ambiente. Con la DGII simulada: semilla firmada y validada contra `Semilla v.1.0.xsd`, token reutilizado y renovado a 5 minutos de `expira` y ante un 401, caída que lanza, códigos 0–4, `secuenciaUtilizada`, hosts y nombres de archivo; la cola (fallo → consulta antes de reenviar → `Sent` → "en proceso" → resultado; *timeout* que llegó; RFCE inmediato; rechazo final); de punta a punta con la API real y el firmador real en TesteCF: una 32 pequeña por RFCE, una 31 rechazada y reemplazada (la venta pasa al nuevo e-NCF), una 32 de RD$300,000 por recepción, un solo token, archivo en la nube, 607 vacío en TesteCF, ANECF aceptado y contingencia (leyenda, envío al volver la DGII, salud Degraded a las 24 h).

**Falta probar en TesteCF** (con TK-141): la forma real de cada respuesta (nombres de campos, `codigo` como número, `mensajes` como lista de `{ valor, codigo }`), que la semilla firmada así sea aceptada, la vida real del token, el RFCE aceptado y su QR `consultatimbrefc`, el nombre de archivo y la respuesta de `anularrango`, el código 0 justo después de recibir un TrackId y los límites de envío por minuto.

### 10.10 Implementado: pantallas, anulación de firmados, fiado y certificado (TK-204 a TK-206, TK-209, TK-176)

> 15 de octubre de 2026. Igual que §10.9: probado con la DGII simulada, nada contra TesteCF todavía.

**TK-204 · Pantalla "Comprobantes electrónicos"** (Escuadra Admin, `/admin/ecf`, en el menú para quien tiene `fiscal.manage`). Arriba, el ambiente (Simulado, Pre-certificación (TesteCF), Certificación (CerteCF) o Producción) y, fuera de producción, el aviso de que esos comprobantes no tienen valor fiscal. La cola en tres cifras: por enviar (pendientes y esperando a la DGII), fuera de plazo de 72 h y rechazados sin reemplazo. **Rechazados por la DGII** con el mensaje de la DGII y **Reemplazar** (llama a `POST /api/fiscal/documents/{id}/replace`); **Últimos comprobantes** (los 50 más recientes) con su estado, el error o el mensaje de la DGII, y **Anular** en los que nunca salieron. **Anular números sin usar** (ANECF): se elige la secuencia del ambiente actual, el "hasta" (vacío = el final), se ve antes cuántos números y cuáles se anulan, y la lista de anulaciones con su respuesta. Toda acción que no se puede deshacer pide un segundo toque, como revocar un equipo. Lo puede hacer quien tenga `fiscal.manage` (dueño o supervisor con ese permiso); no hay aprobación de supervisor aparte porque el cajero no llega a esta pantalla. El Resumen ("Para revisar") avisa en rojo cuando la DGII rechazó comprobantes y lleva a la pantalla. En Configuración → Comprobantes la secuencia sin vencimiento ya no rompe la lista (la 32 y la 34 pueden no tenerlo) y un rango de otro ambiente dice de cuál es.

**TK-205 · Anular un e-CF firmado que nunca salió.** La DGII lo permite con el mismo ANECF de los rangos (§2.5: "e-CF firmados que no se enviaron ni a la DGII ni al receptor"); lo que ya llegó se corrige con nota de crédito. `POST /api/fiscal/documents/{id}/void`:
- Solo un documento `Pending` (o `Simulated`) sin `SentUtc` ni TrackId; si tiene notas de crédito o débito que lo modifican, no (`Fiscal.HasNotes`).
- Si un envío anterior falló (`Attempts > 0`), pudo haber llegado: primero pregunta a la DGII (TrackIds por e-NCF, o `consultarfce` para el RFCE), igual que la cola antes de reenviar. Si la DGII lo tiene, `Fiscal.AlreadySent`; si la DGII no responde, `Fiscal.DgiiUnreachable` y no se anula.
- Pasa a `Voided` en una actualización atómica que exige que siga como se leyó: si la cola lo envió entretanto, falla en vez de anular uno que la DGII tiene. La cola nunca envía un `Voided`.
- Arma el ANECF de ese único número, lo valida contra el XSD, lo firma y lo deja a la cola (`EcfVoids`, ligado a su secuencia); auditoría `fiscal.void-document`.
- Si la venta sigue en pie, **Reemplazar** también acepta un `Voided`: e-CF nuevo con los mismos datos, la venta (y el fiado) pasan al número nuevo.
- Pendiente (§11 #26): el recibo que ya tiene el cliente sigue mostrando el número anulado; el 607 de una venta cuyo e-CF se anuló y no se reemplazó.

**TK-206 · El fiado sigue al reemplazo.** El módulo Clientes escucha `FiscalDocumentReplacedV1`: el cargo a crédito de esa venta (`OnAccountCharges.Document`) pasa del e-NCF rechazado al nuevo, así el estado de cuenta del cliente muestra el comprobante válido, igual que la venta y su recibo.

**TK-209 · Migraciones en SQL Server.** Toda prueba de integración con `ABASTALIA_TEST_DB=sqlserver` (el job `sqlserver` de CI, o un SQL Express local con `ABASTALIA_TEST_SQLSERVER`) ya crea la base aplicando las migraciones de cada módulo. Faltaba la prueba de **actualizar** una tienda con datos: `SqlServerUpgradeTests` migra Fiscal hasta `InitialFiscal`, inserta un rango y un documento como los dejaba la V1, aplica `DgiiDirect` y comprueba que quedan `Simulated`, canal `Ecf`, sin contingencia y con el XML intacto; luego escribe lo nuevo (32 sin vencimiento, RFCE de 20 KB, `Voided`, un ANECF). Pasó contra SQL Express junto con todas las demás pruebas de integración, que aplican también Sales `FiscalReceiptFields`.

**TK-176 · Certificado digital** (Configuración → Comprobantes → "Certificado digital"). El dueño elige el `.p12`/`.pfx` y escribe la contraseña **una vez**: `POST /api/fiscal/certificate` (`{ file: base64, password }`) lo abre, exige llave privada RSA y que no esté vencido, y lo guarda; la contraseña no se guarda en ningún lado ni entra a la auditoría (`fiscal.import-certificate` registra titular, RNC, huella y vencimiento). Dónde queda:
- **PC de la tienda** (Windows en `Production`, o `Fiscal:Certificate:ImportTo` = `Store`): importado **no exportable** en `LocalMachine\My`. Si la cuenta del servicio no puede escribir ese almacén (lo normal con `NT SERVICE\Abastalia` sin el paso del instalador), en `CurrentUser\My` de esa cuenta, que también sobrevive reinicios.
- **Desarrollo y pruebas** (`ImportTo` = `KeysFolder`, el valor fuera de producción): en la carpeta de llaves (`Abastalia:KeysFolder`), cifrado con Data Protection (`ecf-certificate.pfx.protected`).
- En los dos casos se escribe `ecf-certificate.json` en la carpeta de llaves con `Fiscal:Certificate:Thumbprint` y `Fiscal:Certificate:Store`; al arrancar, ese archivo manda sobre `appsettings`. El firmador toma el certificado vigente en cada firma: una **renovación** firma desde la venta siguiente, sin reiniciar.
- Si la tienda sigue con `Fiscal:Signer` = `Simulated`, el certificado queda guardado ("En uso: guardado") y se usa al pasar a la DGII (TK-141); importar no cambia de firmador ni de ambiente.

`GET /api/fiscal/certificate` muestra titular, RNC o cédula (del `SERIALNUMBER` u `organizationIdentifier` del sujeto), emisor, vencimiento, días que quedan y si está en uso. **Aviso a 30 días** (`ExpiresSoon`): en la tarjeta del certificado, en "Para revisar" del Resumen (ámbar, con "Importar certificado") y en la barra de estado de la caja para quien tiene `fiscal.manage` ("Certificado digital vence en N días", ámbar, principio 3); `GET /api/fiscal/queue` trae `certificateExpiresUtc` para que la barra no haga otra llamada.

**Pruebas.** Integración con la DGII simulada (`EcfAdminTests`): anular un e-CF que nunca salió (DGII caída → no se anula; DGII de vuelta → ANECF de un número validado contra el XSD, la cola no lo envía, el reemplazo lleva la venta al número nuevo; uno aceptado no se anula), un envío que llegó aunque se perdió la respuesta no se anula, el estado de cuenta del fiado con el e-NCF de reemplazo, y un certificado renovado que firma la venta siguiente. `EcfCertificateTests`: contraseña equivocada, archivo que no es un certificado, vencido, sin permiso, importado con RNC y aviso a 20 días, `certificateExpiresUtc` en la cola, recargado tras un reinicio y sin la contraseña en disco. Contrato del cliente para las rutas nuevas. bUnit: reemplazar con dos toques, anular un pendiente, vista previa del ANECF y los avisos del Resumen.

### 10.11 Implementado: certificación, operación y contingencia (TK-155 a TK-157, R8 #4 y #20)

> 15 de octubre de 2026. Igual que §10.9: probado con la DGII simulada (`FakeDgii`), nada contra TesteCF ni CerteCF todavía (TK-141). Todo en Escuadra Admin → "Comprobantes electrónicos", debajo de lo de TK-204.

**TK-155 · Asistente de certificación** (tarjeta "Certificación con la DGII").
- Los 14 pasos del portal (§8) con una casilla cada uno; el progreso se guarda (`fiscal.CertificationSteps`, `PUT /api/fiscal/certification/steps/{n}`).
- **Set de pruebas:** `POST /api/fiscal/certification/test-set` (`{ file: base64, name }`) acepta el Excel (.xlsx, con ClosedXML) o un CSV. Generador genérico: recorre el XSD oficial del tipo de cada fila (`TipoeCF`) en su orden y llena cada elemento cuya columna existe (reglas y supuestos en `TestSet` y §11 #30); un elemento compuesto sin valores se omite salvo que el XSD lo exija (el `Comprador` vacío de la 32). Cada fila se valida contra el XSD, se firma y, si es una 32 de menos de RD$250,000, se le arma su RFCE. Todo o nada: una fila que no se puede armar (un tipo sin XSD, como la 41) detiene la carga y se listan todos los problemas. Una carga nueva reemplaza el set anterior (un rechazo reinicia el set en la DGII). Solo 31 a 34 (§11 #5).
- **Enviar y consultar** (`POST /api/fiscal/certification/send`): solo con el proveedor `Dgii` en `CerteCF` (`Fiscal.NotCerteCF` si no). Envía las filas pendientes en el orden del Excel y consulta el resultado de las enviadas; el dueño lo pulsa hasta que todas tienen respuesta. No pasa por la cola de ventas ni toca las secuencias.
- **Simulación:** las ventas normales con el ambiente en CerteCF; la tarjeta cuenta aceptados, rechazados y en espera. El PDF de la RI (pasos 5–6) sigue pendiente (TK-148) y las URL de receptor llegan con TK-152.
- **Firmar archivo de la DGII** (`POST /api/fiscal/certification/sign`): firma con el certificado importado (aunque la tienda siga en modo simulado) el XML del portal, con vista previa antes y un segundo toque. Rechaza DTD, XML inválido y comprobantes (`ECF`, `RFCE`, `ANECF`, `ARECF`, `ACECF`, `SemillaModel`: `Fiscal.SignNotAllowed`). Auditoría `fiscal.sign-dgii-file` con el elemento raíz, el SHA-256 y la huella del certificado.

**TK-156 · Operación.** `GET /api/fiscal/health` (`fiscal.manage`) junta en una lista lo que el dueño tiene que atender: comprobantes sin enviar desde hace más de 24 h (ámbar) o 60 h (rojo), envíos fallidos con el último contacto con la DGII, no poder firmar (rojo) o certificado a 30 días, secuencias bajas, contingencia activa (día N de 15; rojo, y "pasó el límite" después) y comprobantes B por regularizar (30 días desde la salida). La misma lista alimenta el health check `ecf-operations` (Degraded, **nunca Unhealthy**: `/health` es también como la caja sabe si el servidor responde), la tarjeta "Salud de la operación" y el Resumen ("Para revisar", sin repetir el certificado ni las secuencias que ya avisaba). `POST /api/fiscal/dgii-status` pregunta en el momento si la DGII responde con la semilla de autenticación (pública) y 10 s de plazo: `Up`, `Down` (la DGII responde con error, o solo responde su host de estatus: intermitencia de la DGII) o `Unreachable` (ningún host de la DGII responde: el internet de la tienda). El servicio oficial "Estatus" pide una llave (§11 #6). Reloj (§11 #12, D17): con la hora de la PC a más de 2 minutos de la de la DGII (medida con el encabezado `Date` de sus respuestas) se deja de firmar sin frenar la venta; el e-CF espera sin firmar y la caja y la salud lo avisan (`clock`). La guía para el dueño está en `docs/contingencia-ecf.md` y, resumida, en el panel "Qué hacer si no se puede facturar con e-CF". Pendiente: leer la API de estatus cuando la DGII dé la llave (ticket de seguimiento), el receptor de la nube (TK-152) y avisos fuera de la pantalla (correo o WhatsApp).

**TK-157 · Contingencia por imposibilidad de emitir (serie B).**
- Rangos B cargados de antemano (`POST /api/fiscal/contingency/sequences`, `fiscal.PaperSequences`): `B` + 01/02/03/04 + 8 dígitos.
- `POST /api/fiscal/contingency/start` (con el motivo declarado en la OFV) solo cuando la tienda **no puede firmar** (`Fiscal.CanStillSign` si puede); `/end` la termina. Auditoría `fiscal.contingency-start` y `-end`.
- La venta: si no se puede firmar (sin certificado, vencido o todavía no válido) y no hay contingencia, `Fiscal.CannotSign` con qué hacer; con contingencia, toma el siguiente NCF B en la misma transacción (sin huecos) y guarda los datos del comprobante (`fiscal.PaperDocuments`). **Nunca se usa B mientras se pueda firmar**, aunque la contingencia siga activa. El recibo dice "NCF" y "Factura de Consumo" (sin "Electrónica"), sin código de seguridad, fecha de firma ni QR. La caja (cualquier usuario) ve "Contingencia: se facturan comprobantes serie B en papel" en la barra (`GET /api/fiscal/contingency`).
- `POST /api/fiscal/contingency/regularize`, con la contingencia terminada y el certificado de vuelta: por cada B, un e-CF del mismo tipo y datos con `InformacionReferencia` (`NCFModificado` = el B, `FechaNCFModificado`, `CodigoModificacion` 4) y `ReplacesENcf` = el B. Va por la cola a la DGII. Todo o nada (un error deshace la corrida y dice qué B falló). La venta conserva el NCF B que tiene el cliente. **TK-153 no debe entregar al comprador un e-CF cuyo `ReplacesENcf` empieza con `B`**: va solo a la DGII. Preguntas en §11 #31 y #32.

**R8 #4 y #20 · Certificado.** Al importar se rechaza un certificado de otro RNC (`Fiscal.CertificateWrongRnc`; una cédula en el certificado de una empresa se acepta: es la del Firmante delegado, §11 #33) o que todavía no es válido (`Fiscal.CertificateNotYetValid`). Si cargar el certificado falla (huella que no está en el almacén, archivo bloqueado), `EcfCertificate` lo vuelve a intentar a los 60 s en vez de quedarse sin certificado hasta reiniciar.

**Pruebas.** Integración: el ciclo completo de la serie B con el reloj simulado (no puede firmar → contingencia → B → certificado de vuelta y e-CF otra vez → 16 días → terminar → 31 días → regularizar, con el e-CF de reemplazo validado contra el XSD), la lista de salud con la DGII caída y de vuelta, el set de pruebas en CerteCF (CSV armado desde e-CF reales, ida y vuelta campo por campo y en orden, RFCE de la 32, rechazo de un tipo 41), firmar el XML del portal (la firma verifica; comprobantes y DTD rechazados), fuera de CerteCF y el R8 #4 y #20. bUnit: tarjeta de salud, contingencia con dos toques, asistente y el Resumen.

### 10.12 Implementado: receptor en la nube, entrega de la 31 y comprobantes recibidos (TK-151 a TK-154)

> 15 de octubre de 2026. Igual que §10.9: probado con la DGII, un suplidor y un comprador simulados, nada contra TesteCF ni con un Key Vault real todavía. Lo marcado **(probar en TesteCF)** o **(probar en Azure)** es lo primero cuando haya acceso.

**TK-151 · Consentimiento y copia del certificado.** En la tienda, Compras → "Recibidos (e-CF)" → "Receptor en la nube" (permiso `fiscal.manage`): el texto de §10.3, el `.p12`, su contraseña y los nombres del dueño y del titular, con la casilla de aceptación. `POST /api/fiscal/receiver/consent` lo pasa una vez, por el canal del nodo, a `POST /fiscal/v1/receiver/certificate` con el RNC del negocio y la versión del texto (`2026-10`); la tienda audita `fiscal.receiver-consent` (quién, RNC, titular; nunca la contraseña). La nube abre el `.p12` (llave RSA, vigente), lo importa **no exportable** (`Fe:Vault` = `KeyVault`, nombre `fe-{tenant}-{consentimiento}`) y guarda solo el consentimiento (`FeCertificates`: RNC, sujeto, huella, vencimiento, dueño, titular, versión, fecha, nodo). Un consentimiento nuevo retira el anterior. Un RNC activo en otra cuenta da `Fe.RncTaken`. Fuera de producción, `Fe:Vault` = `Local` lo guarda en memoria (la API no arranca así en `Production`). R9 #6: el certificado debe venir de una entidad de `Fe:TrustedRoots` (`Fe.CertificateUntrusted`) y traer el RNC del negocio o, en el de una empresa, la cédula del firmante delegado (`Fe.CertificateRncMismatch`); un índice único filtrado (`Rnc` con `RevokedUtc IS NULL`) impide dos receptores activos para un RNC aunque dos consentimientos lleguen a la vez, y el reemplazo del propio se hace en una transacción.
- **Firmador restringido** (`FeSigner`): rechaza en código toda raíz que no sea `ARECF`, `ACECF` o `SemillaModel` sin namespace; vuelve a leer el consentimiento antes de cada firma; la llave firma con `SignAsync` RS256 de Key Vault (`SignedXml` calcula el resumen y el valor de la firma se pone después; el certificado y el `CryptographyClient` quedan en memoria por consentimiento, así cada firma es una sola llamada al vault); cada firma queda en `FeSignatures` (fecha, raíz, e-NCF, SHA-256; el ARECF entero solo cuando el documento se recibió, R9 #2).
- **Retiro:** desde la tienda (`POST /api/fiscal/receiver/revoke`, auditado) o desde el panel (`POST /api/panel/fe/revoke`, solo Owner). Se marca primero (el receptor responde 503 y el firmador se niega desde ese momento) y luego se borra la llave (borrado suave con protección de purga, 30 días).
- **Panel:** `GET /api/panel/fe`: estado, las URL, las últimas 100 firmas y las aprobaciones que mandaron los compradores. Falta la pantalla en el panel web (hoy solo API).
- **Bicep:** `enablePurgeProtection` en el Key Vault (irreversible), roles "Key Vault Certificates Officer" y "Key Vault Crypto User" para la identidad administrada de la API, `Fe__Vault` y `Fe__KeyVaultUri`. **(probar en Azure)** la importación real, la firma con `CryptographyClient` y el borrado.

**TK-152 · Servicios de receptor** (`Abastalia.Host.Cloud/Fe/FeReceiver.cs`), por tenant y ambiente, sin distinguir mayúsculas:
- `GET /{amb}/{rnc}/fe/autenticacion/api/semilla` y `POST …/validacioncertificado`: semilla firmada por el emisor → token de 1 hora. Semilla y token son sellos de Data Protection (sin tabla, sirven en cualquier instancia) ligados al tenant y al ambiente, y vencen por el reloj de la app. Con `Fe:RequireToken` (true) los otros dos exigen `Bearer`.
- `POST /{amb}/{rnc}/fe/recepcion/api/ecf` (multipart `xml`, máximo 1 MB, sin DTD ni resolver): XSD del tipo (31 a 34, enlazados desde `Abastalia.Fiscal/Schemas`), firma del emisor (una sola `Signature` envuelta, último hijo, RSA-SHA256, `Reference URI=""`, y desde R9 #1 un certificado que encadena a una entidad de `Fe:TrustedRoots` y trae `RNCEmisor` o una cédula si el emisor es una empresa), `RNCComprador` = RNC del tenant y duplicados por (ambiente, RNC emisor, e-NCF, SHA-256 de los bytes). Responde el **ARECF firmado** (validado antes contra `ARECF v1.0.xsd`): Estado 0, o 1 con motivo 1 (especificación), 2 (firma), 3 (duplicado) o 4 (RNC comprador). Lo recibido se guarda en `FeReceived` (los bytes originales en gzip, en su codificación; emisor, e-NCF, tipo, fecha, total, ITBIS). El mismo e-NCF con **otro contenido** no es un duplicado: se guardan las dos versiones (hasta 3) marcadas `Conflict`, se acusan con Estado 0, y la tienda las muestra juntas sin "Registrar compra" hasta que el dueño confirme con el suplidor cuál vale. Sin XML legible con RNC emisor y e-NCF no hay acuse posible: 400.
- `POST /{amb}/{rnc}/fe/aprobacioncomercial/api/ecf`: ACECF de un comprador sobre un e-CF del tenant; XSD (`ACECF v.1.0.xsd`), firma de una entidad de confianza con el certificado de `RNCComprador` (R9 #1) y `RNCEmisor` = RNC del tenant; se guarda (`FeApprovalsReceived`, ligado por e-NCF al archivo) y responde 200, si no 400. La semilla de `validacioncertificado` también exige la cadena.
- **Confianza** (`FeTrust`, R9 #1): `Fe:TrustedRoots` es una lista de certificados en Base64 (DER) de las entidades de certificación de INDOTEL (Avansi y Cámara de Comercio y Producción de Santo Domingo / Digifirma), raíces e intermedios. Se cargan como `Fe__TrustedRoots__0`, `Fe__TrustedRoots__1`… en la configuración de la App Service: se bajan de la página de cada entidad (o se exportan de la cadena de un certificado real, TK-141) y se comprueba la huella con la entidad. **Sin ninguna raíz el receptor no confía en nadie** (todo e-CF se acusa con motivo 2, la semilla da 401 y el consentimiento `Fe.CertificateUntrusted`) y lo avisa en el log al arrancar. `Fe:CheckRevocation` (true) revisa la CRL u OCSP en línea y un estado desconocido es un rechazo; solo se apaga fuera de `Production` (las pruebas usan una entidad propia sin CRL).
  - **Cargadas en `appsettings.json` (16/10/2026),** descargadas de los sitios oficiales de cada entidad. Son certificados públicos. Huellas SHA-256:
    - AVANSI CERTIFICACION (raíz, hasta 2042), de https://cps.viafirma.do/CA003_AVANSICERTIFICACION.crt: `6820B751…D324CB`
    - AVANSI CERTIFICADOS DIGITALES (intermedia): `77C7CC9F…798867`
    - VIAFIRMA QUALIFIED CERTIFICATES 2021 (intermedia de Avansi): `D5A0F0D4…477E9D`
    - VIAFIRMA QTSP ROOT CA (raíz, España, hasta 2044): `00378D75…DEAF06`; su intermedia VIAFIRMA QUALIFIED CERTIFICATES 2023 (VATDO-130222509): `C7D04ABE…876021`
    - digifirma CA Raíz (Cámara de Comercio y Producción de Santo Domingo, hasta 2046), de https://digi.camarasantodomingo.do/Cadena/digifirmaCARaiz.crt: `4F26113C…2D9BF6`
    - digifirma CA Subordinada 1 (hasta 2034): `4B6C87D1…086095`
    - Falta confirmar con INDOTEL si hay otras entidades vigentes (OPTIC, Lleida.net, Novosit) que emitan certificados para e-CF; si un emisor firma con otra entidad, su e-CF se rechaza con motivo 2 y hay que agregar esa raíz.
- **Límites** (R9 #2): `Fe:RequestsPerMinutePerIp` (60) y `Fe:RequestsPerMinutePerRnc` (600) en todas las rutas del receptor, 429 al pasarse. La validación XSD corre en paralelo (el `XmlSchemaSet` compilado solo se lee).
- Sin consentimiento o con el tenant deshabilitado: 503 en todas las rutas, con un aviso en el log. Las URL para la DGII salen en el estado (`Fe:PublicUrl` o el host de la petición).
- Pendiente: prueba de carga de 20 documentos por segundo, el monitoreo de disponibilidad con alerta, el almacenamiento frío de lo recibido (hoy en SQL, `varbinary(max)`), y **(probar en TesteCF)** el simulador Emisor-Receptor y los pasos 8 a 11 de CerteCF.

**TK-153 · Entrega de la 31 al comprador** (`Abastalia.Fiscal/EcfExchange.cs`, `EcfDeliveryQueue`). Una 31 con RNC que la DGII acepta (o acepta condicional) entra a `fiscal.EcfDeliveries`. Directorio de la DGII por RNC (caché de un día): si no aparece, `NotElectronic` (se queda con la RI). Si aparece: semilla de su `urlOpcional` firmada con el certificado de la tienda → token → `POST` del e-CF firmado, sin tocar, a su `urlRecepcion` (`RNCEmisor + e-NCF + .xml`). Se guarda su ARECF (gzip) si es un ARECF firmado de ese e-NCF: Estado 0 o motivo 3 → `Delivered`; otro motivo → `NotReceived` con el motivo. Un fallo reintenta con la espera de la cola (1, 2, 4… 60 minutos) hasta 72 horas y queda `Failed`. Corre en su propio trabajador (`Fiscal:AutoSubmit`, cada 30 s), así un comprador caído nunca frena el envío a la DGII. `GET /api/fiscal/deliveries` lista las entregas. Pendiente: mostrarlas en "Comprobantes electrónicos" y **(probar en TesteCF)** con el simulador.

**TK-154 · Comprobantes recibidos en la tienda.** La tienda pide cada 30 s lo nuevo (`POST /fiscal/v1/received/pull`, cursor = id de la nube, de 100 en 100) y lo guarda en `fiscal.EcfReceived`. Compras → "Recibidos (e-CF)" (`/admin/purchasing/received`, permiso `purchasing.manage`) los lista con suplidor, e-NCF, fecha, total e ITBIS y un enlace a Compras. **Aprobación comercial** (opcional, `fiscal.manage`): `POST /api/fiscal/received/{id}/approval` → la nube arma el ACECF, lo valida contra el XSD, lo firma una sola vez (la decisión no cambia: `Fe.ApprovalAlreadySigned`), lo entrega al suplidor (directorio, su autenticación, su `urlAceptacion`) y la copia a la DGII (token con la semilla firmada en la nube); volver a tocar solo reintenta lo que no salió. Auditoría `fiscal.commercial-approval`. En CerteCF no hay directorio: va solo a la DGII. Pendiente: **"crear compra desde este e-CF"** (llenar suplidor, NCF y montos en el formulario de compra; hoy el enlace abre Compras y el NCF se escribe a mano, el índice de NCF ya impide duplicarla), el reintento automático de la aprobación y **(probar en TesteCF)** la aprobación aceptada.

**Pruebas.** `Abastalia.Cloud.Tests/FeReceiverTests` (con un e-CF 31 armado y firmado por el código de la tienda): 503 sin consentimiento, consentimiento incompleto y contraseña equivocada; acuse firmado y válido contra su XSD con el certificado del tenant, sin token 401, los cuatro motivos, lo que baja a la tienda (y nada a otro tenant), las firmas en el panel; el firmador que rechaza `ECF`, `RFCE`, `ANECF` y un `ARECF` con namespace; RNC tomado; retiro que deja el receptor en 503 al instante y borra la llave; aprobación firmada, válida, entregada con token al suplidor y a la DGII, no cambiable y sin reenvío de lo ya entregado; aprobaciones de compradores (válida, de otro emisor, alterada). `Abastalia.IntegrationTests/Fiscal/EcfExchangeTests` y `ReceivedDocumentsTests`: una 31 a un comprador electrónico (caído → reintento → entregada con su ARECF, el XML enviado igual al firmado), otra a uno no electrónico, una que se rinde a las 72 h; lo recibido que baja con el cursor, la aprobación por la nube y el consentimiento (RNC, versión, error de la nube con su código, sin nube conectada).

### 10.13 Implementado: el receptor sin custodia, firma en la tienda (TK-246)

> 16 de octubre de 2026. Decisión y consejo en ADR-0011. Probado con una tienda, un suplidor y la DGII simulados.

**Dos modos por tenant** (`FeCertificates.SigningMode`, migración `ReceiverSigningModes`; los receptores de antes quedan `CloudCustody`):
- **StoreSigned (por defecto, `Fe:DefaultSigningMode`).** Activación en la tienda (Compras → "Recibidos (e-CF)" → "Receptor en la nube", eligiendo "Esta PC"): `POST /fiscal/v1/receiver/challenge` da un reto (Data Protection, 5 minutos, de ese tenant); la tienda firma `ActivacionReceptor` (RNC + reto) con su certificado (`CertificateEcfSigner`) y lo manda a `POST /fiscal/v1/receiver/activate`. La nube revisa la firma, la cadena hasta `Fe:TrustedRoots` (con revocación), el vencimiento y que el certificado sea del RNC (o la cédula del firmante de una empresa): `Fe.ProofInvalid`, `Fe.CertificateUntrusted`, `Fe.CertificateRncMismatch`. Guarda solo sujeto, huella y vencimiento, y el nodo que activó (`ConsentedByNodeId`): ese nodo es el que firma. Esto responde la pregunta 36 para este modo.
- **CloudCustody (TK-151).** Igual que §10.12, pero solo si `Fe:AllowCloudCustody` es true (falso en producción hasta cumplir ADR-0011; `Fe.CloudCustodyUnavailable`). Pasar de B a A borra la llave del vault.
- **Firma por el canal del nodo** (`FeRemoteSigning.cs`): el receptor arma el ARECF como antes y `FeSigner` lo pone en la cola del nodo (`FeSignBroker`, en memoria) y espera hasta `Fe:SignTimeoutSeconds` (10). La PC sondea con `POST /fiscal/v1/receiver/sign-requests/next` (firmado como nodo, ADR-0008; espera hasta `Fe:SignPollSeconds`, 25; 404 `Fe.NotSigningNode` si esa PC no firma por el receptor) y contesta en `POST /fiscal/v1/receiver/sign-requests/{id}` con el XML firmado o el motivo del rechazo. La nube acepta la firma solo si es **el mismo documento** que pidió (sin la `Signature`, igual nodo por nodo), envuelta, con transformaciones envuelta/C14N y de un certificado de confianza del RNC del receptor.
- **En la tienda** (`Abastalia.Fiscal/RemoteSigning.cs`): `RemoteSigningWorker` mantiene el sondeo mientras la tienda está conectada y tiene certificado vigente (si no, espera un minuto). Solo firma raíces `ARECF` sin namespace, válidas contra `ARECF v1.0.xsd`, y con Estado 0 solo si `RNCComprador` es el RNC del negocio; `SemillaModel`, `ACECF`, un e-CF o cualquier otra cosa se niega. Cada solicitud queda en la auditoría (`fiscal.remote-sign` con raíz, e-NCF, emisor y SHA-256; `fiscal.remote-sign-refused` con el motivo).
- **Sin respuesta** (PC sin sondear en el último minuto, tiempo agotado o firma que no cuadra): **503 con `Retry-After: 60`**. Lo recibido y válido se guarda con `AcknowledgedUtc` nulo y baja a la tienda; el reintento del emisor con los mismos bytes se acusa con Estado 0 (no motivo 3). `FeLateAcknowledger` (cada 30 s, `Fe:LateAcksInBackground`) firma el ARECF de lo pendiente cuando la PC vuelve y lo guarda en `FeSignatures` **sin enviarlo** al emisor (pregunta 44). Cada tramo sin firma queda en `FeReceiverOutages` (se abre en la primera solicitud perdida y se cierra en el siguiente sondeo); `GET /api/panel/fe` devuelve `offline` (horas en 30 días por PC y desde cuándo sigue sin conexión) y el panel muestra "Receptor sin conexión". Los pasos 8 a 11 de la certificación (asistente de la tienda) piden la PC encendida.
- **Firmador restringido:** `FeSigner` solo acepta `ARECF` y `ACECF` en los dos modos. Se quitó `SemillaModel` (una semilla firmada es un token de la DGII para ese RNC) y con eso `FeOutbound`: la nube ya no llama a la DGII ni a suplidores.

**Aprobación comercial en la tienda, en los dos modos** (`Features/Receiver.cs`, `CommercialApprovals`): `POST /api/fiscal/received/{id}/approval` arma el ACECF (XSD `ACECF v.1.0.xsd`, que ahora vive en `Abastalia.Fiscal/Schemas` junto al de ARECF y la nube los enlaza), lo firma una vez con el certificado de la tienda (guardado en `fiscal.EcfReceived.ApprovalXmlGzip`, migración `ApprovalSignedAtStore`), lo entrega al suplidor (directorio de la DGII del ambiente del documento; su semilla firmada en la tienda; `TaxpayerReceiver`, el mismo camino que la entrega de la 31) y manda la copia a la DGII (`DgiiClient.SendCommercialApprovalAsync`). Volver a tocar reintenta solo lo que faltó; la decisión no cambia (`Fiscal.ApprovalAlreadySigned`). El outbox lleva `CommercialApprovalRecordedV1` y la nube lo proyecta en `FeReceived` (la primera decisión se queda; lo entregado solo crece). La nube no firma ni envía aprobaciones.

**TK-250 · Decidida desde el panel, firmada en la tienda.** En Panel → Recibidos el Dueño toca **Aceptar** o **Rechazar** (con motivo: `DetalleMotivoRechazo` es texto libre de hasta 250 caracteres, sin lista de códigos de la DGII; el panel sugiere algunos) y confirma. `POST /api/panel/fe/received/{id}/decision` (solo Owner, límite por minuto del panel) guarda la decisión en `FeReceived` (`PanelDecision`, `PanelReason`, `PanelDecidedBy`, `PanelDecidedUtc`; migración `PanelCommercialDecision`) con una sola actualización condicional (`Approval` y `PanelDecision` nulos): la primera gana, repetir la misma responde 200 sin cambios, otra da `Fe.AlreadyDecided`. Sin receptor activo: `Fe.ReceiverInactive`. No hay medidor de cuota: es una decisión por documento. `POST /fiscal/v1/received/pull` devuelve además `decisions` (las pendientes, hasta 20) **solo a la PC que activó el receptor** (tiene el certificado); la tienda las pasa a `CommercialApprovals` (auditoría `fiscal.commercial-approval` con `DecidedBy` = "panel: correo"), que firma, entrega al suplidor y a la DGII y emite `CommercialApprovalRecordedV1`; la nube la deja de enviar cuando el evento pone `Approval`. Si la tienda ya había decidido ese documento, la del panel se ignora y vale la de la tienda (el panel lo dice). Con la PC apagada la decisión espera y el panel muestra "Pendiente: se firmará en la tienda". No hay cuenta regresiva: no se conoce un plazo de la DGII para la aprobación comercial (§11, pregunta 45).

**Sondeo de firma sin receptor (auditoría).** Si la nube responde 404 (`Fe.NotSigningNode`: esta PC no firma por el receptor), la tienda espera 6 h antes del siguiente sondeo de `sign-requests/next` (antes, cada minuto: mantenía despierta la base de datos sin servidor). Activar el receptor en esta PC (StoreSigned) despierta el sondeo al instante (`RemoteSigningWake`).

**Bicep:** se quedan los roles de Key Vault de TK-151 (para B); `Fe__DefaultSigningMode = StoreSigned` y `Fe__AllowCloudCustody = false`.

**Pruebas.** `Abastalia.Cloud.Tests/FeReceiverTests`: activación con prueba firmada (sin cadena, otro RNC, reto inventado, de otro tenant, otro RNC en la prueba), cambio de B a A que borra la llave, B apagado por la plataforma; acuse firmado por la tienda con un nodo simulado, sin certificado ni llave en la nube; otra PC del tenant no firma ni contesta por la que firma; PC apagada → 503 con `Retry-After`, lo recibido pendiente, horas sin conexión en el panel, reintento acusado y acuse tardío guardado; PC que no contesta (espera el tiempo) o firma con otro RNC u otro documento → sin acuse; el firmador niega `SemillaModel`; la aprobación llega por evento. `Abastalia.IntegrationTests/Fiscal`: `RemoteSigningTests` (firma ARECF del negocio y el rechazo de otro RNC; niega receipt de otro negocio, semilla, ACECF, e-CF y basura; todo auditado; el sondeo va firmado como nodo) y `ReceivedDocumentsTests` (activación con la prueba firmada, CloudCustody con el `.p12`, aprobación firmada en la tienda, entregada con token al suplidor y a la DGII, nada por la nube, evento en el outbox, sin reenvío).

---

## 11. Riesgos y preguntas abiertas

| # | Riesgo o pregunta | Cómo se resuelve |
|---|---|---|
| 1 | **¿Custodiar copias de certificados y alojar los servicios de receptor de clientes convierte a Abastalia en "proveedor de servicios de FE" (NG 10-2021) sin estar certificado?** | Preguntar a la DGII (facturacionelectronica@dgii.gov.do, 809-689-3444 opción 4) antes de producción. TK-246: por defecto ya no se custodia (la tienda firma); la respuesta escrita es condición para encender CloudCustody (ADR-0011) |
| 2 | **Plazo del 15-11-2026**: un mes para certificar la primera tienda con un módulo que aún no existe | Decisión del dueño: piloto con TesteCF ya, priorizar TK-141 a TK-147 y TK-151 a TK-155, y tener un plan B (Facturador Gratuito de la DGII para poco volumen) |
| 3 | Acceso a TesteCF: requiere la solicitud FI-GDF-016 de un RNC real y un certificado. Sin eso no se prueba nada | TK-141: el RNC de Abastalia o el de la tienda piloto |
| 4 | Formato del certificado del emisor (`.p12` exportable o token/nube) y precio real | Cotizar con Avansi y Digifirma |
| 5 | Si el set de pruebas exige tipos que la tienda no usa (41, 43–47) | Ver el Excel en el portal; el generador del set es genérico por columnas |
| 6 | URL del servicio **Estatus de servicios / ventanas de mantenimiento** | **Encontrada (TK-156, 16/10/2026):** la DGII publica la "FE Estatus Servicios API" en `https://statusecf.dgii.gov.do` (Swagger en `https://statusecf.dgii.gov.do/help/index.html`, definición en `https://statusecf.dgii.gov.do/api-docs/v1/definition.json`): `GET /api/EstatusServicios/ObtenerEstatus` (cada servicio y si está disponible), `GET /api/EstatusServicios/ObtenerVentanasMantenimiento` y `GET /api/EstatusServicios/VerificarEstado?Ambiente=1\|2\|3`. Pide `Authorization: ApiKey {key}` (sin ella responde 401) y no documenta la forma de la respuesta ni qué número es cada ambiente. No hay página pública para personas. Abastalia no la lee todavía: "Consultar a la DGII" pide la semilla y, si nada responde, cualquier respuesta de `statusecf` (aunque sea el 401) separa "intermitencia de la DGII" de "esta tienda sin internet". Falta: preguntar a la DGII cómo se obtiene la llave (¿sirve el token de la semilla?), la forma de la respuesta y los códigos de ambiente; con eso, mostrar el estatus y las ventanas de mantenimiento en "Salud de la operación" (ticket de seguimiento) |
| 7 | Si la DGII acepta un **prefijo de ruta** por tenant en las URL de receptor o exige un host propio por contribuyente | Probar en TesteCF con el simulador Emisor-Receptor |
| 8 | Qué pasa cuando el receptor no acusa (nuestra nube caída o un tenant sin consentimiento): ¿hay sanción para el receptor? | Preguntar a la DGII |
| 9 | Vida real del token (hoy 1 h) y límites de envío por minuto | Leer `expira`; medir en TesteCF |
| 10 | `FechaVencimientoSecuencia` en la 32 y la 34 de producción (el formato dice que no va; el Informe dice que toda secuencia vence) | Ver la autorización real en la OFV |
| 11 | Rechazo de un RFCE ya entregado al cliente: volver a emitir con el mismo número cambia el código de seguridad del recibo | Validar el XSD antes de firmar; documentar el caso al dueño |
| 12 | El reloj de la PC: `FechaHoraFirma` posterior a la hora de la DGII causa rechazo | **Resuelto (TK-156, D17):** la hora de la DGII sale del encabezado HTTP `Date` de cada respuesta suya (semilla, token, recepción); la semilla se pide cada 10 minutos, y cada minuto mientras la hora está mal. Con más de `Fiscal:MaxClockSkewSeconds` (120) de diferencia, adelantada o atrasada, **no se firma**: la venta sigue, su e-CF queda guardado sin firmar (recibo sin código de seguridad ni QR, con la leyenda de contingencia) y se firma (`FechaHoraFirma` nueva; `FechaEmision` la de la venta) y se envía solo cuando la hora vuelve; la venta recibe el código y el QR (`FiscalDocumentSignedV1`). Aviso en la caja y en "Salud de la operación". Reemplazar, regularizar y anular (ANECF) se niegan (`Fiscal.ClockOff`). Sin medida (la DGII no ha respondido) no bloquea. Convive con R11 A18: con la hora 5 minutos atrás del último registro de auditoría no se guarda nada. Pendiente de confirmar con la DGII: que un recibo sin código mientras se corrige la hora es aceptable (como la contingencia de 72 h) |
| 13 | Robo de la llave en una PC de colmado (malware con permisos de administrador) | Llave no exportable, cuenta de servicio, TPM opcional, revocación con la entidad de certificación y alerta en el panel ante firmas extrañas |
| 14 | Cambio de versión del XML o de los servicios (la DGII separó la Descripción Técnica en 2026) | Revisar la "Bitácora" de cada documento cada mes; pruebas de contrato contra TesteCF (TK-158) |
| 15 | Una tienda sin nube (si algún plan lo permitiera) no puede tener servicios de receptor | Para e-CF, la conexión a la nube de Abastalia es obligatoria. Reflejarlo en *planes* (doc. privado) |
| 16 | Retención de la copia en la nube (10 años) y su costo | Almacenamiento frío (Blob *cool/archive*) por tenant; estimarlo en TK-146 |
| 17 | Ventas por peso con 3 decimales: el XSD solo admite 2 en `CantidadItem` (§10.8, diferencia 2) | Preguntar a la DGII si redondear el peso y recalcular el precio es lo esperado; probar en TesteCF |
| 18 | `<Comprador />` vacío en la 32 a consumidor anónimo frente a la regla de "no enviar etiquetas vacías" (§10.8, diferencia 3) | Preguntar a la DGII; probar una 32 completa sin comprador en TesteCF |
| 19 | Venta a crédito sin `FechaLimitePago`, y forma de pago del saldo a favor del cliente (¿7 nota de crédito u 8 otras?) | Preguntar a la DGII |
| 20 | Anulación (`anularrango`): qué nombre debe tener el archivo y qué responde (la Descripción Técnica no lo dice). Abastalia manda `RNC + ANECF + fecha.xml` y toma un 200 sin código como aceptado (§10.9) | Preguntar a la DGII; probar en TesteCF |
| 21 | e-CF de reemplazo de un rechazado: ¿lleva alguna referencia al rechazado (`InformacionReferencia`) o es un e-CF normal? ¿Fecha de emisión nueva o la de la venta? Con `secuenciaUtilizada = false`, ¿se prefiere reusar el mismo e-NCF o uno nuevo y anular el viejo con ANECF? Abastalia hoy: e-CF normal, fecha nueva, número nuevo | Preguntar a la DGII |
| 22 | QR de un e-CF sin comprador (una 34 a consumidor anónimo): ¿se omite `RncComprador` o va vacío? Abastalia lo omite | Preguntar a la DGII; probar el QR en TesteCF |
| 23 | Cuánto esperar con código 0 ("no encontrado") después de recibir un TrackId antes de reenviar. Abastalia sigue consultando (máximo cada 5 minutos) y no reenvía | Medir en TesteCF |
| 24 | En CerteCF no hay consulta de TrackIds por e-NCF: un *timeout* durante el set de pruebas obliga a reenviar. ¿Cuenta como envío duplicado en la certificación? | Preguntar a la DGII |
| 25 | Nota de crédito sobre una venta cuyo e-CF fue reemplazado: Abastalia la hace sobre el e-CF vigente (el reemplazo). Confirmar | Preguntar a la DGII |
| 26 | e-CF firmado y anulado con ANECF sin enviarse (TK-205) cuya representación impresa ya tiene el cliente: ¿basta con el ANECF o hay que avisar al comprador? Si la venta sigue en pie, Abastalia emite un reemplazo con número nuevo; si no se reemplaza, ¿cómo va esa venta en el 607 (hoy sale con el e-NCF anulado)? | Preguntar a la DGII; mientras, la pantalla recomienda reemplazar |
| 27 | ANECF de un solo número firmado (desde = hasta) dentro de un rango que se sigue usando: ¿lo acepta la DGII, o pide anular solo al final del rango? ¿Y para una 32 enviada por RFCE que falló (la DGII "verifica las dos vías")? | Probar en TesteCF |
| 28 | Dónde trae el certificado de la entidad (Avansi, Digifirma) el RNC o la cédula del titular: Abastalia lo lee del `SERIALNUMBER` u `organizationIdentifier` del sujeto. ¿Debe coincidir con el RNC del negocio para firmar e-CF de una persona jurídica? | Ver un certificado real (TK-141); preguntar a la DGII |
| 29 | La cuenta del servicio (`NT SERVICE\Abastalia`) no puede escribir `LocalMachine\My` ni crear llaves CNG de la máquina. R11 C8: la llave queda como llave no exportable de esa cuenta (en el TPM cuando la PC lo tiene), con ACL solo para ella y SYSTEM; la parte pública en `keys\ecf-certificate.cer`. Falta probarlo en una VM limpia con el servicio real | TK-160 (VM limpia) |
| 30 | Forma real del Excel del set de pruebas (TK-155): ¿las columnas se llaman como los elementos del XML, cómo numera los repetidos (`NombreItem[1]`), usa `#e` para "vacío", trae una hoja o varias, y cómo distingue un nombre que se repite bajo otro padre (`NumeroLinea` del ítem y del descuento)? Abastalia hoy: nombre del elemento sin distinguir mayúsculas, índices `[n]`, `#e` o vacío = no se envía, la primera hoja con `TipoeCF` y `eNCF`, y `Padre.Nombre` para la segunda aparición de un nombre | Ver el Excel real en el portal de certificación (TK-141) |
| 31 | Regularización de la serie B (TK-157): ¿la 32 de reemplazo de menos de RD$250,000 va por RFCE (Abastalia hoy) o completa? ¿Lleva la fecha de emisión del NCF B o la del día de la regularización (Abastalia: la del día, con `FechaNCFModificado` = la del B)? ¿Se envía también una nota de crédito sobre un B04, o el reemplazo de la nota apunta al B04 (Abastalia: e-CF 34 con `NCFModificado` = el B04)? | Preguntar a la DGII |
| 32 | 607 de una venta con NCF B regularizada: Abastalia la reporta con el NCF B (el comprobante del cliente) y deja fuera el e-CF de reemplazo, que no es una venta nueva. Confirmar | Preguntar a la DGII; revisar con el contador |
| 33 | Certificado de una persona jurídica (R8 #4): el certificado es de la persona física delegada como Firmante y trae su cédula, no el RNC. Abastalia rechaza un certificado con **otro RNC** (o con otra cédula si el negocio es una persona física) y acepta una cédula en el de una empresa. Confirmar con un certificado real si alguna entidad pone el RNC de la empresa en el sujeto | Ver un certificado real (TK-141); relacionado con #28 |
| 34 | Desde R9 #1 el receptor valida la cadena hasta las entidades de `Fe:TrustedRoots` y que el certificado traiga el RNC emisor (o una cédula en el e-CF de una empresa), pero **no** que esa cédula esté delegada para ese RNC: eso solo lo sabe la DGII. Faltan las raíces reales (Avansi, Digifirma): sin ellas el receptor rechaza todo. Tampoco consulta a la DGII (`consultaestado`) antes de pasar el e-CF a la tienda: el emisor puede enviar al receptor antes de que la DGII responda, la forma de la respuesta no está probada y el cursor de la tienda no admite huecos. ¿La DGII espera que el receptor valide la delegación? ¿Publica las raíces? | Cargar las raíces de cada entidad (comprobar la huella); preguntar a la DGII; probar `consultaestado` en TesteCF y, si sirve, confirmarlo antes de la entrega |
| 35 | El directorio (`obtenerdirectorioporrnc`) da `urlRecepcion`, `urlAceptacion` y `urlOpcional`: ¿son la URL base del contribuyente o la del servicio completo? Abastalia acepta las dos (si trae `/fe/`, cambia lo que sigue; si no, agrega `/fe/recepcion/api/ecf`) | Ver respuestas reales en TesteCF |
| 36 | La nube toma el RNC de la URL del receptor (`/{amb}/{rnc}/fe/…`) de lo que declara la tienda al dar el consentimiento; dos cuentas no pueden activar el mismo RNC (índice único desde R9 #6), y el certificado debe ser de una entidad de confianza con ese RNC o una cédula, pero con una cédula cualquier firmante con certificado válido puede declarar el RNC de una empresa. ¿Cómo comprobar que el RNC es de quien lo declara? | Mientras: un segundo intento da `Fe.RncTaken` y lo resuelve soporte (verificación manual). Posible: pedir al dueño que firme un reto con el certificado ya delegado en la OFV. TK-246: en StoreSigned la activación es así (la tienda firma un reto de la nube); sigue abierta para la cédula delegada |
| 37 | Un acuse duplicado: Abastalia responde Estado 1 con motivo 3 aunque el primer acuse se haya perdido en la red. ¿Debe repetir el acuse original (Estado 0)? Del lado emisor (TK-153), un motivo 3 se toma como entregado | Preguntar a la DGII; probar con el simulador Emisor-Receptor |
| 38 | Tipos recibidos sin XSD descargado (41, 43, 44, 45, 46, 47): hoy se acusan sin validar el XSD (sí la firma, el RNC del comprador y los campos que necesita la tienda) | Descargar sus XSD de la DGII y agregarlos a `Abastalia.Fiscal/Schemas` (la nube los enlaza) |
| 39 | Autenticación del receptor: Abastalia la publica y la exige (`Fe:RequireToken` = true). ¿Hay emisores (o el simulador de la DGII) que envían sin autenticarse cuando la URL de autenticación existe? En CerteCF no hay directorio: la aprobación comercial que pide la tienda va solo a la DGII | Probar con el simulador Emisor-Receptor y en los pasos 8 a 11 de la certificación |
| 40 | **Cambiarse a Abastalia ya siendo emisor electrónico (TK-221).** ¿Hay que recertificar el software nuevo o basta con cambiar las URL en "Mantenimiento Directorio FE"? ¿Puede Abastalia seguir la secuencia autorizada desde el **siguiente número libre** después del último que emitió el otro programa, o la DGII pide anular los sobrantes (ANECF) y solicitar una secuencia nueva? ¿Hay que avisar el cambio de proveedor de software en la OFV? Abastalia hoy: no recertifica, carga el resto del rango desde el número siguiente al último que el dueño escribe (en Producción) y la guía dice que, si la DGII pide anular, se anulan y se carga la secuencia nueva | Preguntar a la DGII (TK-177); probar con la tienda piloto |
| 41 | Forma real del Excel de **comprobantes recibidos** de la OFV (TK-223): nombres de columnas, si trae título encima del encabezado, formato de fecha y montos, si separa NCF de e-CF en dos consultas y si se baja como `.xlsx`, `.xls` o `.csv`. Abastalia hoy: solo `.xlsx`; busca el encabezado en las primeras 15 filas de cualquier hoja y toma las columnas por nombre (sin acentos ni mayúsculas): RNC/cédula del emisor (no la del comprador), razón social, NCF o e-NCF (no el modificado), fecha de emisión o del comprobante, monto total, ITBIS facturado (no retenido ni percibido) y estado (salta los anulados o rechazados) | Bajar el Excel real en la OFV de la tienda piloto y ajustar `DgiiReceivedFile` |
| 42 | **Regularizar una nota serie B (B03, B04) emitida en contingencia (R9 #11).** El e-CF de reemplazo lleva `CodigoModificacion` 4 con `NCFModificado` = el B04, así que pierde la referencia a la factura que la nota corrige. ¿Cómo se regulariza: un e-CF 34 que apunta a la factura original (con qué código), dos referencias, o se hace a mano en la OFV? Abastalia hoy: regulariza solo B01 y B02; los B03 y B04 quedan pendientes, con un aviso para hacerlo con el contador (siguen frenando el 607 del período) | Preguntar a la DGII; revisar con el contador |
| 43 | **Venta de RD$ 0 (descuento del 100 %, TK-239).** El XSD de la 32 (y del RFCE) admite `MontoTotal`, `MontoItem` y `PrecioUnitarioItem` en cero ("mayor o igual a cero") y Abastalia la emite sin `TablaFormasPago`. ¿La acepta la DGII (validaciones de negocio fuera del XSD) o espera otra forma para un obsequio o una promoción? Abastalia hoy: la permite (pide supervisor por el descuento) | Probar en TesteCF; preguntar a la DGII |
| 44 | **Receptor con la PC de la tienda apagada (TK-246, StoreSigned).** Abastalia responde HTTP 503 con `Retry-After: 60` y guarda lo recibido. ¿Qué hace el emisor (y el simulador de la DGII) ante un 503: reintenta, cuántas veces y por cuánto tiempo, o entrega la RI en papel? ¿Vale un acuse **tardío** (firmado cuando la PC vuelve) enviado por iniciativa del receptor, y a qué servicio del emisor? ¿Hay plazo para acusar? Abastalia hoy: el reintento del emisor se acusa como recibido; el acuse tardío se firma y se guarda, **no se envía**, y la tienda ya tiene el documento | Preguntar a la DGII; probar con el simulador Emisor-Receptor y en los pasos 8 a 11 |
| 45 | **Aprobación comercial (TK-250).** ¿Hay un plazo para enviar el ACECF desde que se recibe el e-CF (y qué pasa si no se envía)? ¿Hay una lista de motivos de rechazo con códigos o `DetalleMotivoRechazo` es siempre texto libre? Abastalia hoy: sin plazo ni cuenta regresiva en el panel; motivo libre con sugerencias | Preguntar a la DGII |

---

## 12. Tickets (M13 · e-CF propio con la DGII)

Orden sugerido. Tamaño: S ≤ 1 día, M 2–4 días, L 1–2 semanas.

**TK-141 · Acceso a TesteCF, certificado de prueba y ADR-0009** (S, dueño + desarrollo)
Conseguir un RNC habilitado en pre-certificación (el de Abastalia o el de la tienda piloto: FI-GDF-016, Administrador e-CF, delegación de Firmante) y un certificado de persona física (cotizar Avansi y Digifirma: formato `.p12`, precio y vigencia). Hacer una prueba de humo manual (semilla → token → envío de una 32 por RFCE) con un script desechable. Escribir el ADR-0009 "e-CF propio" con las decisiones de §10.7 y marcar en la arquitectura y en ADR-0008 lo que cambia.
*Criterios:* token obtenido en TesteCF; un RFCE aceptado; ADR-0009 aceptado; preguntas 1, 6, 7 y 8 de §11 enviadas a la DGII.
*Depende de:* nada.

**TK-142 · Firma XMLDSig con el certificado del almacén de Windows** (M)
`CertStoreEcfSigner`: XMLDSig envuelta, C14N, RSA-SHA256, `Reference URI=""`, `X509Certificate`, sin preservar espacios; código de seguridad = 6 primeros caracteres del `SignatureValue`. Pantalla para importar el `.p12` (contraseña una sola vez, llave no exportable, permiso solo para la cuenta del servicio). Verificar que el SN del sujeto sea una cédula o RNC válidos. Health check de vencimiento a 30 días. `Fiscal:Signer` = `Simulated` | `Certificate` (implementado como `CertificateEcfSigner`, §10.8).
*Criterios:* el XML firmado pasa la verificación de `SignedXml.CheckSignature` y la del validador de TesteCF; la llave no se puede exportar; ni la contraseña ni el archivo quedan en disco ni en la base; pruebas unitarias con un certificado autofirmado.
*Depende de:* nada (TK-141 para validar contra la DGII).

**TK-143 · XML de e-CF 31, 32, 33 y 34 según el formato oficial, validado contra el XSD** (L)
Reescribir `BuildXml` como `EcfXmlBuilder` con la estructura completa (§3.2), sin etiquetas vacías. XSD oficiales embebidos y validación **antes de firmar** (si falla, la venta hace *rollback* y el número vuelve). Datos del emisor en Configuración (dirección, municipio y provincia con códigos de la Tabla III, nombre comercial). `FechaVencimientoSecuencia` solo en la 31 y la 33. Desglose de ITBIS por tasa y `IndicadorMontoGravado`.
*Criterios:* snapshot por tipo; los cuatro tipos pasan su XSD; un e-CF de cada tipo es aceptado (no condicional) en TesteCF; la prueba e2e cuadra los montos del XML con la venta.
*Depende de:* TK-142.

**TK-144 · Cliente DGII en el nodo** (M)
`DgiiClient` con la tabla de URL por ambiente (§2.2), caché de token, `multipart` con el nombre `RNC+eNCF.xml`, y todas las operaciones: semilla, validar semilla, recepción, recepción FC, consulta de resultado, consulta de TrackIds, consulta de RFCE, anulación, directorio y aprobación comercial. Errores de la DGII mapeados a `Result` con su código y mensajes.
*Criterios:* pruebas de contrato con respuestas grabadas de TesteCF; el token se renueva antes de `expira` y ante un 401; *timeouts* configurables; sin secretos en los logs.
*Depende de:* TK-142.

**TK-145 · Resumen de factura de consumo (RFCE) y umbral de RD$250,000** (M)
`RfceBuilder` desde el e-CF 32 firmado (totales, formas de pago, `CodigoSeguridadeCF`), firmado, validado contra `RFCE 32 v.1.0.xsd` y guardado junto al e-CF. Una 32 de menos de RD$250,000 va por RFCE; de RD$250,000 o más, completa.
*Criterios:* RFCE aceptado en TesteCF; una 32 de RD$250,000 va por recepción con TrackId; el e-CF completo queda guardado aunque no se envíe.
*Depende de:* TK-143, TK-144.

**TK-146 · Cola de envío directo a la DGII y relevo convertido en archivo** (M)
`DgiiEcfProvider` reemplaza la llamada única: `Pending → Sent (TrackId) → Accepted / AcceptedConditional / Rejected`, y RFCE inmediato. Consulta por e-NCF antes de reenviar tras un *timeout*. Columnas nuevas: ambiente, canal, código y mensajes de la DGII, `secuenciaUtilizada`, `SentUtc` y `ResultUtc`. Se retiran `RelayEcfProvider` y `ICloudEcfProvider`; `/relay/v1/ecf` pasa a `/fiscal/v1/archive` (XML, RFCE y respuesta) con almacenamiento frío.
*Criterios:* con la DGII simulada en pruebas (`FakeTimeProvider`): "En proceso" se vuelve a consultar, un *timeout* no duplica el envío, el orden es por plazo, nunca se bloquea por licencia; health `ecf-queue` con 24/60/72 h; e2e en TesteCF con 50 ventas offline enviadas al reconectar.
*Depende de:* TK-144, TK-145.

**TK-147 · Rechazos de la DGII: reemplazo, no nota de crédito** (M)
Bandeja de rechazos con las acciones correctas: `secuenciaUtilizada = false` → corregir y volver a emitir con el mismo e-NCF; `true` → e-CF de reemplazo con número nuevo para la misma venta (y su RI); aceptado condicional → aviso con los mensajes. Corregir §7.3 de la arquitectura.
*Criterios:* prueba de cada camino; auditoría de cada reemplazo; la venta queda ligada al e-CF válido; los reportes 607 usan el e-CF vigente.
*Depende de:* TK-146.

**TK-148 · Representación impresa y QR por tipo y ambiente** (M)
QR versión 8 de al menos 22 mm: `consultatimbre` o `consultatimbrefc` según el tipo y el monto, con el host del ambiente; código de seguridad escapado. Encabezado obligatorio (tipo en palabras, e-NCF, vencimiento de la secuencia en la 31 y la 33, modificado y código en las notas), código de seguridad y fecha de firma bajo el QR, y la leyenda de contingencia según el estado de conexión (§10.6). PDF de la misma RI para la certificación. Cierra el pendiente 14 de R3.
*Criterios:* el QR impreso en el rollo de 80 mm abre la consulta correcta en TesteCF y en CerteCF; snapshot del recibo de cada tipo; PDF de menos de 10 MB.
*Depende de:* TK-143; TK-118 para la impresora real.

**TK-149 · Secuencias por ambiente y vencimiento opcional** (S)
`Environment` en `EcfSequences` y en `EcfDocuments`; `ExpiresOn` opcional en la 32 y la 34; rango de TesteCF cargado solo; al pasar a producción, lo emitido en pruebas queda fuera de los reportes fiscales. Migración que conserva lo actual como `Simulated`.
*Criterios:* no se puede reservar un número de otro ambiente; los reportes 606 y 607 ignoran lo que no es de producción; prueba de la migración.
*Depende de:* nada.

**TK-150 · Anulación de e-NCF (ANECF)** (S)
Armar, firmar y enviar el ANECF de los sobrantes de un rango vencido o abandonado y de los e-CF firmados que nunca salieron. Pantalla en Comprobantes con vista previa de los rangos. Desde la nube no se puede: se envía desde el nodo.
*Criterios:* anulación aceptada en TesteCF; un número anulado no se vuelve a reservar; auditoría.
*Depende de:* TK-144, TK-149.

**TK-151 · Copia del certificado en Key Vault con consentimiento** (M)
Pantalla de consentimiento (texto de §10.3) firmada por el dueño. Subida del `.p12` desde el nodo e importación no exportable, un certificado por tenant. `KeyVaultEcfSigner` con lista blanca de raíces (`ARECF`, `ACECF`, `SemillaModel`). Registro de firmas visible en el panel. Retiro del consentimiento que borra la llave. Bicep: Key Vault con protección de purga y acceso por identidad administrada.
*Criterios:* el firmador de nube rechaza un `ECF` en una prueba; ni el archivo ni la contraseña se guardan; el retiro bloquea las firmas en menos de un minuto; las firmas aparecen en el panel.
*Depende de:* TK-142.

**TK-152 · Servicios de receptor en la nube (Recepción y Aprobación Comercial)** (L)
Endpoints por tenant y ambiente (`/{amb}/{rnc}/fe/recepcion/api/ecf` y `/fe/aprobacioncomercial/api/ecf`, sin distinguir mayúsculas): XML endurecido, límite de tamaño, XSD, verificación de la firma del emisor, comprobación del RNC del comprador, duplicados y `ARECF` firmado con el estado y el motivo correctos. Las aprobaciones recibidas se ligan al e-CF emitido. Todo se guarda 10 años. Monitoreo de disponibilidad y alerta.
*Criterios:* pasa las pruebas con el simulador Emisor-Receptor de TesteCF; las pruebas de comunicación de CerteCF (pasos 8 a 11) en verde; prueba de carga de 20 documentos por segundo; sin consentimiento el endpoint responde 503 y lo registra.
*Depende de:* TK-151.

**TK-153 · Entrega de la 31 al comprador electrónico** (M)
Después de `Accepted`: buscar al comprador en el directorio (caché diaria), autenticarse en su `urlOpcional` si la publica, hacer `POST` a su `urlRecepcion`, guardar el `ARECF` y reintentar con *backoff* durante 72 h. Si no es electrónico, no se hace nada (lleva la RI).
*Criterios:* entrega aceptada por el simulador de TesteCF; el acuse queda guardado; los fallos no bloquean la cola de la DGII.
*Depende de:* TK-146, TK-144.

**TK-154 · Comprobantes recibidos de suplidores en la tienda** (M)
Lo que recibe TK-152 baja al nodo por Sync como "comprobantes recibidos": lista en Compras con un botón "crear compra desde este e-CF" que llena el suplidor, los montos y el NCF para el 606. La aprobación o el rechazo comercial (opcional), enviado al suplidor y a la DGII, se firma en la nube.
*Criterios:* un e-CF recibido aparece en la tienda en menos de 1 minuto con internet; crear la compra no duplica el NCF (índice existente); la aprobación es aceptada en TesteCF.
*Depende de:* TK-152.

**TK-155 · Asistente de certificación** (L)
Lista de los 14 pasos con su estado. Importar los Excel del set de e-CF y de aprobaciones comerciales y generar los XML **en el orden y con los campos del Excel** (generador genérico por columnas, para cualquier tipo). Enviar a CerteCF, seguir cada TrackId y mostrar los rechazos con su mensaje. Lote de simulación con datos reales y sus PDF. Herramienta "Firmar archivo de la DGII" para la postulación, la declaración jurada y la delegación, con la vista previa del XML antes de firmar. Las URL de receptor de la tienda, listas para copiar.
*Criterios:* una tienda de prueba completa los pasos 2 a 11 en CerteCF sin ayuda del desarrollador; manual de una página para el revendedor.
*Depende de:* TK-143 a TK-148, TK-152.

**TK-156 · Operación: salud, estatus de la DGII y guía de contingencia** (S)
Health checks: token de la DGII, último envío, certificado (30 días), reloj (bloquea la firma por encima del umbral), receptor de la nube y estatus de servicios de la DGII (cuando se confirme la URL). Avisos 24/60/72 h al dueño. Guía en español para declarar la contingencia en la OFV, el límite de 15 días y la regularización.
*Criterios:* cada health check con prueba; la guía revisada por el dueño.
*Depende de:* TK-146.

**TK-157 · Contingencia por imposibilidad de emitir (serie B y regularización)** (M)
Si no hay certificado o no se puede firmar: modo serie B con rangos B cargados de antemano, venta con NCF no electrónico y una alerta de "declare la contingencia en la OFV". Al salir, emitir e-CF de reemplazo (`CodigoModificacion` 4, `InformacionReferencia` al NCF B) **solo a la DGII**, en 30 días.
*Criterios:* prueba del ciclo completo con `FakeTimeProvider`; contador de días de contingencia (15) y de regularización (30); nunca se usa B mientras se pueda firmar.
*Depende de:* TK-146.

**TK-158 · Pruebas de contrato y de punta a punta contra TesteCF** (M)
Un job de CI manual y nocturno con el certificado de prueba en un secreto de GitHub, que emite, envía y consulta los cuatro tipos, el RFCE, el ANECF y la recepción con el simulador. Alerta si cambia una respuesta o un XSD de la DGII.
*Criterios:* el job corre verde dos semanas seguidas; el certificado nunca aparece en los logs.
*Depende de:* TK-146, TK-150, TK-152.

**TK-159 · (Después de V1) Receptor sin custodia por túnel a la tienda y E41 de compras** (L)
Dos piezas para cuando algún dueño las pida: el modo receptor sin copia del certificado (conexión saliente del nodo y 503 con la tienda apagada) y el Comprobante Electrónico de Compras (41) para compras a productores sin RNC.
*Criterios:* a definir cuando se priorice.
*Depende de:* TK-152, TK-143.

---

## 13. Fuentes

Primarias (DGII), sección "Documentación sobre e-CF":
- Informe Técnico e-CF v1.0 (actualizado en marzo de 2026: contingencia según el Decreto 587-24): https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Informe%20y%20Descripci%C3%B3n%20T%C3%A9cnica/Informe%20T%C3%A9cnico%20e-CF%20v1.0.pdf
- Descripción Técnica Servicios DGII (actualizada el 02-01-2026): https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Informe%20y%20Descripci%C3%B3n%20T%C3%A9cnica/Descripcion%20Tecnica%20Servicios%20DGII.pdf
- Descripción Técnica Servicios Emisores Electrónicos: https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Informe%20y%20Descripci%C3%B3n%20T%C3%A9cnica/Descripcion%20Tecnica%20Emisores%20Electronicos.pdf
- Representación Impresa (modelos ilustrativos): https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Informe%20y%20Descripci%C3%B3n%20T%C3%A9cnica/Representaci%C3%B3n%20Impresa%20(Modelos%20ilustrativos).pdf
- Firmado de e-CF (marzo de 2023): https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Instructivos%20sobre%20Facturaci%C3%B3n%20Electr%C3%B3nica/Firmado%20de%20e-CF.pdf
- Instructivo de Contingencia de FE (octubre de 2026): https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Instructivos%20sobre%20Facturaci%C3%B3n%20Electr%C3%B3nica/Instructivo%20de%20Contingencia%20de%20FE.pdf
- Instructivo Delegaciones de Roles de FE (v2.0, junio de 2025): https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Instructivos%20sobre%20Facturaci%C3%B3n%20Electr%C3%B3nica/Instructivo%20Delegaciones%20de%20Roles%20de%20Facturaci%C3%B3n%20Electr%C3%B3nica.pdf
- Proceso de Certificación para ser Emisor Electrónico (julio de 2025): https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaciones%20Proceso%20de%20Certificaci%C3%B3n%20FE/Proceso%20de%20Certificacion%20para%20ser%20Emisor%20Electronico.pdf
- Emisor Electrónico con Proveedor de Servicios de FE Certificado (mayo de 2026): https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaciones%20Proceso%20de%20Certificaci%C3%B3n%20FE/Proceso-Certificacion-EmisorElectronico-Proveedor-Servicios-FECertificado.pdf
- Formato e-CF v1.0 (octubre de 2025): https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Formatos%20XML/Formato%20Comprobante%20Fiscal%20Electr%C3%B3nico%20(e-CF)%20V1.0.pdf
- Formato RFCE v1.0: https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Formatos%20XML/Formato%20Resumen%20Factura%20Consumo%20Electr%C3%B3nica%20v1.0.pdf
- Formato Anulación de e-NCF v1.0: https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Formatos%20XML/Formato%20Anulaci%C3%B3n%20de%20e-NCF%20v1.0.pdf
- Formato Acuse de Recibo v1.0: https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Formatos%20XML/Formato%20Acuse%20de%20Recibo%20v%201.0.pdf
- Formato Aprobación Comercial v1.0: https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Formatos%20XML/Formato%20Aprobaci%C3%B3n%20Comercial%20v1.0.pdf
- XSD (e-CF 31 a 47, RFCE 32, ARECF, ACECF, ANECF y Semilla), carpeta `Documentación Técnica (XSD)`; índice en https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Paginas/documentacionSobreE-CF.aspx. Los de 31 a 34 se descargaron el 15-10-2026 (URL directas y SHA-256 en `src/Modules/Fiscal/Abastalia.Fiscal/Schemas/README.md`), por ejemplo https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/e-CF%2032%20v.1.0.xsd
- Norma General 10-2021 (Proveedores de Servicios de FE): https://dgii.gov.do/legislacion/normasGenerales/Documents/NG%20sobre%20Comprobantes%20Fiscales/Norma10-21.pdf
- Aviso 19-25 (certificados gratuitos solo para el Facturador Gratuito): https://dgii.gov.do/publicacionesOficiales/avisosInformativos/Documents/2025/19-25.pdf
- Prórroga de 6 meses para pequeños, micro y no clasificados (noticia de la DGII; la página pide sesión): https://dgii.gov.do/noticias/Paginas/DGII-otorga-prorroga-seis-meses-Pequeños-Micros-y-no-clasificados.aspx

Secundarias (verificar):
- INDOTEL, resoluciones de Avansi (070-2022) y de la Cámara de Comercio y Producción de Santo Domingo (108-21): https://indotel.gob.do/wp-content/uploads/2022/10/resoluci_n_070_2022_avansi.pdf y https://indotel.gob.do/wp-content/uploads/2022/10/res_108-21_que_autoriza_renovavion_a_ccpsd_para_operar_como_entidad_de_certificacion_fd.pdf
- EY, alerta sobre la prórroga: https://www.ey.com/es_ce/technical/tax/tax-alerts/republica-dominicana-dgii-otorga-prorroga-de-seis-meses-a-contribuyentes-identificados-como-pequenos-micros-y-no-clasificados-para-la-implementacion-de-fe
- Costo estimado de certificados: https://siemprealdia.co/republica-dominicana/impuestos/como-obtener-el-certificado-digital/
- Flujo RFCE de una librería abierta (referencia, sin licencia declarada: no copiar código): https://github.com/victors1681/dgii-ecf
