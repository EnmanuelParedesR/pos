---
id: factura-electronica-e-cf
title: Factura electrónica (e-CF) y la fecha de la DGII
audience: public
links: [/e-cf, /guia-e-cf, /glosario, /certificacion, /comparar]
sources: [src/Cloud/panel/src/pages/site/SitePages.tsx, docs/contingencia-ecf.md, src/Cloud/panel/src/pages/site/LegalPages.tsx]
watch: [https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Paginas/documentacionSobreE-CF.aspx]
---

## ¿Qué es el e-CF o factura electrónica?
El e-CF (comprobante fiscal electrónico, factura electrónica) es la factura en formato XML, numerada con un e-NCF, firmada con el certificado digital del negocio y enviada a la DGII, que la valida. El cliente recibe una versión impresa (representación impresa) con un código QR y un código de seguridad. Abastalia firma cada e-CF en la PC de su tienda y lo envía directo a la DGII, sin intermediarios y sin cupo mensual. Las palabras del e-CF están explicadas en /glosario.

## ¿Cuándo me toca la factura electrónica? Fecha límite del e-CF
Según la DGII, los contribuyentes pequeños, micro y no clasificados (Ley 32-23) deben emitir e-CF desde el 15 de noviembre de 2026. La DGII dio una prórroga de seis meses en mayo de 2026 (Aviso 06-26) a todos los pequeños, micro y no clasificados: no hay que pedirla. Los grandes locales y medianos emiten solo e-CF desde el 1 de noviembre de 2026. Confirme su clasificación y su fecha en la Oficina Virtual (OFV) de la DGII o con su contador. Esto no es asesoría legal.

## ¿Qué necesito para facturar con e-CF?
Para emitir e-CF con Abastalia necesita: un RNC activo y al día con acceso a la Oficina Virtual (OFV) de la DGII y alta de NCF; un certificado digital de persona física, comprado a una entidad autorizada por INDOTEL (Avansi o la Cámara de Comercio y Producción de Santo Domingo, Digifirma), en archivo .p12 o .pfx; un programa certificado ante la DGII para su RNC (la certificación, que Abastalia hace con usted); y sus secuencias e-NCF, que autoriza la DGII en la OFV. La DGII no cobra por certificarle ni por cada documento enviado. Paso a paso en /guia-e-cf.

## ¿Qué tipos de e-CF emite Abastalia?
Abastalia emite la factura de consumo (e-CF tipo 32), la factura de crédito fiscal (tipo 31) para clientes con RNC y la nota de crédito (tipo 34) para devoluciones. Si la empresa compradora es emisora electrónica, Abastalia le entrega la factura de crédito fiscal directamente. La nota de débito (tipo 33) no está disponible por ahora en Abastalia.

## ¿Qué pasa si no cumplo con la factura electrónica?
La Ley 32-23 (artículo 27) y el Código Tributario (artículo 257) prevén sanciones para quien no cumpla. Abastalia no da asesoría legal ni tributaria: para saber cómo le aplica a su negocio, consulte a su contador o a la DGII (facturación electrónica: 809-689-3444, opción 4).

## ¿Cuánto tiempo hay que guardar los e-CF?
La DGII pide guardar los e-CF diez años. Abastalia guarda cada e-CF firmado en la tienda y una copia en la nube durante 10 años; desde el panel puede buscar un comprobante por su e-NCF y descargar el XML firmado. La factura de consumo de menos de RD$250,000 se reporta a la DGII con un resumen firmado (RFCE) y Abastalia guarda el e-CF completo.

## ¿Puedo usar el certificado gratis que da la DGII?
No con Abastalia. El certificado digital que entrega la DGII solo sirve para el Facturador Gratuito de la DGII. Para Abastalia necesita un certificado de persona física comprado a una entidad autorizada por INDOTEL, en archivo .p12 o .pfx (no en token USB ni en la nube del emisor). Compare ambas opciones en /comparar.
