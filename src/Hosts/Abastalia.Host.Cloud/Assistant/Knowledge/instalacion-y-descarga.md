---
id: instalacion-y-descarga
title: Descargar e instalar Abastalia
audience: public
links: [/descargar, /precios, /precios#contacto, /novedades]
sources: [src/Cloud/panel/src/pages/site/SitePages.tsx, src/Cloud/panel/src/pages/site/config.ts, docs/instalador-web.md, docs/v1-alcance.md, src/Client/Abastalia.Client.UI/Help/HelpContent.cs]
---

## ¿Dónde descargo Abastalia? ¿Cómo lo instalo?
Abastalia se descarga en la página /descargar: es un solo instalador para Windows para todas las PC de la tienda. Si el enlace de descarga todavía no aparece en esa página, escríbanos en /precios#contacto y se lo enviamos. Pasos: abra el instalador en la PC principal (Windows pide permiso una vez), elija "PC principal" y complete "Primera instalación" con los datos de su negocio y su usuario. En cada caja adicional use el mismo instalador, elija "Caja" y únala con el código de Configuración → Equipos. La instalación toma unos 10 minutos y no pide código ni tarjeta.

## ¿Qué es la PC principal y qué es una caja?
En Abastalia, la PC principal guarda las ventas y atiende a las demás cajas: tiene el servidor, la base de datos (SQL Server Express, que el instalador agrega si falta) y también la caja. Se instala primero. Una caja es cada PC de cobro adicional: solo lleva la app de caja y se une a la PC principal con un código de un solo uso que vale 10 minutos. En una tienda de una sola caja, la misma PC es la caja y la principal.

## ¿Quién instala Abastalia? ¿Cuánto cuesta la instalación?
Usted puede instalar Abastalia por su cuenta, gratis, siguiendo los pasos de /descargar. También es gratis con ayuda remota. Si prefiere que un técnico vaya a la tienda, la visita cuesta US$45 (≈ RD$2,770). Para pedir ayuda con la instalación, escríbanos en /precios#contacto.

## ¿Cómo se actualiza Abastalia?
Las actualizaciones de Abastalia se instalan solas y vienen firmadas: la caja se actualiza al abrirse y la PC principal cuando no hay turnos abiertos o fuera del horario de la tienda, para no interrumpir una venta. No hay que reinstalar ni se pierden datos. Lo nuevo de cada versión se publica en /novedades.

## ¿Puedo instalar Abastalia en varias computadoras?
Sí. El mismo instalador de Abastalia sirve para la PC principal y para cada caja de la tienda, todas en la misma red. Cuántas cajas puede usar además de la PC principal depende del plan: Esencial solo la PC principal, Estándar 1 caja, Pro 3 cajas, y más con el adicional "Caja adicional". Vea /precios.
