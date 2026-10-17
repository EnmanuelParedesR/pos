---
id: licencia-estados
title: Estados de la licencia (prueba, activa, gracia, restringido, bloqueada)
audience: panel
links: [/app/billing, /terminos]
sources: [src/Hosts/Abastalia.Host.Cloud/Assistant/PanelHelp.cs, docs/licencias.md, docs/cobro-paddle.md, src/Cloud/panel/src/pages/site/LegalPages.tsx, src/Client/Abastalia.Client.UI/Pages/Admin/Settings.razor]
---

## ¿Qué estados tiene la licencia de Abastalia?
La licencia de Abastalia vive en la PC de la tienda y pasa por estas etapas:
- Prueba: sin licencia, 14 días (30 si llegó recomendado) desde la primera venta, todo encendido.
- Activa: licencia vigente. Con el plan pagado en línea, la tienda conectada la recibe sola de la nube.
- Gracia: venció hace menos de 15 días; todo sigue igual.
- Restringido: terminó la prueba o la gracia; funciones pagas apagadas, la PC principal y una caja, 2 cajeros. Todavía vende y factura, y el aviso dice el día en que se detiene la venta.
- Bloqueada: 30 días después del vencimiento o del fin de la prueba. No vende ni abre turnos.
El estado se ve en la app de la tienda, en Configuración → Licencia.

## Mi licencia está bloqueada. ¿Qué puedo hacer y qué no?
Con la licencia de Abastalia bloqueada no se puede vender, abrir turnos, crear encargos, anular, devolver ni emitir e-CF nuevos. Sí se puede: entrar, cerrar el turno abierto, enviar a la DGII los e-CF ya emitidos (y reemplazar un rechazado), ver el historial y los reportes, exportar el 606, 607 y 608, y respaldar y restaurar. Ningún dato se pierde. La caja muestra "Su licencia está vencida" (o "Terminó la prueba gratis") con los botones «Activar licencia» y «Cerrar el turno».

## ¿Cómo salgo del modo restringido o del bloqueo? Activar licencia
Hay dos formas. Pague o renueve su plan en el panel, en «Plan y facturación»: la tienda conectada recibe la licencia nueva sola en minutos. O, si Abastalia le envió un archivo de licencia (.jws) o un código, instálelo en la PC principal: Configuración → Licencia → elija el archivo o pegue el código → «Activar licencia». Aplica al momento, sin reinstalar y con todas sus ventas. Para comprar una licencia sin pago en línea, escriba a Abastalia con el nombre y el RNC de su tienda.

## ¿Si desconecto el internet o atraso la hora de la PC la licencia dura más?
No. La licencia de Abastalia vence sola aunque la tienda nunca se conecte, y atrasar el reloj de Windows no la estira: Abastalia guarda la hora más tardía que vio y avisa "La hora de la PC está atrasada". Además, una hora mal puesta impide firmar los e-CF. Deje la hora de Windows en automático.

## Dice "Esta licencia es de otro negocio" o "La licencia no es válida"
Abastalia rechaza una licencia cuyo RNC no es el de Configuración → Negocio ("Esta licencia es de otro negocio"), una licencia que no firmó Abastalia ("La licencia no es válida o no la firmó Abastalia") o una más antigua que la última instalada. Revise que el RNC del negocio esté bien escrito y pida a Abastalia una licencia nueva con el RNC correcto.

## Abastalia suspendió el servicio de mi negocio
Si la caja dice "Abastalia suspendió el servicio de su negocio", la licencia fue bloqueada por Abastalia (por ejemplo por falta de pago o por un acuerdo pendiente). Pagar en línea no siempre quita ese bloqueo: escriba a Abastalia para resolverlo. Mientras tanto puede cerrar el turno, enviar lo ya emitido a la DGII, exportar sus reportes y respaldar.
