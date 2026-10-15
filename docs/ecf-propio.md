# e-CF propio: Toki habla directo con la DGII (TK-140)

> 15 de octubre de 2026. Diseño, sin código. Reemplaza el borrador anterior de este archivo.
> Decisión del dueño: **sin proveedor de terceros**. Toki llama a los servicios web de la DGII con la información y el certificado digital de cada contribuyente.
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

| Tipo | Nombre | ¿V1 de Toki? |
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
- `TipoIngresos` (01 = ingresos por operaciones) es obligatorio en la 31, la 32, la 33 y la 34. `IndicadorMontoGravado` (1 si los precios de las líneas incluyen ITBIS) es condicional: va cuando hay ítems gravados. Los precios de Toki incluyen ITBIS, así que irá en 1.
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
- La DGII publica un ejemplo en C# con `SignedXml`. Toki usa `System.Security.Cryptography.Xml` (MIT, ya listado en la arquitectura) **sin** la bandera `Exportable` que usa ese ejemplo: con una llave no exportable del almacén de Windows firma igual.

Se firman con el mismo método el e-CF, el RFCE, el ANECF, el ACECF, el ARECF y la semilla.

### 4.2 El certificado: de quién es, quién lo emite y cuánto cuesta

- Es un **Certificado Digital de Persona Física para Procedimientos Tributarios** (NG 10-2021 art. 13; Informe Técnico §3). No hay certificado "de empresa": la factura sale a nombre del RNC y la firma una **persona**.
- Esa persona debe estar **vinculada al RNC como representante** y tener el rol de **Firmante Autorizado**, que el **Administrador e-CF** delega en la OFV ("Delegación de Roles", firmando un XML de delegación). Los roles son Administrador, Solicitante, Firmante y Aprobador Comercial.
- Lo emiten las entidades de certificación autorizadas por INDOTEL. Con resolución vigente están **Avansi S.R.L.** (tecnología Viafirma) y la **Cámara de Comercio y Producción de Santo Domingo (Digifirma)**. Validan la identidad con biometría. Vigencia de 1 o 2 años.
- **Costo (verificar):** no encontramos tarifas oficiales. Una fuente secundaria estima entre US$30 y US$70 al año. La DGII regala certificados por 1 año, pero **solo para su Facturador Gratuito**: no sirven con Toki.
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

---

## 6. Contingencia y plazo de 72 horas (Decreto 587-24, arts. 40–43)

| Caso | Qué hace el emisor | Plazo |
|---|---|---|
| **Sin conexión** (puede firmar, no puede enviar) | Emite e-CF "offline", entrega la RI con la leyenda de contingencia y los envía al volver la conexión | **72 horas** desde la emisión |
| **No puede emitir e-CF** (sin certificado, PC dañada, software caído) | Lo declara en la OFV (contingencia total o parcial), usa **comprobantes no electrónicos serie B** autorizados y declara la salida al terminar | Máximo **15 días calendario** |
| Regularización después de la anterior | Emite e-CF que **reemplazan** los B (`CodigoModificacion` 4) y los envía **solo a la DGII** | **30 días calendario** desde la salida |
| **La DGII no está disponible** | Guarda los e-CF y los envía al volver el servicio | Si pasa de 15 días hábiles, la OFV habilita reportes alternos |

Para Toki:
- La cola de 72 horas ya existe (`EcfSubmissionQueue`, arquitectura §7.3). Hay que sumarle el ciclo TrackId → resultado y el RFCE.
- **Certificado vencido o PC principal dañada** es "imposibilidad de emitir". Hoy la venta fallaría con `Fiscal.CertificadoNoDisponible`. Hace falta un **modo serie B** con su regularización (TK-157). El aviso de 30 días antes del vencimiento del certificado es la defensa principal.
- No existe un modo "vender sin firmar y firmar después": el código de seguridad sale de la firma y va impreso. Con certificado, la tienda **siempre firma al vender**.

---

## 7. Factura de consumo: resumen o e-CF completo

| Monto total de la 32 | Qué se envía | Servicio | QR | Comprador |
|---|---|---|---|---|
| **Menos de RD$250,000** | **RFCE** firmado (encabezado, totales, formas de pago y código de seguridad del e-CF) | `fc.dgii.gov.do/{amb}/recepcionfc` (respuesta inmediata) | `consultatimbrefc` | Opcional |
| **RD$250,000 o más** | e-CF completo | `recepcion` + TrackId | `consultatimbre` | Obligatorio (ya validado en `FiscalDocuments.IssueAsync`) |

Aunque se mande el resumen, **el e-CF completo firmado se conserva 10 años** y debe estar disponible para consulta. Toki ya lo guarda en `fiscal.EcfDocuments.SignedXml` (gzip) y nunca lo borra.

Flujo: armar el e-CF 32 completo → firmarlo → código de seguridad → armar el RFCE con ese código → firmar el RFCE → enviarlo. Las dos firmas son locales.

---

## 8. Certificación: los pasos y qué automatiza Toki

Requisitos del contribuyente: RNC activo, clave de la OFV, alta de NCF, certificado del representante que actuará como **Usuario Administrador e-CF**, estar al día y tener un software de e-CF. En el formulario el software se declara "adquirido a un proveedor externo", con los datos de Toki como proveedor.

| # | Paso del portal de certificación | Quién | Qué hace Toki |
|---|---|---|---|
| 0 | Solicitud FI-GDF-016 en la OFV; la DGII responde por el buzón con el acceso al portal | Dueño | Lista de pasos con enlaces |
| — | Pruebas libres en **TesteCF** (antes de postularse) | Toki | Carga automática del rango de pruebas, prueba de humo: autenticación, una 31, una 32 por RFCE y una 34 |
| 1 | Postulación: nombre y versión del software, URL de Recepción, Aprobación y Autenticación (opcional); se descarga un XML, se firma y se sube | Dueño + Toki | Muestra las URL de su tienda; **firma el XML descargado** con el certificado instalado (herramienta "Firmar archivo de la DGII") |
| 2 | Pruebas de datos e-CF: se descarga un **Excel** con el set y se generan y envían los XML **con los mismos campos y en el mismo orden**; un rechazo reinicia el set | Toki | Importa el Excel, genera, firma y envía cada fila a CerteCF (o el RFCE cuando corresponde) y sigue cada TrackId hasta el final |
| 3 | Pruebas de datos de aprobaciones comerciales (otro Excel) | Toki | Genera, firma y envía los `ACECF` |
| 4 | Simulación: e-CF con datos reales del negocio | Toki | Emite un lote representativo en CerteCF |
| 5–6 | Subir el **PDF de la RI** de cada e-CF de la simulación (máximo 10 MB) | Dueño + Toki | Genera los PDF con el mismo diseño del recibo |
| 7 | Confirmar las URL de prueba | Dueño | Las muestra |
| 8–9 | Comunicación: la DGII manda e-CF a la URL de Recepción y espera **acuses firmados** | Nube de Toki | Servicio de receptor en el ambiente `certecf` |
| 10–11 | La DGII manda aprobaciones comerciales a la URL de Aprobación | Nube de Toki | Las recibe, valida y responde |
| 12 | URL de producción | Dueño | Las muestra |
| 13 | **Declaración jurada**: descargar el XML, firmarlo y subirlo | Dueño + Toki | Lo firma con "Firmar archivo de la DGII" |
| 14 | La DGII verifica el estado del RNC | DGII | — |
| — | Autorización: se habilita el menú FE en la OFV. El dueño pide secuencias e-NCF de producción y registra las URL en "Mantenimiento Directorio FE" | Dueño | Asistente para cargar los rangos (ya existe) y cambiar el ambiente a `eCF` |

Toki **no** entra al portal ni a la OFV con las credenciales del dueño: los pasos marcados "Dueño" los hace él en su navegador, y Toki prepara y firma los archivos. Meta: que una tienda pequeña complete los pasos 2 a 11 en una tarde.

Más adelante, si Toki se certifica como **Proveedor de Servicios de FE (NG 10-2021)**, sus clientes solo harían las pruebas de simulación (art. 15). Requiere que Toki sea emisor electrónico, el SLA, la mesa de ayuda y recertificar con cada versión nueva del XML. Queda fuera de este diseño: ver §11, pregunta 1.

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

## 10. Cómo encaja en Toki

### 10.1 Qué tiene hoy el código y qué falta

| Pieza actual | Estado | Falta |
|---|---|---|
| `EcfXml` (antes `FiscalDocuments.BuildXml`) | ✅ TK-143: formato oficial, validado contra el XSD antes de firmar (§10.8) | Municipio y provincia (opcionales en el XSD) |
| `SimulatedEcfSigner` | Hash, no firma; sigue siendo el valor por defecto | ✅ TK-142: `CertificateEcfSigner` con XMLDSig (§4.1, §10.8). Falta la pantalla de importación del `.p12` y el aviso de vencimiento |
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

| Opción | Emitir sin internet | Receptor 24/7 | Custodia por Toki | Riesgo |
|---|---|---|---|---|
| **A.** Solo en la PC (almacén `LocalMachine\My`, llave no exportable) | Sí | **No**: la PC se apaga de noche y no tiene IP pública. Habría que pasar cada acuse por un túnel a la tienda | No | Si la tienda está cerrada o sin internet, los suplidores no pueden entregar e-CF y la prueba de comunicación de la certificación depende de que la PC esté encendida |
| **B.** Solo en Key Vault | **No** | Sí | Sí | Pierde la contingencia: inaceptable en una tienda |
| **C.** **En los dos**: la PC firma la emisión; Key Vault firma acuses, aprobaciones y semillas de la nube | Sí | Sí | Sí, con consentimiento | Dos lugares donde vive la llave. Se mitiga abajo |

**Contraseña:** el dueño la escribe **una sola vez**, en la pantalla de Configuración → Comprobantes de la PC principal, al importar el `.p12`. Toki importa la llave como **no exportable**, le da permiso de lectura solo a la cuenta del servicio y descarta el archivo y la contraseña. Nunca se guardan: el `appsettings` no tiene secretos (arquitectura §5). Con consentimiento, el mismo paso sube el `.p12` por el canal autenticado del nodo (TLS) y la nube lo importa en Key Vault, uno por tenant y no exportable. Tampoco ahí se guarda el archivo ni la contraseña.

**Consentimiento (opción C):** una pantalla clara, en español, que firman el dueño y el titular del certificado. Dice que Toki guarda una copia **no exportable** del certificado, que **solo** firma acuses de recibo, aprobaciones comerciales que el dueño pida y semillas de autenticación, y **nunca** facturas, notas ni anulaciones; que cada firma queda registrada y el dueño puede verla en el panel; y que puede retirar el permiso cuando quiera (la llave se borra; con la protección de purga de Key Vault queda en borrado suave e inaccesible durante su periodo de retención **(verificar el periodo que se configura)**). Base: Ley 172-13 (datos personales), Ley 126-02 (el titular responde por su firma) y NG 10-2021 art. 14 d (almacenamiento seguro de certificados).

**Mitigaciones:** un firmador de nube (`KeyVaultEcfSigner`) que **rechaza en código** cualquier raíz distinta de `ARECF`, `ACECF` y `SemillaModel`; identidad administrada; una llave por tenant; registro de cada firma (fecha, tipo, e-NCF y hash) replicado al panel; alerta si cambia el certificado. En la PC: llave no exportable; como endurecimiento opcional, importarla al proveedor TPM (`Microsoft Platform Crypto Provider`) **(probar)**.

**Si el dueño no da el consentimiento:** opción A con túnel. La nube recibe el e-CF del suplidor, lo pasa por la conexión saliente del nodo, la PC firma el acuse y la nube lo devuelve. Con la tienda apagada, la nube responde HTTP 503 y el suplidor reintenta o entrega la RI en papel. Se construye solo si algún dueño lo pide (TK-159).

### 10.4 Quién habla con la DGII

**La PC principal, directo**, con su propio token (semilla firmada localmente). Motivos:
- Tiene el certificado. La API key de un proveedor era la razón del relevo (ADR-0008); sin proveedor, esa razón desaparece.
- El plazo de 72 horas depende solo del internet de la tienda y de la DGII, **no de que nuestra nube esté arriba**. Una caída de Toki no deja a todas las tiendas fuera de plazo.
- Hay un salto menos y un solo emisor por documento, sin riesgo de doble envío.

La nube habla con la DGII solo para su papel de receptor (y para mandar a la DGII la copia de una aprobación comercial).

El **relevo** (`/relay/v1/ecf`) cambia de papel: ya no reenvía, **archiva**. La PC sube el XML firmado, el RFCE y la respuesta de la DGII, y así la nube guarda la copia de 10 años fuera de la tienda y el panel muestra el estado fiscal. Se renombra para que no confunda (`/fiscal/v1/archive`).

### 10.5 Cambios en el código (para los tickets)

- **`IEcfSigner`**: `Sign(XDocument) → (SignedXml, SecurityCode)` se queda. Se suman `CertStoreEcfSigner` (nodo) y `KeyVaultEcfSigner` (nube, con lista blanca de raíces). El `RSA` de Key Vault se usa con `SignedXml` a través de una subclase de `RSA` que delega `SignHash` a `CryptographyClient`.
- **XML**: `EcfXmlBuilder` por tipo según el formato, `RfceBuilder` a partir del e-CF firmado y `AnecfBuilder`. Los XSD oficiales van embebidos y se valida **antes de firmar**: un XML que no cumple el XSD no se firma ni gasta número. Las pruebas de snapshot y XSD ya están previstas en `Toki.Fiscal.Tests`.
- **`DgiiClient`** (nodo): una tabla de URL por ambiente (`Fiscal:Environment` = `TesteCF` | `CerteCF` | `eCF`), una caché de token por ambiente que renueva 5 minutos antes de `expira` o al recibir un 401, y envíos `multipart` con el nombre `RNC+eNCF.xml`. Cliente HTTP con *timeouts* cortos y sin reintentos propios: los reintentos son de la cola.
- **`IEcfProvider`** pasa a cubrir enviar un e-CF (→ TrackId), consultar el resultado, enviar un RFCE (→ resultado), buscar TrackIds o RFCE por e-NCF (para dudas por *timeout*) y anular rangos. Implementaciones: `SimulatedEcfProvider` (demo, pruebas y tiendas aún no certificadas) y `DgiiEcfProvider`. `RelayEcfProvider` y `ICloudEcfProvider` se retiran.
- **Cola** (`EcfSubmissionQueue`): `Pending` → (RFCE) `Accepted` / `AcceptedConditional` / `Rejected`, o → (e-CF) `Sent` con TrackId → consulta cada pocos segundos → estado final. Ante un *timeout*, primero se consulta por e-NCF y después se reenvía. Sigue ordenando por `SubmitDeadlineUtc` y con los mismos reintentos. Después de `Accepted`, una 31 a un comprador electrónico pasa a **entrega al receptor**: directorio por RNC, `POST` a su `urlRecepcion` y se guarda el `ARECF`.
- **Rechazos**: con `secuenciaUtilizada = false`, se corrige y se vuelve a emitir **con el mismo e-NCF** (la RI ya entregada queda con otro código: caso raro si se valida el XSD antes de firmar). Con `true`, se emite un **e-CF de reemplazo** con número nuevo para la misma venta. Un aceptado condicional se guarda como válido y avisa.
- **Secuencias**: una columna `Environment`. `ExpiresOn` opcional en la 32 y la 34. En TesteCF, rango 1–10,000,000 cargado solo. Al pasar a `eCF`, lo emitido en pruebas queda marcado como no fiscal y nunca se envía a producción.
- **Nube**: módulo `Fe` en `Toki.Host.Cloud` con `/{amb}/{rnc}/fe/recepcion/api/ecf` y `/{amb}/{rnc}/fe/aprobacioncomercial/api/ecf` (la DGII deja libre el host y el prefijo: **verificar en TesteCF** que acepta un prefijo de ruta; si no, un subdominio por tenant). XML endurecido (sin DTD, sin resolver), límite de tamaño, verificación de la firma del emisor, `RNCComprador` = RNC del tenant, duplicados por (RNC emisor, e-NCF) y acuse firmado. Lo recibido se guarda y **baja a la tienda** por Sync como "comprobantes recibidos de suplidores", la base para llenar compras y el 606.
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
5. **Certificación asistida** dentro de Toki (Excel → XML → CerteCF → resultados → PDF), con el dueño haciendo solo lo que exige su clave de la OFV.
6. **No ser PSFE por ahora.** Reevaluar cuando haya más de ~20 tiendas: acorta la certificación de cada cliente, pero trae SLA, mesa de ayuda y la responsabilidad del art. 14.
7. Mantener `SimulatedEcfProvider` y `SimulatedEcfSigner` como valores por defecto hasta que cada tienda esté certificada, igual que hoy.

### 10.8 Implementado: firma (TK-142) y XML (TK-143)

> 15 de octubre de 2026. Sin llamadas a la DGII (eso es TK-144) y sin cambios en el QR ni en la RI (TK-148).

**XSD.** Los cuatro XSD oficiales (`e-CF 31 v.1.0.xsd` a `e-CF 34 v.1.0.xsd`) están en `src/Modules/Fiscal/Toki.Fiscal/Schemas/`, con su nombre original, la URL de descarga y el SHA-256 en el `README.md` de esa carpeta. Van embebidos y se compilan una vez por tipo (cada XSD repite los mismos tipos globales, así que no caben en un solo `XmlSchemaSet`).

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

**Pruebas.** `Toki.UnitTests/Fiscal`: una 32 con exento + 18 % + 16 %, descuento y dos formas de pago; una 32 de RD$250,000 o más con comprador; una 31 con RNC y un peso de 3 decimales; la 34 con 10 y 45 días (indicador 0 y 1) y una anulación sin comprador; una 33; la 31 sin comprador que falla el XSD; escape y corte de textos; firma de una 32 y una 31 con un certificado autofirmado (`CertificateRequest`), verificada con `SignedXml.CheckSignature`, con el código de seguridad y el XSD después de firmar; una firma rota al cambiar un monto; carga desde `.pfx`. `Toki.IntegrationTests/Fiscal/CertificateSigningTests`: `IssueAsync` con el firmador real (dirección, firma, fecha del e-NCF modificado y referencia inexistente).

**Diferencias encontradas en los XSD reales** (frente a §3 y al PDF del formato):

| # | Qué dice el XSD | Qué hace Toki |
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

> 15 de octubre de 2026. **Todo probado contra una DGII simulada** (`FakeDgii` en `Toki.IntegrationTests`), hecha con los contratos de §2. No tenemos acceso a TesteCF ni certificado real (TK-141): nada de esto ha hablado todavía con la DGII de verdad. Lo marcado **(probar en TesteCF)** es lo primero que hay que confirmar cuando llegue el acceso.

**Configuración.** `Fiscal:Provider` = `Simulated` (por defecto) | `Dgii`. Con `Dgii` hace falta `Fiscal:Signer` = `Certificate` (la autenticación es la semilla firmada; si no, la API no arranca). `Fiscal:Dgii:Environment` = `TesteCF` (por defecto) | `CerteCF` | `eCF` elige las URL de §2.2 (`ecf.dgii.gov.do/{testecf|certecf|ecf}` y `fc.dgii.gov.do/{…}`). `Fiscal:Dgii:TimeoutSeconds` (30). El valor `Relay` ya no existe.

**TK-144 · `DgiiClient`** (`Toki.Fiscal/DgiiClient.cs`).
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

**TK-150 · ANECF.** `POST /api/fiscal/sequences/{id}/void` (cuerpo opcional `{ "to": … }`) anula desde el próximo número sin usar hasta `to` (por defecto, el final del rango): mueve el rango más allá de esos números en la misma operación atómica (si una venta tomó el número entretanto, falla en vez de anular uno usado), arma el ANECF según `ANECF v.1.0.xsd`, lo valida, lo firma y lo deja a la cola, que lo envía a `anulacionrangos`. Auditoría `fiscal.void-sequence`; `GET /api/fiscal/voids` lista los envíos con la respuesta. Pendiente: la pantalla con vista previa y la anulación de e-CF firmados que nunca salieron.

**Pruebas.** Unitarias: RFCE, reemplazo y ANECF validados contra sus XSD, QR por canal y ambiente. Con la DGII simulada: semilla firmada y validada contra `Semilla v.1.0.xsd`, token reutilizado y renovado a 5 minutos de `expira` y ante un 401, caída que lanza, códigos 0–4, `secuenciaUtilizada`, hosts y nombres de archivo; la cola (fallo → consulta antes de reenviar → `Sent` → "en proceso" → resultado; *timeout* que llegó; RFCE inmediato; rechazo final); de punta a punta con la API real y el firmador real en TesteCF: una 32 pequeña por RFCE, una 31 rechazada y reemplazada (la venta pasa al nuevo e-NCF), una 32 de RD$300,000 por recepción, un solo token, archivo en la nube, 607 vacío en TesteCF, ANECF aceptado y contingencia (leyenda, envío al volver la DGII, salud Degraded a las 24 h).

**Falta probar en TesteCF** (con TK-141): la forma real de cada respuesta (nombres de campos, `codigo` como número, `mensajes` como lista de `{ valor, codigo }`), que la semilla firmada así sea aceptada, la vida real del token, el RFCE aceptado y su QR `consultatimbrefc`, el nombre de archivo y la respuesta de `anularrango`, el código 0 justo después de recibir un TrackId y los límites de envío por minuto.

---

## 11. Riesgos y preguntas abiertas

| # | Riesgo o pregunta | Cómo se resuelve |
|---|---|---|
| 1 | **¿Custodiar copias de certificados y alojar los servicios de receptor de clientes convierte a Toki en "proveedor de servicios de FE" (NG 10-2021) sin estar certificado?** | Preguntar a la DGII (facturacionelectronica@dgii.gov.do, 809-689-3444 opción 4) antes de producción |
| 2 | **Plazo del 15-11-2026**: un mes para certificar la primera tienda con un módulo que aún no existe | Decisión del dueño: piloto con TesteCF ya, priorizar TK-141 a TK-147 y TK-151 a TK-155, y tener un plan B (Facturador Gratuito de la DGII para poco volumen) |
| 3 | Acceso a TesteCF: requiere la solicitud FI-GDF-016 de un RNC real y un certificado. Sin eso no se prueba nada | TK-141: el RNC de Toki o el de la tienda piloto |
| 4 | Formato del certificado del emisor (`.p12` exportable o token/nube) y precio real | Cotizar con Avansi y Digifirma |
| 5 | Si el set de pruebas exige tipos que la tienda no usa (41, 43–47) | Ver el Excel en el portal; el generador del set es genérico por columnas |
| 6 | URL del servicio **Estatus de servicios / ventanas de mantenimiento** | Pedirla a la DGII o buscarla en el Swagger de TesteCF |
| 7 | Si la DGII acepta un **prefijo de ruta** por tenant en las URL de receptor o exige un host propio por contribuyente | Probar en TesteCF con el simulador Emisor-Receptor |
| 8 | Qué pasa cuando el receptor no acusa (nuestra nube caída o un tenant sin consentimiento): ¿hay sanción para el receptor? | Preguntar a la DGII |
| 9 | Vida real del token (hoy 1 h) y límites de envío por minuto | Leer `expira`; medir en TesteCF |
| 10 | `FechaVencimientoSecuencia` en la 32 y la 34 de producción (el formato dice que no va; el Informe dice que toda secuencia vence) | Ver la autorización real en la OFV |
| 11 | Rechazo de un RFCE ya entregado al cliente: volver a emitir con el mismo número cambia el código de seguridad del recibo | Validar el XSD antes de firmar; documentar el caso al dueño |
| 12 | El reloj de la PC: `FechaHoraFirma` posterior a la hora de la DGII causa rechazo | El health check de reloj (arquitectura §7.3) se vuelve bloqueante para firmar si la diferencia pasa de 2 minutos **(decidir umbral)** |
| 13 | Robo de la llave en una PC de colmado (malware con permisos de administrador) | Llave no exportable, cuenta de servicio, TPM opcional, revocación con la entidad de certificación y alerta en el panel ante firmas extrañas |
| 14 | Cambio de versión del XML o de los servicios (la DGII separó la Descripción Técnica en 2026) | Revisar la "Bitácora" de cada documento cada mes; pruebas de contrato contra TesteCF (TK-158) |
| 15 | Una tienda sin nube (si algún plan lo permitiera) no puede tener servicios de receptor | Para e-CF, la conexión a la nube de Toki es obligatoria. Reflejarlo en `planes.md` |
| 16 | Retención de la copia en la nube (10 años) y su costo | Almacenamiento frío (Blob *cool/archive*) por tenant; estimarlo en TK-146 |
| 17 | Ventas por peso con 3 decimales: el XSD solo admite 2 en `CantidadItem` (§10.8, diferencia 2) | Preguntar a la DGII si redondear el peso y recalcular el precio es lo esperado; probar en TesteCF |
| 18 | `<Comprador />` vacío en la 32 a consumidor anónimo frente a la regla de "no enviar etiquetas vacías" (§10.8, diferencia 3) | Preguntar a la DGII; probar una 32 completa sin comprador en TesteCF |
| 19 | Venta a crédito sin `FechaLimitePago`, y forma de pago del saldo a favor del cliente (¿7 nota de crédito u 8 otras?) | Preguntar a la DGII |
| 20 | Anulación (`anularrango`): qué nombre debe tener el archivo y qué responde (la Descripción Técnica no lo dice). Toki manda `RNC + ANECF + fecha.xml` y toma un 200 sin código como aceptado (§10.9) | Preguntar a la DGII; probar en TesteCF |
| 21 | e-CF de reemplazo de un rechazado: ¿lleva alguna referencia al rechazado (`InformacionReferencia`) o es un e-CF normal? ¿Fecha de emisión nueva o la de la venta? Con `secuenciaUtilizada = false`, ¿se prefiere reusar el mismo e-NCF o uno nuevo y anular el viejo con ANECF? Toki hoy: e-CF normal, fecha nueva, número nuevo | Preguntar a la DGII |
| 22 | QR de un e-CF sin comprador (una 34 a consumidor anónimo): ¿se omite `RncComprador` o va vacío? Toki lo omite | Preguntar a la DGII; probar el QR en TesteCF |
| 23 | Cuánto esperar con código 0 ("no encontrado") después de recibir un TrackId antes de reenviar. Toki sigue consultando (máximo cada 5 minutos) y no reenvía | Medir en TesteCF |
| 24 | En CerteCF no hay consulta de TrackIds por e-NCF: un *timeout* durante el set de pruebas obliga a reenviar. ¿Cuenta como envío duplicado en la certificación? | Preguntar a la DGII |
| 25 | Nota de crédito sobre una venta cuyo e-CF fue reemplazado: Toki la hace sobre el e-CF vigente (el reemplazo). Confirmar | Preguntar a la DGII |

---

## 12. Tickets (M13 · e-CF propio con la DGII)

Orden sugerido. Tamaño: S ≤ 1 día, M 2–4 días, L 1–2 semanas.

**TK-141 · Acceso a TesteCF, certificado de prueba y ADR-0009** (S, dueño + desarrollo)
Conseguir un RNC habilitado en pre-certificación (el de Toki o el de la tienda piloto: FI-GDF-016, Administrador e-CF, delegación de Firmante) y un certificado de persona física (cotizar Avansi y Digifirma: formato `.p12`, precio y vigencia). Hacer una prueba de humo manual (semilla → token → envío de una 32 por RFCE) con un script desechable. Escribir el ADR-0009 "e-CF propio" con las decisiones de §10.7 y marcar en la arquitectura y en ADR-0008 lo que cambia.
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
- XSD (e-CF 31 a 47, RFCE 32, ARECF, ACECF, ANECF y Semilla), carpeta `Documentación Técnica (XSD)`; índice en https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Paginas/documentacionSobreE-CF.aspx. Los de 31 a 34 se descargaron el 15-10-2026 (URL directas y SHA-256 en `src/Modules/Fiscal/Toki.Fiscal/Schemas/README.md`), por ejemplo https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaci%C3%B3n%20T%C3%A9cnica%20(XSD)/e-CF%2032%20v.1.0.xsd
- Norma General 10-2021 (Proveedores de Servicios de FE): https://dgii.gov.do/legislacion/normasGenerales/Documents/NG%20sobre%20Comprobantes%20Fiscales/Norma10-21.pdf
- Aviso 19-25 (certificados gratuitos solo para el Facturador Gratuito): https://dgii.gov.do/publicacionesOficiales/avisosInformativos/Documents/2025/19-25.pdf
- Prórroga de 6 meses para pequeños, micro y no clasificados (noticia de la DGII; la página pide sesión): https://dgii.gov.do/noticias/Paginas/DGII-otorga-prorroga-seis-meses-Pequeños-Micros-y-no-clasificados.aspx

Secundarias (verificar):
- INDOTEL, resoluciones de Avansi (070-2022) y de la Cámara de Comercio y Producción de Santo Domingo (108-21): https://indotel.gob.do/wp-content/uploads/2022/10/resoluci_n_070_2022_avansi.pdf y https://indotel.gob.do/wp-content/uploads/2022/10/res_108-21_que_autoriza_renovavion_a_ccpsd_para_operar_como_entidad_de_certificacion_fd.pdf
- EY, alerta sobre la prórroga: https://www.ey.com/es_ce/technical/tax/tax-alerts/republica-dominicana-dgii-otorga-prorroga-de-seis-meses-a-contribuyentes-identificados-como-pequenos-micros-y-no-clasificados-para-la-implementacion-de-fe
- Costo estimado de certificados: https://siemprealdia.co/republica-dominicana/impuestos/como-obtener-el-certificado-digital/
- Flujo RFCE de una librería abierta (referencia, sin licencia declarada: no copiar código): https://github.com/victors1681/dgii-ecf
