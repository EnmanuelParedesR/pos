---
id: glosario
title: Glosario del e-CF y de Abastalia
audience: public
links: [/glosario, /guia-e-cf, /e-cf]
sources: [src/Cloud/panel/src/pages/site/SitePages.tsx, docs/contingencia-ecf.md, src/Hosts/Abastalia.Host.Cloud/Assistant/PanelHelp.cs]
---

## ¿Qué es NCF, e-NCF y cuál es la diferencia?
El NCF (número de comprobante fiscal) numera el comprobante en papel: la letra B, el tipo y 8 dígitos, 11 caracteres en total (ejemplo B0200000001). El e-NCF numera el comprobante fiscal electrónico (e-CF): la letra E, el tipo y 10 dígitos, 13 caracteres (ejemplo E320000000001). Las secuencias de e-NCF las autoriza la DGII en la Oficina Virtual y se cargan en Abastalia en Configuración → Comprobantes. La serie B se usa en Abastalia solo para la contingencia.

## ¿Qué son los tipos 31, 32, 33 y 34 del e-CF?
Los tipos de e-CF de la DGII son: 31 factura de crédito fiscal (para empresas con RNC que la usan para su crédito fiscal), 32 factura de consumo (cliente final), 33 nota de débito y 34 nota de crédito. Abastalia emite los tipos 31, 32 y 34; la nota de débito (33) no está disponible por ahora.

## ¿Qué es el RNC, la OFV y el ITBIS?
El RNC (Registro Nacional del Contribuyente) es el número fiscal del negocio ante la DGII; las personas físicas pueden usar su cédula. La OFV (Oficina Virtual de la DGII) es el portal de la DGII para solicitudes, secuencias, declaraciones y la contingencia; se entra con usuario y clave que solo usted conoce. El ITBIS es el impuesto a la venta: Abastalia maneja las tasas 18 %, 16 %, 0 y exento, con los precios con ITBIS incluido.

## ¿Qué es el 606, el 607 y el 608?
Son reportes que se envían a la DGII por la OFV, a más tardar el día 15 del mes siguiente: el 606 lista las compras y gastos del mes, el 607 las ventas del mes y el 608 los comprobantes anulados en el mes. Abastalia los prepara para su contador en Excel o CSV.

## ¿Qué es TesteCF, CerteCF y eCF (ambientes de la DGII)?
Son los ambientes de la DGII para la factura electrónica. TesteCF (Pre-certificación) es el ambiente de pruebas libres. CerteCF (Certificación) es el ambiente del set de pruebas y la simulación de la certificación. eCF (Producción) es el ambiente real, donde se factura de verdad. En Abastalia el ambiente se elige en Configuración → Comprobantes; lo emitido en TesteCF o CerteCF no entra en el 607.

## ¿Qué es el RFCE, la representación impresa y el certificado .p12?
El RFCE es el resumen firmado de la factura de consumo de menos de RD$250,000 que se envía a la DGII; el e-CF completo se guarda en la tienda. La representación impresa es la versión en papel del e-CF, con QR y código de seguridad. El certificado .p12 (o .pfx) es el certificado digital de persona física, de una entidad autorizada por INDOTEL, con el que se firma cada e-CF.

## ¿Qué es el cierre Z, el cuadre ciego y el fondo de caja?
En Abastalia, el fondo de caja es el efectivo con que abre el turno. El cuadre ciego es contar el efectivo al cerrar sin ver lo que el sistema espera. El cierre Z es el reporte del cierre de un turno: ventas, formas de pago, anulaciones, devoluciones, entradas y salidas de efectivo, abonos de clientes a crédito, efectivo esperado, contado y la diferencia.

## ¿Qué es la contingencia, el acuse de recibo y la aprobación comercial?
La contingencia es cuando no se puede emitir e-CF: se factura con serie B y luego se regulariza con e-CF. El acuse de recibo es la respuesta firmada que el comprador electrónico envía al recibir un e-CF; Abastalia lo responde por usted las 24 horas cuando un suplidor le envía uno. La aprobación comercial es la aceptación o el rechazo de esa factura por el comprador, que el dueño decide en el panel, en Recibidos, o en la tienda.
