# e-CF propio: cómo dejar de depender de un proveedor

> 15 de octubre de 2026. Basado en la Norma General 10-2021 (DGII) y las guías oficiales de certificación (julio 2025 y mayo 2026). Las guías dicen "publicación informativa sin validez legal": la norma es la que manda.

## Resumen

Hay dos caminos y se pueden hacer en orden:

1. **Ya mismo, sin ser proveedor certificado:** cada cliente se certifica como emisor electrónico **usando nuestro software** ("software adquirido a un proveedor externo"). Nosotros hablamos directo con la DGII. La DGII no exige que el fabricante del software sea un proveedor certificado.
2. **Después, Proveedor de Servicios de Facturación Electrónica (PSFE) certificado:** la DGII nos registra y publica como proveedor. A cambio, la certificación de cada cliente nuevo se vuelve mucho más corta y podemos vender el servicio de e-CF a otros sistemas.

Lo único que **no** se puede hacer sin un tercero es el **certificado digital**: cada cliente (y nosotros) necesita uno de una entidad de certificación autorizada (Avansi, Viafirma, etc.).

---

## Camino 1: cada cliente se certifica con nuestro software

Requisitos del cliente (guía DGII "Proceso de Certificación para ser Emisor Electrónico", julio 2025):
- RNC activo, clave de la Oficina Virtual (OFV) y autorización de NCF.
- Certificado digital para procedimiento tributario del representante, vinculado al RNC.
- Estar al día con los impuestos.
- Un software de e-CF: **el nuestro**.

Pasos (14 en el portal de certificación):

| Etapa | Qué se hace | Quién |
|---|---|---|
| Solicitud | Formulario FI-GDF-016 en la OFV | El representante del cliente |
| Postulación | Datos del software, URL de **Recepción**, **Aprobación Comercial** y **Autenticación**, versión; se firma el XML | Nosotros preparamos; el cliente firma |
| Pruebas de datos | Generar los e-CF y aprobaciones comerciales del set de la DGII y enviarlos | Nuestro software (automatizable) |
| Pruebas de simulación | e-CF con datos reales del negocio + PDF de la representación impresa | Nuestro software |
| Pruebas de comunicación | Recibir e-CF y aprobaciones comerciales que envía la DGII y responder acuses | Nuestros servicios en la nube |
| URL de producción | Registrar las URL definitivas | Nosotros |
| Declaración jurada | Firmada por el representante | El cliente |
| Certificación | La DGII habilita el menú de e-CF en la OFV | DGII |

**Ventaja:** no hay que pagar a ningún proveedor ni esperar a ser PSFE.
**Costo:** cada cliente pasa el set completo de pruebas; hay que automatizarlo para que sea un proceso de horas y no de semanas.

---

## Camino 2: ser Proveedor de Servicios de FE certificado (NG 10-2021)

### Requisitos (artículo 4)
- RNC activo.
- **Actividad económica de venta o desarrollo de software.**
- Clave de la OFV.
- **Ser emisor electrónico certificado nosotros mismos.** Lo necesitamos de todas formas para facturar las suscripciones con e-CF.
- Certificado digital de persona física para procesos tributarios.
- Al día con obligaciones formales y sustantivas.
- Cumplir las exigencias técnicas de la DGII y presentar la documentación que pida.

La norma **no exige un número mínimo de clientes** (una fuente secundaria hablaba de 3 o 5; no aparece en la norma).

### Servicios mínimos que debemos dar (artículo 5)
1. Emitir y enviar e-CF a la DGII y a los receptores (XML).
2. Generar y enviar la representación impresa.
3. Recibir e-CF de la DGII y de otros emisores.
4. Emitir, enviar y recibir **acuses de recibo**.
5. Emitir, enviar y recibir **aprobaciones y rechazos comerciales**.
6. Generar y enviar los **resúmenes de facturas de consumo** (menores de RD$250,000).
7. **Guardar los XML** según el artículo 16 de la NG 01-2020 y mostrarlos a la DGII cuando los pida.

### Condiciones del software (artículo 7)
XML, comunicación por **REST API** sobre **HTTPS**, y los servicios web por ambiente según el documento "Descripción Técnica de Facturación Electrónica".

### Proceso (artículos 8 a 11)
Solicitud (presencial u OFV) → la DGII responde en **10 días hábiles** con acceso al portal → **set de pruebas** del software → **declaración jurada** → **autorización** enviada por la OFV → publicación en el **registro público de proveedores** (artículo 12).

### Obligaciones (artículos 13, 14 y 16)
- Hacer la certificación de cada cliente **a nombre del cliente**.
- Confidencialidad de la información de los e-CF.
- Entregar a la DGII la información que pida.
- Almacenamiento seguro de datos fiscales, comprobantes y certificados.
- **Compromiso de niveles de servicio (SLA)** y **mesa de ayuda**.
- Responder legalmente por el mal uso de la información.
- Firma: el cliente firma con su certificado en nuestro sistema, **o** nos designa como representante y nos delega el rol de firmante para firmar automáticamente.
- **Recertificar el software** cada vez que la DGII publique una versión nueva del formato XML.
- La DGII puede **revocar** la certificación por incumplimiento; las faltas se sancionan como deberes formales (artículos 17 y 18; artículo 257 del Código Tributario).

### Modelos de operación permitidos
- **Servicio de desarrollo:** los certificados y la firma están en la infraestructura del cliente. Es nuestro modelo local-primero (la PC principal firma).
- **Software como servicio:** el proveedor custodia certificados y firma. Sería el modelo "En línea".

### Beneficio para nuestros clientes
Un cliente que se certifica a través de un PSFE certificado solo hace **pruebas de simulación** (más resúmenes de consumo en pre-certificación), sin pruebas de datos ni de comunicación. Su solicitud también se responde en 10 días hábiles (artículo 15).

---

## Qué tenemos que construir

| Pieza | Detalle |
|---|---|
| Generador XML | Todos los tipos de e-CF que usen los clientes (31, 32, 33, 34, 41, 43, 44, 45, 46, 47) según el formato oficial y sus XSD, más el resumen de consumo (RFCE) |
| Firma | XMLDSig con el .p12 (RSA-SHA256), `SignedXml` de .NET; código de seguridad y QR de la representación impresa |
| Autenticación con la DGII | Semilla → firmarla → token; renovar antes de que venza |
| Cliente DGII | Recepción de e-CF, consulta de resultado por TrackId, recepción de RFCE, anulación de rangos, consulta de directorio, envío de aprobaciones comerciales a receptores |
| Servicios nuestros en la nube, por cliente | **Autenticación** (emitir tokens), **Recepción** (recibir e-CF de suplidores y devolver acuse), **Aprobación Comercial** |
| Contingencia | Cola local con plazo de 72 h (ya diseñada en `arquitectura-v1-vsa.md`) |
| Almacenamiento | XML firmados, acuses y respuestas por el tiempo que exige la ley, cifrados y respaldados |
| Herramienta de certificación | Automatiza el set de pruebas de cada cliente |
| Operación | SLA, mesa de ayuda, monitoreo de los servicios de recepción (si se caen, los suplidores no pueden entregar e-CF a nuestros clientes) |

Hay librerías abiertas de otros lenguajes que sirven como referencia de los flujos (por ejemplo `dgii-ecf` en npm y `platinum-place/laravel-dgii`). Revisar su licencia antes de leer su código.

---

## Plan sugerido

1. **Registrar la empresa** con actividad de desarrollo y venta de software, y sacar el certificado digital del representante.
2. **Construir el módulo Fiscal** (generador, firma, cliente DGII, servicios de recepción) en el ambiente de pre-certificación de la DGII.
3. **Certificar nuestra propia empresa como emisor electrónico** con nuestro software (lo necesitamos para facturar suscripciones).
4. **Certificar las tiendas piloto** por el Camino 1.
5. **Solicitar la certificación como PSFE** (NG 10-2021) en cuanto el software esté estable.
6. Mantener un proveedor externo **solo como respaldo** mientras tanto, detrás de `IEcfProvider`.

## Costos y beneficios

- **Ahorro por tienda:** el pago al proveedor (US$10–25/mes) y la certificación de US$183.05.
- **Nueva línea de ingresos:** vender e-CF a otros sistemas como PSFE.
- **Lo que sigue costando:** certificados digitales, nube, almacenamiento, mesa de ayuda, horas de desarrollo y la responsabilidad legal.
- **No encontramos cobros de la DGII** por certificar (ni emisor ni proveedor).

## Riesgos

- **Plazo:** los pequeños contribuyentes deben emitir e-CF desde el 15 de noviembre de 2026; nuestro módulo no estará listo para esa fecha. Las primeras tiendas pueden necesitar un proveedor externo de transición.
- **Responsabilidad legal y sanciones** por fallas de servicio o manejo de datos (Ley 32-23, Ley 172-13, Código Tributario).
- **Disponibilidad 24/7** de los servicios de recepción y autenticación.
- **Cambios de versión del XML** obligan a recertificar.
- **Custodia de certificados** si se ofrece el modelo en la nube.

## Preguntas para la DGII

1. Plazo de conservación de los XML que aplica a los proveedores (artículo 16 de la NG 01-2020 y artículo 50 del Código Tributario).
2. Si una tienda certificada con nuestro software (Camino 1) puede luego pasar a nuestro PSFE sin volver a certificarse.
3. Lista vigente de actividades económicas aceptadas como "desarrollo o venta de aplicaciones informáticas".
4. Documentación de soporte exigida para la solicitud de PSFE.

## Fuentes

- Norma General núm. 10-2021, DGII: https://dgii.gov.do/legislacion/normasGenerales/Documents/NG%20sobre%20Comprobantes%20Fiscales/Norma10-21.pdf
- Proceso de Certificación para ser Emisor Electrónico (julio 2025): https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaciones%20Proceso%20de%20Certificaci%C3%B3n%20FE/Proceso%20de%20Certificacion%20para%20ser%20Emisor%20Electronico.pdf
- Emisor Electrónico con Proveedor de Servicios de FE Certificado (mayo 2026): https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Documentaciones%20Proceso%20de%20Certificaci%C3%B3n%20FE/Proceso-Certificacion-EmisorElectronico-Proveedor-Servicios-FECertificado.pdf
- Guía para ser Proveedores de Servicios de FE Autorizados (julio 2025): https://dgii.gov.do/publicacionesOficiales/bibliotecaVirtual/contribuyentes/facturacion/Documents/Facturaci%C3%B3n%20Electr%C3%B3nica/Guia-Basica-Proveedor-de-Servicios-de-Facturacion-Electronica.pdf
