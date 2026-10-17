---
id: cambiarse-a-abastalia
title: Cambiarse a Abastalia desde otro sistema
audience: public
links: [/certificacion#ya-emito, /certificacion, /precios#contacto]
sources: [src/Cloud/panel/src/pages/site/SitePages.tsx, docs/servicio-certificacion.md]
---

## Ya tengo otro sistema de facturación. ¿Puedo cambiarme a Abastalia?
Sí. Puede pasar a Abastalia desde otro punto de venta o programa de facturación. Si todavía no emite e-CF, instala Abastalia, carga sus productos y se certifica con la DGII usando Abastalia (vea /certificacion). Si ya emite e-CF con otro programa, el camino es más corto: vea la sección "Ya emito e-CF con otro programa" en /certificacion#ya-emito. Para que le ayudemos a planear el cambio, escríbanos en /precios#contacto.

## Ya emito e-CF con otro programa. ¿Tengo que certificarme otra vez?
Si su RNC ya es emisor electrónico, no debería repetir la certificación: solo cambia el programa (por confirmar con la DGII). Los pasos con Abastalia son: importar en Abastalia el mismo certificado .p12, si sigue vigente; en la OFV, "Mantenimiento Directorio FE", poner las direcciones de Abastalia completas (no solo el dominio); escribir el último e-NCF emitido para que Abastalia siga desde el siguiente (o anular los sobrantes si la DGII lo pide); y dejar de usar el otro programa, porque la misma secuencia repetiría números. Luego pasa a producción y revisa la primera venta. En la app esto está en Comprobantes electrónicos → "Ya emito e-CF con otro programa".

## ¿Puedo pasar mis productos y clientes de mi sistema anterior?
Abastalia no publica hoy una herramienta para importar productos, clientes o historial desde otro sistema. Los productos se cargan en la app de la tienda, en Productos, por libra o por unidad. Si tiene muchos productos, escríbanos en /precios#contacto y le indicamos la mejor forma de hacerlo.
