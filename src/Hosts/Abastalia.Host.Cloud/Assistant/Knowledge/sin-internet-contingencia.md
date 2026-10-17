---
id: sin-internet-contingencia
title: Sin internet, sin luz y contingencia del e-CF
audience: public
links: [/e-cf, /glosario]
sources: [src/Cloud/panel/src/pages/site/SitePages.tsx, docs/contingencia-ecf.md, src/Hosts/Abastalia.Host.Cloud/Assistant/CertificationFaq.cs]
watch: [https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Documentacin%20sobre%20eCF/Instructivos%20sobre%20Facturaci%C3%B3n%20Electr%C3%B3nica/Instructivo%20de%20Contingencia%20de%20FE.pdf]
---

## ¿Abastalia funciona sin internet? ¿Qué pasa si se va el internet?
Sí. Abastalia sigue vendiendo sin internet. Las cajas venden contra la PC principal de la tienda, que firma cada e-CF en el momento; el recibo lleva la leyenda de contingencia y los comprobantes quedan en cola. Cuando vuelve la conexión, Abastalia los envía solos a la DGII, que da 72 horas desde la emisión. Abastalia avisa a las 24 horas y a las 60 horas si algo sigue sin salir. No tiene que hacer nada ni declarar contingencia por quedarse sin internet.

## ¿Y si se va la luz?
Con un apagón, las ventas que Abastalia ya registró quedan guardadas en la PC principal y los e-CF pendientes salen solos cuando vuelven la energía y la conexión, dentro de las 72 horas que da la DGII. Si la PC principal no puede emitir e-CF por más tiempo (por ejemplo, se dañó o se venció el certificado), Abastalia lo guía para declarar la contingencia, facturar con serie B y luego regularizar.

## ¿Qué es la contingencia y cuándo se usa la serie B?
La contingencia es cuando el negocio no puede emitir e-CF, por ejemplo sin certificado digital, con el certificado vencido o con la PC principal dañada. En ese caso: declare la contingencia en la OFV de la DGII; en Abastalia, Comprobantes electrónicos → "Contingencia (serie B)", escriba el mismo motivo y pulse «Activar contingencia»; la caja factura con comprobantes NCF serie B en papel. Dura como máximo 15 días calendario. Al salir, declare la salida en la OFV, pulse «Terminar contingencia» y «Regularizar»: Abastalia emite un e-CF que reemplaza cada comprobante B. Tiene 30 días para regularizar.

## ¿Qué pasa si la DGII está caída?
Si la DGII no responde, Abastalia guarda los e-CF firmados y los envía cuando vuelve el servicio; usted sigue vendiendo. Si la DGII pasa más de 15 días hábiles caída, la OFV habilita otra forma de reportar: llame a la DGII (809-689-3444, opción 4). En la app, el botón «Consultar a la DGII» le dice si el problema es su internet ("No se llega a la DGII") o la DGII ("La DGII no responde").

## ¿Cómo evito tener que usar la contingencia?
Para no caer en contingencia con Abastalia: renueve el certificado digital antes de que venza (Abastalia avisa 30 días antes); tenga cargado un rango de comprobantes serie B (B02 consumo y, si vende a empresas, B01 crédito fiscal) en "Contingencia (serie B)", que Abastalia solo usa si no puede firmar; y deje la hora de Windows en automático, porque con más de 2 minutos de diferencia con la DGII los e-CF se guardan sin firmar hasta que la hora se corrija.
