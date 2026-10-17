---
id: encargos-y-cotizaciones
title: Encargos con abono y cotizaciones
audience: public
links: [/deli, /carniceria]
sources: [src/Cloud/panel/src/pages/site/SitePages.tsx, backlog/BACKLOG.md, src/Client/Abastalia.Client.UI/Help/HelpContent.cs]
---

## ¿Abastalia maneja encargos o pedidos con abono?
Sí. En Abastalia un encargo tiene fecha y hora de entrega, y el cliente puede dejar un abono como anticipo, que entra a la caja. En la caja, la tecla F10 guarda el carrito como encargo y lista los encargos de hoy y mañana, que también salen en el Resumen. Al entregar, el encargo se cobra con el peso real y se descuenta lo que el cliente abonó. Los encargos y las cotizaciones están en Administración → Pedidos y catálogo.

## ¿Puedo hacer cotizaciones con Abastalia?
Sí. Abastalia hace cotizaciones con vigencia que se imprimen o se guardan como PDF. Una cotización no mueve el inventario ni la caja hasta que se convierte en venta; al aceptarla, se cobra con los precios del día y la caja muestra la diferencia si cambiaron. Abastalia no hace conduces por ahora.
