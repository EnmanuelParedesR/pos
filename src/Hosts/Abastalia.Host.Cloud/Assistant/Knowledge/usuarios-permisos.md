---
id: usuarios-permisos
title: Usuarios, roles, PIN y permisos
audience: public
links: [/precios, /novedades]
sources: [docs/permisos.md, backlog/BACKLOG.md, src/Client/Abastalia.Client.UI/Help/HelpContent.cs, src/Cloud/panel/src/pages/MembersPage.tsx, src/Cloud/panel/src/pages/InvitePage.tsx]
---

## ¿Cada cajero tiene su usuario? ¿Cómo entran a la caja?
Sí. En Abastalia cada persona entra a la caja con su código y su PIN; nadie debe compartir el PIN. El dueño crea los usuarios en Configuración → Usuarios, con su rol y su PIN, y la persona puede cambiar su PIN después. Si alguien olvida el PIN, el dueño se lo cambia. Tras 5 intentos fallidos, el PIN se bloquea unos minutos. Si el mismo usuario trabaja en dos cajas a la vez, Abastalia lo avisa en el Resumen.

## ¿Puedo decidir qué puede hacer cada empleado? Roles y permisos
Sí. En Abastalia el dueño maneja los permisos con roles, en Configuración → Roles, con casillas por acción. Abrir caja, cerrar caja, cobrar abonos de clientes a crédito, ver costos, pagar a suplidores, hacer respaldos o ver la auditoría son permisos separados. Hay roles de partida: Cajero, Cajero con apertura, Supervisor, Inventario, Contador y Administrador. Nadie puede dar un permiso que no tiene, siempre queda al menos un Administrador, y cada cambio de rol o de usuario queda en la auditoría.

## ¿El cajero puede ver mis costos y ganancias?
No, salvo que usted le dé el permiso "Ver costos y ganancias". Sin ese permiso, Abastalia no muestra el costo, la utilidad ni el valor del inventario en la caja, en la administración, en las exportaciones ni en el panel. En el panel, un miembro de "Solo lectura" (por ejemplo su contador) tampoco ve costos, salvo que el dueño marque "Ve costos y ganancias" en Equipo.

## ¿Abastalia guarda quién hizo qué? Auditoría
Sí. Abastalia registra en la auditoría quién hizo qué y cuándo: anulaciones, devoluciones, descuentos, retiros, aperturas de gaveta, autorizaciones de supervisor, cambios de precio y de permisos. La auditoría está encadenada: si alguien cambia un registro en la base de datos, la cadena lo muestra. Se consulta en Administración → Auditoría, con su permiso.

## ¿Mi contador o mi socio pueden ver el panel?
Sí. En el panel de Abastalia, el dueño invita a otras personas por correo desde Equipo, como Dueño (ve todo y administra el negocio) o Solo lectura (ve ventas, cuadres y comprobantes, pero no cambia nada). El enlace de invitación sirve una sola vez y vence en 7 días. Cuántas invitaciones puede enviar al mes depende del plan (2 en Esencial, 3 en Estándar, 5 en Pro), y se amplían con el adicional "Usuarios del panel +5".
