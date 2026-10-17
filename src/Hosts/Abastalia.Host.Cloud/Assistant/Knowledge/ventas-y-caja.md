---
id: ventas-y-caja
title: Vender, cobrar, abrir y cerrar caja (turnos, cuadre y cierre Z)
audience: public
links: [/precios, /colmado]
sources: [src/Client/Abastalia.Client.UI/Help/HelpContent.cs, backlog/BACKLOG.md, docs/permisos.md, src/Cloud/panel/src/pages/site/SitePages.tsx]
---

## ¿Cómo se vende y se cobra en la caja de Abastalia?
En la caja de Abastalia escanee el producto o escriba su nombre o código y pulse Enter; lo pesado con etiqueta de balanza entra con su peso. Toque una línea para cambiar su cantidad o peso (F3), su precio (F4) o darle descuento (F7). Para cobrar pulse F12, elija cómo paga el cliente y confirme. Acepta efectivo con devuelta, tarjeta, transferencia, pago mixto, venta a crédito y saldo a favor. Otras teclas: F5 poner la venta en espera, F6 cliente, F8 anular la venta, F9 menú de caja, F10 pedidos y F1 ayuda en cualquier pantalla.

## ¿Cómo abro la caja? Abrir turno con fondo
En Abastalia cada caja abre su propio turno. Al abrir, elija la caja y cuente el fondo inicial billete por billete; ese monto es la base del cuadre al cerrar. En Configuración → Cajas se puede fijar un fondo estándar por caja: abrir con otro monto pide la autorización de un supervisor. Un cajero no puede tener dos turnos abiertos a la vez.

## ¿Cómo se cierra la caja y se hace el cuadre? Cierre Z
Al final del turno, el cajero cuenta el efectivo de la gaveta por denominación sin ver lo que el sistema espera (cuadre ciego). Al confirmar, Abastalia muestra si la caja cuadró, sobró o faltó, por medio de pago, y genera el cierre Z con las ventas, las anulaciones, las devoluciones, las entradas y salidas de efectivo y los abonos de clientes a crédito. El cierre no se puede deshacer: F12 pide confirmación. Si hay diferencia, puede pedirse la aprobación de un supervisor. El Z se imprime en la térmica o en una página A4 para PDF, y el dueño lo ve en el panel, en Cuadres.

## ¿Puedo anular una venta o hacer una devolución?
Sí. En Abastalia una venta se anula solo dentro del mismo turno, con un motivo y la autorización de un supervisor si la tienda lo pide; repone el inventario y saca el dinero del turno. Fuera del turno, o para devolver solo una parte, se hace una devolución parcial contra la factura, por línea: el producto vuelve al inventario, se devuelve el efectivo o queda saldo a favor, y Abastalia emite la nota de crédito electrónica (e-CF 34). Las ventas del turno se ven, reimprimen, anulan o devuelven desde la caja.

## ¿Puedo sacar dinero de la caja o registrar gastos menores?
Sí. Desde el menú de caja (F9) de Abastalia se registran entradas de efectivo, retiros y gastos menores, cada uno con su motivo. No se editan: si hay un error se registra el movimiento contrario. Todo sale en el cierre Z del turno. Abrir la gaveta sin venta es un permiso aparte y queda registrado.

## ¿Abastalia maneja descuentos, precios por mayor y ventas en espera?
Sí. Abastalia permite descuento por línea o en toda la venta, en porcentaje o en monto, con un tope sin supervisor que define el dueño. Tiene niveles de precio: detalle y mayorista; con un cliente mayorista las líneas toman el precio mayorista. Las ventas en espera (F5) permiten guardar varias ventas por caja y retomarlas luego. El ITBIS (18 %, 16 %, 0 o exento) se calcula solo, con los precios con ITBIS incluido.

## ¿La supervisión con PIN es obligatoria? Autorización de supervisor
No. En Abastalia el dueño decide en Configuración → Autorizaciones qué acciones piden la autorización de un supervisor con su PIN (por ejemplo anular, retirar efectivo o cerrar con diferencia). Si nadie supervisa, como en un colmado de una persona, se pueden apagar: quien atiende lo hace solo y queda igual registrado en la auditoría.
