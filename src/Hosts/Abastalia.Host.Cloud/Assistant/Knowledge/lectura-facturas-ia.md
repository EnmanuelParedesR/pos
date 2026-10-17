---
id: lectura-facturas-ia
title: Leer facturas de suplidores con IA
audience: public
links: [/precios, /privacidad]
sources: [docs/cuotas.md, backlog/BACKLOG.md, src/Cloud/panel/src/pages/site/SitePages.tsx, src/Cloud/panel/src/pages/site/LegalPages.tsx, src/Cloud/panel/src/pages/UsageCard.tsx]
---

## ¿Abastalia lee las facturas de los suplidores? Escanear factura con IA
Sí. En la app de la tienda, en Compras → «Leer factura con IA», suba la foto o el PDF de la factura del suplidor y Abastalia saca el RNC, el NCF, la fecha, las líneas, el ITBIS y el total, y crea la compra en borrador. Siempre la revisa una persona antes de aplicarla; Abastalia valida el NCF, el RNC, la fecha y los totales. Acepta PDF, JPG, PNG o WEBP de hasta 4 MB. La nube no guarda el archivo.

## ¿Cuántas facturas puedo leer con IA al mes?
Todos los planes de Abastalia incluyen 20 facturas de suplidor leídas con IA al mes. El adicional "Lectura de facturas" suma 300 más al mes por unidad (US$5 al mes). En Compras la app muestra "Lecturas con IA este mes: le quedan X de Y"; cuando se agotan, avisa la fecha en que se renuevan (el día 1). Si el suplidor le envió un e-CF electrónico, su XML se lee sin IA y sin gastar lecturas. Con la licencia en modo restringido, la lectura con IA no está disponible.
