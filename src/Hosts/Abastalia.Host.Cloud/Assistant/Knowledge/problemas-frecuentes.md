---
id: problemas-frecuentes
title: Problemas frecuentes y cómo resolverlos
audience: panel
links: [/app/stores, /app/billing, /app/notifications]
sources: [docs/contingencia-ecf.md, docs/instalador-web.md, docs/cobro-paddle.md, docs/permisos.md, docs/cuotas.md, src/Client/Abastalia.Client.UI/Help/HelpContent.cs, backlog/BACKLOG.md]
---

## La caja dice que no hay conexión con el servidor o no encuentra la PC principal
En Abastalia las cajas venden contra la PC principal por la red de la tienda; sin conexión con el servidor no se cobra y la venta espera en pantalla. Revise que la PC principal esté encendida y en la misma red (mismo Wi-Fi o cable). En Windows 11 una red nueva queda como Pública y bloquea a las cajas: en la PC principal vaya a Configuración de Windows → Red e Internet y ponga la red como Privada. Si cambió la PC principal, la caja avisa que el servidor cambió y hay que volver a unirla con un código nuevo (Configuración → Equipos).

## Dice "La hora de la PC está atrasada" o los e-CF salen sin código de seguridad
Abastalia necesita la hora correcta. Si la hora de la PC principal difiere más de 2 minutos de la de la DGII, Abastalia sigue vendiendo pero guarda cada factura sin firmar (el recibo sale sin código de seguridad ni QR y con la leyenda de contingencia). Corrija la hora en Windows: Configuración → Hora e idioma → Fecha y hora → "Establecer la hora automáticamente" y «Sincronizar ahora». En cuanto la hora está bien, Abastalia firma y envía solo lo pendiente. No declare contingencia por esto.

## Tengo e-CF en cola o sin enviar a la DGII
Si la caja muestra "e-CF en cola" o el Resumen avisa de comprobantes sin enviar, casi siempre es el internet de la tienda o la DGII. Abastalia los envía solo al volver la conexión; la DGII da 72 horas desde la firma. Abastalia avisa a las 24 horas (ámbar) y a las 60 horas (rojo). En Comprobantes electrónicos, «Consultar a la DGII» dice si el problema es su internet o la DGII. Revise también la hora de la PC y que el certificado no esté vencido.

## La DGII rechazó un comprobante. ¿Qué hago?
En la app de la tienda de Abastalia, en Comprobantes electrónicos → "Rechazados por la DGII", lea el mensaje de la DGII y pulse «Reemplazar»: Abastalia emite un e-CF nuevo con los mismos datos, ligado al rechazado, y la venta pasa al nuevo e-NCF. No se hace nota de crédito por un rechazo. Mientras haya rechazados sin reemplazar, el 607 de ese periodo no se exporta. Active en el panel, en Avisos, el correo de comprobantes rechazados.

## No me deja crear o activar otro cajero
Abastalia limita cuántos cajeros puede tener habilitados según su plan (Esencial 2, Estándar 4, Pro 8, más los adicionales). El límite solo impide crear, activar o darle un rol que vende a una persona más; nunca detiene una venta ni un cuadre. Desactive a quien ya no trabaja en Configuración → Usuarios para liberar su lugar, o agregue "Cajero adicional" en el panel, en «Plan y facturación».

## Se acabaron las preguntas a Talia o las facturas leídas con IA
Cuando un servicio llega al límite del mes, Abastalia lo detiene hasta el día 1 del mes siguiente. Vea lo usado en el panel, en Tiendas → Consumo del mes. Para seguir, agregue en «Plan y facturación» el adicional "Asistente +1,000 preguntas" o "Lectura de facturas" (300 más al mes). La caja, las ventas y el e-CF no se afectan.

## No imprime la factura o no abre la gaveta
En la caja de Abastalia, vaya a Configuración → Equipos de la caja, elija la impresora correcta y pulse probar. La gaveta se abre por la impresora térmica, así que debe estar conectada a ella. La opción "Imprimir factura" se recuerda en cada PC. Si la impresora sigue sin imprimir, anote la marca y el modelo y escriba a Abastalia.

## No llegan los correos de Abastalia
Los correos de Abastalia (confirmar correo, cambiar contraseña, invitaciones, avisos) pueden tardar unos minutos. Busque en el correo no deseado o en promociones. Si pidió varios seguidos, espere unos minutos antes de intentar otra vez. Revise en Avisos que tenga marcados los avisos que quiere; los avisos se dejan de enviar si el negocio gastó sus correos del mes.
