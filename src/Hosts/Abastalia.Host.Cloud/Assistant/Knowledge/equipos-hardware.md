---
id: equipos-hardware
title: Equipos compatibles: PC, balanza, impresora, gaveta y escáner
audience: public
links: [/descargar, /precios, /carniceria]
sources: [src/Cloud/panel/src/pages/site/SitePages.tsx, docs/funcionalidades-v1.md, backlog/BACKLOG.md, src/Client/Abastalia.Client.UI/Help/HelpContent.cs]
---

## ¿Qué computadora necesito para Abastalia? Requisitos de la PC
Abastalia funciona en Windows 10 u 11 de 64 bits. La PC principal necesita 8 GB de memoria RAM y 10 GB libres en el disco; cada PC de caja adicional, 4 GB de RAM y 2 GB libres. Hace falta internet al instalar (se descargan de Microsoft SQL Server Express y WebView2 si faltan) y todas las PC deben estar en la misma red de la tienda. Abastalia no funciona hoy en Mac, en tablet ni en celular como caja; el celular se usa para el panel del dueño. Se recomienda activar el cifrado del disco (BitLocker o Cifrado de dispositivo) en la PC principal.

## ¿Abastalia funciona con mi balanza?
Abastalia se conecta por puerto COM (o USB) con balanzas compatibles con CAS, Mettler Toledo y A&D, y muestra el peso en vivo en la caja ("Esperando peso estable"). También lee las etiquetas EAN-13 con peso o precio de las balanzas etiquetadoras. Si no hay balanza conectada, el peso se escribe a mano. Antes de instalar, Abastalia revisa su modelo de balanza: escríbanos con la marca y el modelo en /precios#contacto. En cada caja se prueba en Configuración → Equipos de la caja.

## ¿Qué impresora necesito? Impresora térmica de facturas
Abastalia imprime la factura en impresoras térmicas de tickets de 58 mm u 80 mm (comandos ESC/POS), conectadas a Windows o por puerto COM, con el QR del e-CF y el corte del papel. También puede imprimir en hoja carta o A4, o guardar como PDF, el cierre Z, el estado de cuenta de los clientes a crédito, las cotizaciones y la representación impresa. La impresora se prueba en Configuración → Equipos de la caja.

## ¿Abastalia abre la gaveta de dinero? ¿Funciona con escáner?
Sí. Abastalia abre la gaveta (cajón de dinero) conectada a la impresora térmica al cobrar. Abrirla sin venta es un permiso aparte y queda registrado. Los escáneres de código de barras que trabajan como teclado (lo normal en los lectores USB) funcionan sin configurar: el campo de escaneo de la caja siempre está listo. Abastalia lee códigos de barras de productos, códigos propios (PLU) y etiquetas de balanza.

## ¿Abastalia vende los equipos?
Abastalia puede cotizarle un kit de equipos según su negocio (PC, balanza, impresoras, escáner y gaveta); el precio se cotiza aparte. Si ya tiene equipos, revisamos si son compatibles antes de instalar. Escríbanos en /precios#contacto con lo que tiene: la marca y el modelo de su balanza, su impresora y su computadora.
