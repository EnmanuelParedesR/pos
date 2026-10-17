---
id: panel-pantallas
title: Las pantallas del panel de Abastalia
audience: panel
links: [/app, /app/sales, /app/shifts, /app/stores, /app/assistant, /app/received, /app/notifications, /app/security, /app/members, /app/billing]
sources: [src/Cloud/panel/src/Panel.tsx, src/Hosts/Abastalia.Host.Cloud/Assistant/PanelHelp.cs, src/Cloud/panel/src/pages/SummaryPage.tsx, src/Cloud/panel/src/pages/SalesPage.tsx, src/Cloud/panel/src/pages/ShiftsPage.tsx, src/Cloud/panel/src/pages/EcfLookup.tsx]
---

## ¿Qué hay en cada pantalla del panel? Mapa del panel
El panel de Abastalia tiene estas secciones:
- Resumen (/app): ventas de hoy y de los últimos 7 días, cómo le pagaron, quién le debe, lo más vendido, ventas por hora y lo que necesita atención.
- Ventas (/app/sales): ventas por día, lo más vendido y ventas por cajero en las fechas que elija, y la búsqueda de un comprobante electrónico.
- Cuadres (/app/shifts): cada cierre de turno.
- Tiendas (/app/stores): sus tiendas, cajas conectadas, respaldos en la nube y Consumo del mes.
- Asistente (/app/assistant): Talia.
- Recibidos (/app/received): e-CF de sus suplidores.
- Avisos (/app/notifications), Seguridad (/app/security), Equipo (/app/members) y Facturación (/app/billing).
En el celular, la barra de abajo muestra Resumen, Ventas, Cuadres, Tiendas y "Más".

## ¿Dónde veo los cuadres de caja y el cierre Z en el panel?
En el panel de Abastalia, en Cuadres (/app/shifts), está cada cierre de turno con la tienda, el cajero, la caja, el efectivo esperado, el contado y la diferencia (faltante o sobrante). Puede ver solo los cierres con diferencias. Toque uno para ver e imprimir su Z: fondo inicial, ventas por forma de pago, anuladas, devoluciones, entradas y salidas de efectivo, «Abonos de fiado» (cobros a clientes a crédito) y aperturas de gaveta sin venta. Los cierres aparecen cuando una caja conectada cierra su turno.

## ¿Cómo busco o descargo un comprobante electrónico (e-CF)?
En el panel de Abastalia, en Ventas (/app/sales), use «Buscar un comprobante electrónico»: escriba el e-NCF (por ejemplo E310000000001) y descargue el XML firmado. Abastalia guarda el XML de cada e-CF 10 años en la nube. Los comprobantes de hace más de 90 días pueden tardar en estar disponibles, y las consultas al archivo cuentan en el Consumo del mes.

## ¿Quién puede ver cada pantalla del panel?
En el panel de Abastalia, el Dueño ve todo y es el único que entra a Equipo (invitar o quitar personas) y a Facturación («Plan y facturación»), y el único que crea tiendas, conecta cajas y ve el Consumo del mes. Un miembro de Solo lectura ve Resumen, Ventas, Cuadres, Tiendas, Asistente, Recibidos, Avisos y Seguridad, pero no cambia nada, y no ve costos ni ganancias salvo que el Dueño lo permita.

## ¿Puedo cambiar productos o precios desde el panel?
No. El panel de Abastalia es para ver cómo van sus tiendas. Los productos, precios, inventario, compras, clientes, pedidos, facturas, reportes 606/607/608, turnos y comprobantes electrónicos se manejan en la app de la tienda, en Administración, en la PC principal.

## El panel no muestra mis ventas de hoy. ¿Por qué?
El panel de Abastalia recibe los datos que sube la tienda cuando hay internet. Si no ve ventas: revise que la tienda esté conectada (Tiendas debe mostrar la caja conectada y la hora del último dato), que la PC principal esté encendida y con internet, y en la PC principal Configuración → Nube, que dice si el último envío falló (se reintenta solo). Sin internet, los datos esperan en la tienda y suben al volver. Puede preguntarle a Talia: "¿cuándo llegó el último dato de mi tienda?".
