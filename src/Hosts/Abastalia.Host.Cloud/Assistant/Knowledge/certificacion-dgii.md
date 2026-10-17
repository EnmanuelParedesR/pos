---
id: certificacion-dgii
title: Certificación e-CF con la DGII
audience: public
links: [/certificacion, /certificacion#preparacion, /guia-e-cf, /precios]
sources: [src/Cloud/panel/src/pages/site/SitePages.tsx, src/Hosts/Abastalia.Host.Cloud/Assistant/CertificationFaq.cs, docs/servicio-certificacion.md, src/Cloud/panel/src/pages/site/config.ts]
watch: [https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Paginas/documentacionSobreE-CF.aspx]
---

## ¿Qué es la certificación con la DGII y por qué la necesito?
Para emitir factura electrónica (e-CF) con un programa como Abastalia, la DGII debe probar ese programa con su RNC: eso es la certificación como emisor electrónico. Abastalia arma, firma y envía las pruebas; usted hace los pasos que piden su clave de la Oficina Virtual (OFV). Las etapas son: solicitud FI-GDF-016 desde la OFV, pruebas libres, postulación, set de pruebas, simulación, comunicación (acuses), declaración jurada y producción, cuando la DGII lo habilita y usted carga sus secuencias e-NCF. Detalles en /certificacion.

## ¿Qué necesito antes de empezar la certificación?
Para certificarse con Abastalia prepare: RNC activo y al día con la DGII (sin declaraciones pendientes); acceso a la OFV de la empresa (pruebe que entra antes); alta de NCF; un representante vinculado al RNC que será el Usuario Administrador e-CF; el certificado digital de persona física para procedimientos tributarios a nombre de ese representante, comprado a Avansi (Viafirma) o a la Cámara de Comercio y Producción de Santo Domingo (Digifirma), como archivo .p12 o .pfx con su contraseña; y la PC principal con Abastalia instalado, encendida y con internet. La lista completa está en /certificacion#preparacion.

## ¿Abastalia necesita mi clave de la Oficina Virtual (OFV)?
No. Abastalia nunca pide, guarda ni ve su clave de la OFV ni la contraseña de su certificado digital. Los pasos que piden la clave los hace usted en su navegador; la contraseña de su certificado (.p12) se escribe una sola vez al importarlo en Abastalia y no se guarda. Si alguien le pide su clave en nombre de Abastalia, no la dé.

## ¿Cuánto tarda la certificación?
No hay una fecha fija: varios pasos dependen de la DGII o de la entidad que emite el certificado. Lo que hace Abastalia (set de pruebas, simulación, firma de archivos) se puede completar en una tarde cuando la DGII abre cada paso. Lo más lento suele ser la espera de la DGII entre una etapa y otra.

## ¿Abastalia me ayuda a certificarme? Acompañamiento
Sí. Puede certificarse por su cuenta con el asistente de certificación de la app (incluido en la licencia), o con acompañamiento: un técnico de Abastalia le guía en dos sesiones remotas cortas, con usted presente; usted escribe sus claves y el técnico pausa la vista mientras tanto. El acompañamiento cuesta US$49 (≈ RD$3,015), gratis con plan anual, y gratis con cualquier plan pagado antes del 31/12/2026. El certificado digital no está incluido. Se pide en la app (Comprobantes electrónicos → «Solicitar acompañamiento») o en /precios#contacto.

## ¿Qué pasa si la DGII rechaza algo en la certificación?
Si la DGII rechaza un comprobante del set de pruebas, el set se reinicia: se descarga el Excel nuevo del portal, se sube otra vez en Abastalia y se vuelve a enviar. Si la DGII rechaza la solicitud FI-GDF-016, lea el motivo en el buzón de la OFV, corríjalo con la DGII o su contador (por ejemplo, declaraciones pendientes) y envíela de nuevo.

## ¿Y si no llego a certificarme antes del 15 de noviembre de 2026?
Plan B para negocios de poco volumen: emitir con el Facturador Gratuito de la DGII (en la OFV) las facturas que deben ser e-CF mientras termina la certificación con Abastalia, y seguir usando Abastalia para la caja, el inventario y los cierres. Anote el e-NCF de cada factura del Facturador Gratuito junto a la venta de Abastalia para cuadrar el 607 con su contador. Confirme con la DGII o su contador si su negocio califica.

## ¿Abastalia está certificado por la DGII? ¿Es un proveedor autorizado?
La DGII certifica a cada negocio (su RNC) como emisor electrónico, no a un programa en general: usted se certifica una vez, con su propio certificado digital, usando Abastalia. Abastalia es el programa que queda en medio: arma, firma con su certificado y envía cada e-CF a la DGII, recibe la respuesta y guarda el comprobante. Por eso su factura electrónica es tan válida como la de cualquier otro programa certificado con su RNC. Abastalia no aparece hoy en la lista de proveedores de servicios de facturación electrónica de la DGII, y no hace falta para que usted emita: el emisor es su negocio.
