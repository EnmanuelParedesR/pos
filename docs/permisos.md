# Permisos: cómo están y cómo segmentarlos

Pedido del dueño (15 de octubre de 2026): el dueño maneja sus propios usuarios y les da permisos dentro de la plataforma (abrir caja, cerrar caja, etc.). Este documento revisa los 27 permisos de hoy y propone la segmentación. Tickets: TK-212 a TK-217.

## Lo que ya funciona

- **Roles, no permisos sueltos por persona.** Cada usuario tiene un rol; el dueño crea y edita roles en Configuración → Roles, con casillas por permiso agrupadas en cuatro bloques. Administrador no se edita y siempre tiene todo.
- **El servidor decide.** Cada endpoint pide su permiso (`RequirePermission`); la pantalla solo esconde.
- **Autorización de supervisor.** Si el cajero no tiene el permiso, un supervisor que sí lo tiene lo autoriza con su PIN. Desde TK-210 la tienda decide qué acciones lo piden.
- **Protecciones.** Solo un Administrador crea o cambia administradores; nadie se desactiva ni se cambia el rol a sí mismo.

## Problemas encontrados

| # | Problema | Ejemplo real |
|---|---|---|
| 1 | `register.operate` junta abrir turno, cerrar turno, entradas de efectivo y cobrar abonos de fiado. | No se puede tener un cajero que venda pero no abra ni cierre la caja (la abre el encargado en la mañana). |
| 2 | Cerrar el turno siempre pide `register.approve-close`, que además deja ver y cerrar turnos de otras cajas. | El cajero que hace su propio cierre ciego necesita al supervisor aunque no haya diferencia. |
| 3 | `settings.manage` junta datos del negocio, cajas, respaldos, auditoría, licencia, nube y la política de autorizaciones. | Para dejar que el encargado haga respaldos hay que darle también la licencia y la nube. |
| 4 | `purchasing.manage` junta compras, suplidores, gastos y pagos a suplidores (que sacan dinero). | El que recibe mercancía puede también pagar facturas. |
| 5 | `reports.view` junta reportes de ventas, Turnos y cuadres (Z de otros) y reportes de la DGII. | El contador ve los 606/607 pero también los cuadres de cada cajero; o al revés. |
| 6 | **Escalada de privilegios:** quien tiene `users.manage` puede crear un rol con cualquier permiso y dárselo a un usuario nuevo. | Un supervisor con `users.manage` se fabrica un usuario con `costs.view` y `settings.manage`. |
| 7 | Solo hay tres roles sembrados (Administrador, Supervisor, Cajero). | No hay punto de partida para "Inventario" o "Contador". |
| 8 | El panel en la nube tiene una sola cuenta por negocio (el dueño). | El contador o un socio no pueden ver el panel sin la clave del dueño. |

## Propuesta

### Caja (TK-212)

| Permiso | Qué deja hacer | Reemplaza |
|---|---|---|
| `register.open` | Abrir turno con fondo inicial | parte de `register.operate` |
| `register.close` | Cerrar **su propio** turno con conteo ciego. Si hay diferencia y la política lo pide, el supervisor autoriza | parte de `register.operate` y de `register.approve-close` |
| `register.cash-in` | Entradas de efectivo | parte de `register.operate` |
| `register.withdraw` | Retiros y gastos de caja | igual |
| `register.open-drawer` | Abrir la gaveta sin venta | igual |
| `register.approve-close` | Autorizar cierres con diferencia y cerrar turnos de otras cajas | se queda, más estrecho |
| `credit.collect` | Cobrar abonos de fiado en la caja | parte de `register.operate` |

`sales.create` sigue siendo "vender". La política de autorizaciones (TK-210) gana una entrada "Cerrar turno con diferencia" en lugar de "Cerrar turno".

### Administración (TK-213)

| Permiso | Qué deja hacer |
|---|---|
| `settings.manage` | Datos del negocio, recibo, política de autorizaciones, nube, licencia |
| `registers.manage` | Crear, renombrar cajas y asignarles equipo |
| `backups.manage` | Hacer y restaurar respaldos |
| `audit.view` | Ver la auditoría |
| `shifts.view` | Ver Turnos y cuadres (Z de todas las cajas) |
| `reports.view` | Reportes de ventas e inventario |
| `fiscal.reports` | Reportes de la DGII (606, 607, 608) sin tocar certificado ni secuencias |
| `fiscal.manage` | Certificado, secuencias, anulaciones, padrón |

### Compras (TK-214)

| Permiso | Qué deja hacer |
|---|---|
| `purchasing.manage` | Suplidores, compras, pedido sugerido, recibir mercancía |
| `payables.pay` | Pagar a suplidores y registrar gastos con NCF |

### Reglas (TK-215)

- Nadie da un permiso que no tiene: al guardar un rol o asignarlo, el servidor rechaza cualquier permiso que el usuario que guarda no tenga (Administrador tiene todos).
- Un rol en uso no se borra; siempre queda al menos un Administrador activo.
- Cada cambio de rol o de usuario queda en la auditoría con el antes y el después.

### Cajeros de la licencia (TK-277)

- **Quién cuenta como cajero:** un usuario **activo** cuyo rol tiene `sales.create` o `register.open` (puede vender o abrir turno en una caja con su PIN). El Administrador (el Dueño) **no** cuenta, ni los roles que no venden (Contador, Inventario).
- **Tope:** `limits.Cashiers` de la licencia (`docs/licencias.md`). Solo se rechaza **habilitar** a uno más: crear un usuario con un rol que vende, activar a uno inactivo, cambiarle el rol a uno que vende o agregar `sales.create`/`register.open` a un rol con usuarios activos (`Identity.CashierLimitReached`, con cómo agregar más). Entrar, vender, cuadrar y el e-CF **nunca** se frenan, aunque haya más cajeros que el tope (por ejemplo, tras pasar a un plan más chico).
- **Desactivar** (Configuración → Usuarios → **Desactivar**) libera el lugar al instante y guarda todo su historial; nadie se desactiva a sí mismo.
- Prueba: sin tope. Restringido: 2.
- **En la nube (TK-321, TK-347):** cada plan tiene sus topes (`Lite:Plans:<Plan>:Cashiers` y `:SellingDevices`); el Ligero, el dueño y **1** cajero y un solo dispositivo de venta a la vez, que el Dueño del panel une con un código y revoca en Tiendas (ADR-0013). Un miembro de Solo lectura lo ve, pero no une ni revoca.
- **PIN compartido:** si el mismo usuario trabaja en dos cajas a la vez, el Resumen lo avisa y Salud queda "Degradado" (`shared-pin`); no bloquea. Cada persona debe tener su propio usuario y PIN.

### Roles de partida (TK-216)

Plantillas que el dueño puede usar tal cual o copiar y ajustar:

| Rol | Permisos |
|---|---|
| Cajero | vender, cerrar su turno, entradas de efectivo, cobrar abonos, agregar clientes en la caja (`customers.quick-add`) |
| Cajero con apertura | lo de Cajero + abrir turno |
| Supervisor | todo lo de caja y ventas, autorizar cierres, clientes y fiado (`credit.sell`), ver inventario, compras sin pagar, Turnos y cuadres, reportes |
| Inventario | productos, existencias, ajustes, conteos, transferencias, compras (sin pagar) |
| Contador | reportes, reportes de la DGII, costos, Turnos y cuadres; solo lectura |
| Administrador | todo (no se edita) |

### Migración sin sorpresas

Los roles que el dueño ya editó conservan exactamente lo que podían hacer: quien tenía `register.operate` recibe `register.open`, `register.close`, `register.cash-in` y `credit.collect`; quien tenía `settings.manage` recibe `registers.manage`, `backups.manage` y `audit.view`; quien tenía `reports.view` recibe `shifts.view` y `fiscal.reports`; quien tenía `purchasing.manage` recibe `payables.pay`. Se hace con SQL a mano en la migración de Identity, como en TK-210.

### Panel en la nube (TK-217)

Miembros del negocio en el panel con dos roles: **Dueño** (todo, incluye conectar cajas e invitar) y **Solo lectura** (resumen, ventas, cuadres, asistente). Invitación por correo con enlace de un solo uso. Es independiente de los usuarios de la tienda (que entran con PIN en la caja).

**Costos y ganancias en el panel (TK-201):** el Dueño siempre los ve. Un miembro de Solo lectura no, salvo que un Dueño marque "Ve costos y ganancias" en Equipo (pensado para el contador, igual que el rol Contador de la tienda tiene `costs.view`). Sin esa marca la API manda la ganancia vacía (`null`) en "Lo más vendido" y el asistente no la recibe. Hoy es el único dato de costo que llega al panel: la réplica no tiene valor de inventario ni costos de compra.
