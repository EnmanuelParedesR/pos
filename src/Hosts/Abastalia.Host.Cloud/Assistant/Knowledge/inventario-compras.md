---
id: inventario-compras
title: Inventario, compras, suplidores y gastos
audience: public
links: [/precios, /carniceria]
sources: [backlog/BACKLOG.md, src/Client/Abastalia.Client.UI/Help/HelpContent.cs, docs/consultas-dgii.md, src/Cloud/panel/src/pages/site/SitePages.tsx]
---

## ¿Abastalia lleva el inventario? Existencias y kardex
Sí. Abastalia descuenta el inventario con cada venta y lo repone con cada compra, devolución o anulación. Cada producto tiene su historial de movimientos (kardex) que no se borra, su existencia actual y su costo promedio. Se pueden manejar varios almacenes (por ejemplo tienda y cuarto frío) con transferencias entre ellos, ajustes con motivo (merma, daño, consumo, corrección) y conteos físicos a ciegas que generan los ajustes. También muestra el valor del inventario, visible solo para quien tiene el permiso de ver costos.

## ¿Cómo cargo mis productos? Precios, libras y presentaciones
En Abastalia, en Productos, cada producto se vende por unidad o por peso (libra), con su código de barras, su precio con ITBIS incluido, su costo, su tasa de ITBIS (18 %, 16 %, 0 o exento) y su categoría con color. Un producto puede tener presentaciones (libra, paquete, caja) con su propio precio y código, precio mayorista, y la opción de venderse sin existencia. Las notas por categoría ("molida", "sin grasa") ayudan en la carnicería.

## ¿Cómo registro una compra a un suplidor?
En Abastalia, en Compras y gastos, registre la mercancía que entra con la factura del suplidor: sube la existencia y se actualiza el costo. La compra va de borrador a aplicada (y se puede anular). Si es a crédito, queda como cuenta por pagar; los pagos a suplidores se aplican a las facturas más viejas y cada suplidor tiene su estado de cuenta. Al escribir el RNC y el NCF del suplidor, Abastalia los consulta en la DGII y avisa si el NCF no es válido. También puede leer la factura con IA desde una foto o un PDF.

## ¿Abastalia registra gastos con NCF para el 606?
Sí. En Compras y gastos de Abastalia se registran los gastos con NCF que no llevan productos (por ejemplo luz, alquiler o servicios). Esos gastos y las compras alimentan el reporte 606 para su contador. Pagar a suplidores y registrar gastos es un permiso aparte de recibir mercancía.

## ¿Abastalia me dice qué pedir? Mínimos, máximos y pedido sugerido
Sí. En Abastalia cada producto puede tener un mínimo y un máximo. Con el pedido sugerido, Abastalia calcula cuánto pedir de lo que está en su mínimo o debajo, con el último suplidor, y crea la compra en borrador para que usted la revise. El Resumen avisa de lo que necesita atención.

## ¿Puedo cuadrar mis compras con lo que reporta la DGII?
Sí. En Compras de Abastalia está el "Cruce con la DGII": suba el Excel de comprobantes recibidos que descarga de la DGII y Abastalia le muestra los que faltan en sus compras, los montos o el ITBIS distintos, los repetidos y lo que usted reporta y la DGII no. Desde un faltante puede crear la compra en borrador.
