---
id: clientes-credito
title: Clientes, ventas a crédito y cuentas por cobrar
audience: public
links: [/colmado, /precios]
sources: [backlog/BACKLOG.md, src/Client/Abastalia.Client.UI/Help/HelpContent.cs, src/Cloud/panel/src/pages/site/SitePages.tsx, src/Cloud/panel/src/pages/SummaryPage.tsx]
---

## ¿Abastalia maneja ventas a crédito? Crédito con límite
Sí. Abastalia lleva las ventas a crédito (cuentas por cobrar) sin cuaderno. En la caja se cobra a un cliente con la forma de pago a crédito; cada cliente tiene un límite de crédito y pasarlo pide un supervisor. Los abonos se aplican a las facturas más viejas. Abastalia muestra la antigüedad de la deuda (30, 60 y 90 días) y el estado de cuenta de cada cliente, que se descarga en una página para PDF y se puede enviar por WhatsApp. Vender a crédito y cobrar abonos son permisos separados.

## ¿Cómo cobro un abono de un cliente a crédito?
En la caja de Abastalia, abra el menú de caja (F9) → Abono de cliente: busque al cliente, vea su deuda, cobre el abono en efectivo y entregue el recibo de abono. También se registra en Administración → Clientes, contra una caja abierta. Lo vendido a crédito y lo cobrado en abonos salen separados en el cierre de cada turno.

## ¿Dónde veo quién me debe?
En la app de la tienda de Abastalia, en Clientes, verá la lista de quién le debe y cuánto, con su antigüedad; toque un cliente para ver su estado de cuenta o cambiar su límite. Desde el celular, el panel del dueño muestra en el Resumen cuánto le deben y quiénes, y puede preguntarle a Talia "¿quién me debe más?".

## ¿Cómo registro un cliente? ¿Abastalia busca el RNC?
En Abastalia el cliente se crea en Clientes o rápido desde la caja, con su nombre, RNC o cédula, teléfono, tipo de comprobante, nivel de precio (detalle o mayorista) y límite de crédito. Al escribir el RNC, Abastalia consulta la DGII y muestra si está activo o suspendido. Para una factura de crédito fiscal (e-CF 31) se puede escribir el RNC solo para esa venta.
