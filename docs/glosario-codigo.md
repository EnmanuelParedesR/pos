# Glosario: términos del negocio en el código

Regla (15 de octubre de 2026): **todo el código está en inglés**. Eso incluye proyectos, namespaces, tipos, miembros, rutas de la API, campos JSON, valores de enums, códigos de error, permisos, esquemas, tablas y columnas, claves de configuración, comandos de consola, nombres de pruebas, comentarios y logs.

En español queda solo lo que ve una persona en la tienda: textos de pantalla, mensajes de error y de validación que muestra la caja, la factura impresa y los reportes para el dueño o el contador. La documentación de `docs/` y `backlog/` sigue en español.

Los nombres propios no se traducen: Toki, Escuadra, ITBIS, RNC, e-NCF (`ENcf`), e-CF (`Ecf`), DGII, 606/607, PLU.

## Módulos

| Español | Proyecto | Esquema SQL | Ruta |
|---|---|---|---|
| Identidad | `Toki.Identity` | `identity` | `/api/identity` |
| Configuración, licencia, respaldos, auditoría | `Toki.Settings` | `settings` | `/api/settings`, `/api/backups`, `/api/audit` |
| Inventario y catálogo | `Toki.Inventory` | `inventory` | `/api/inventory` |
| Caja (cajas, turnos, movimientos, cierre Z) | `Toki.CashRegister` | `cash` | `/api/cash` |
| Ventas | `Toki.Sales` | `sales` | `/api/sales` |
| Compras y suplidores | `Toki.Purchasing` | `purchasing` | `/api/purchasing` |
| Clientes y fiado | `Toki.Customers` | `customers` | `/api/customers` |
| Fiscal (e-CF) | `Toki.Fiscal` | `fiscal` | `/api/fiscal` |
| Reportes | `Toki.Reports` | — | `/api/reports` |
| Nube (conexión y subida) | `Toki.Sync` | `sync` | `/api/sync` |

## Términos

| Español | Código |
|---|---|
| venta, línea, pago | `Sale`, `SaleLine`, `Payment` |
| medio de pago: efectivo, tarjeta, transferencia, fiado, saldo a favor | `PaymentMethod`: `Cash`, `Card`, `Transfer`, `OnAccount`, `CreditBalance` |
| devuelta | `Change` |
| anular, anulación | `Void` |
| devolución | `Return` |
| nota de crédito | `CreditNote` |
| venta en espera | `HeldSale` |
| encargo (pedido), cotización; pendiente, listo, entregado, cancelado | `Order` con `OrderKind`: `Order`, `Quote`; `OrderStatus`: `Pending`, `Ready`, `Delivered`, `Cancelled` (`/api/orders`) |
| abono de un encargo (anticipo) | `Deposit`: entra a la caja como cobro a cliente y queda como saldo a favor (`CreditBalance`) hasta que la venta lo usa |
| fecha y hora de entrega, vigencia | `PickupDate`, `PickupTime`, `ValidUntil` |
| catálogo público, en catálogo | `PublicCatalog`, `CatalogItem` (`/api/catalog`; en la nube `/api/public/catalog/{storeId}`, panel `/c/{storeId}`) |
| descuento, tope de descuento | `Discount`, `DiscountCapPercent` |
| caja (física), cajero | `Register`, `Cashier` |
| turno, fondo inicial | `Shift`, `OpeningFloat` |
| entrada, retiro, gasto (de caja) | `CashMovement`: `CashIn`, `Withdrawal`, `Expense` |
| cierre, conteo ciego, denominación | `Close`, `BlindCount`, `Denomination` |
| esperado, contado, diferencia, redondeo | `Expected`, `Counted`, `Difference`, `Rounding` |
| reporte Z / X | `ZReport` / `XReport` |
| producto, categoría, presentación | `Product`, `Category`, `Presentation` |
| código de barras, etiqueta de balanza, formato de etiqueta | `Barcode`, `ScaleLabel`, `LabelFormat` |
| unidad de medida: unidad, libra | `UnitOfMeasure`: `Unit`, `Pound` |
| existencia, mínimo, máximo, almacén | `Stock`, `MinimumStock`, `MaximumStock`, `Warehouse` |
| pedido sugerido | `SuggestedOrder` |
| kardex (movimiento de inventario) | `StockMovement` |
| ajuste, conteo físico, transferencia | `Adjustment`, `StockCount`, `Transfer` |
| costo, costo promedio | `Cost`, `AverageCost` |
| precio detalle / mayorista, nivel de precio | `RetailPrice` / `WholesalePrice`, `PriceLevel` (`Retail`, `Wholesale`) |
| compra (borrador, aplicada, anulada) | `Purchase` (`Draft`, `Posted`, `Voided`) |
| suplidor, pago a suplidor, gasto con NCF | `Supplier`, `SupplierPayment`, `Expense` |
| cuenta por pagar / por cobrar, antigüedad | `Payable` / `Receivable`, `Aging` |
| cliente, límite de crédito, estado de cuenta | `Customer`, `CreditLimit`, `Statement` |
| fiado (cargo), abono | `OnAccountCharge`, `CustomerPayment` |
| deuda, saldo a favor (cuenta del cliente) | `Debt`, `CreditBalance` (`CustomerBalanceChangedV1`) |
| mezcla de medios de pago, ventas por hora | `PaymentMethodSales`, `HourlySales` |
| tema (automático, claro, oscuro) | `Theme` (`system`, `light`, `dark`; `data-theme` en `<html>`) |
| RNC o cédula | `TaxId` |
| comprobante fiscal, secuencia | `FiscalDocument`, `NcfSequence` |
| tipo de e-CF: crédito fiscal 31, consumo 32, nota de débito 33, nota de crédito 34 | `EcfType`: `FiscalCredit31`, `Consumer32`, `DebitNote33`, `CreditNote34` |
| código de seguridad, cola de envío | `SecurityCode`, `SubmissionQueue` |
| ambiente de la DGII (simulado, pre-certificación, certificación, producción) | `FiscalEnvironment`: `Simulated`, `TesteCF`, `CerteCF`, `eCF` |
| resumen de factura de consumo (RFCE), canal de envío | `Rfce`, `EcfChannel` (`Ecf`, `Rfce`) |
| enviado (con TrackId, esperando resultado), aceptado, aceptado condicional, rechazado | `SubmissionStatus`: `Sent`, `Accepted`, `AcceptedConditional`, `Rejected` |
| e-CF de reemplazo de un rechazado | `ReplaceRejected`, `ReplacesENcf` / `ReplacedByENcf` |
| anulación de e-NCF (ANECF) | `EcfVoid`, `VoidSequence` |
| e-CF anulado sin enviar (ANECF de un número firmado) | `SubmissionStatus.Voided`, `VoidDocument` |
| certificado digital (importar, vence) | `EcfCertificate`, `ImportCertificate`, `CertificateStatus` (`ExpiresSoon`) |
| cliente de los servicios de la DGII, semilla, archivo e-CF en la nube | `DgiiClient`, seed, `/fiscal/v1/archive` |
| archivo frío (Blob Cool/Archive), índice, sacar del archivo | `IColdArchive`, `ColdArchiver`, `EcfRelayRecord.ColdKey`, rehydrate (`GET /api/fiscal/archive/{eNCF}`) |
| ver costos y ganancias en el panel, Z en el panel | `TenantMember.CanViewCosts`, `ShiftReport` (`GET /api/panel/shifts/{id}`) |
| leyenda de contingencia | `ContingencyLegend` |
| padrón, contribuyente, carga del padrón | `TaxpayerRegistry`, `Taxpayer`, `TaxpayerImport` |
| usuario, rol, permiso | `User`, `Role`, `Permission` |
| equipo (dispositivo), emparejar, código | `Device`, `Pair`, `PairingCode` |
| certificado del servidor fijado, huella | `NodeCertificatePin`, `Fingerprint` |
| sesión, PIN, bloqueado | `Session`, `Pin`, `Locked` |
| autorización de supervisor | `SupervisorApproval` |
| qué acciones piden supervisor en esta tienda | `ApprovalPolicy` (`/api/settings/approvals`) |
| instalación | `Setup` |
| datos del negocio, pie del recibo | `BusinessProfile`, `ReceiptFooter` |
| licencia, funciones | `License`, `Features` |
| suscripción, pagado hasta, pago pendiente, acuerdo manual (TK-249) | `TenantSubscription`, `PaidThroughUtc`, `PastDue`, `ManualUntilUtc` |
| respaldo, frase | `Backup`, `Passphrase` |
| auditoría | `Audit` |

## Permisos

| Antes | Ahora |
|---|---|
| `usuarios.gestionar` | `users.manage` |
| `dispositivos.gestionar` | `devices.manage` |
| `configuracion.gestionar` (negocio, recibo, autorizaciones, nube, licencia) | `settings.manage` |
| crear y renombrar cajas, asignarles equipo (M16) | `registers.manage` |
| hacer y restaurar respaldos (M16) | `backups.manage` |
| ver la auditoría (M16) | `audit.view` |
| `productos.gestionar` | `products.manage` |
| `precios.cambiar` | `prices.change` |
| `inventario.ver` / `ajustar` / `contar` / `transferir` | `inventory.view` / `adjust` / `count` / `transfer` |
| abrir turno / cerrar su turno / entradas de efectivo (M16, antes `register.operate`) | `register.open` / `close` / `cash-in` |
| `caja.retirar` / `aprobar-cierre` (cierres con diferencia y turnos de otras cajas) / `abrir-gaveta` | `register.withdraw` / `approve-close` / `open-drawer` |
| `ventas.registrar` / `anular` / `devolver` | `sales.create` / `void` / `return` |
| `ventas.descuento-mayor` / `cambiar-precio` | `sales.large-discount` / `price-override` |
| `ventas.quitar-linea` (quitar líneas o anular la venta en curso) | `sales.remove-line` |
| `compras.gestionar` (suplidores, compras, pedido sugerido) | `purchasing.manage` |
| pagar a suplidores y gastos con NCF (M16) | `payables.pay` |
| `clientes.gestionar` | `customers.manage` |
| cobrar abonos de fiado en la caja (M16) | `credit.collect` |
| `fiado.gestionar` / `exceder-limite` | `credit.manage` / `exceed-limit` |
| `fiscal.gestionar` (certificado, secuencias, anulaciones, padrón) | `fiscal.manage` |
| reportes de la DGII 606/607/608 (M16) | `fiscal.reports` |
| `reportes.ver` (ventas e inventario) | `reports.view` |
| Turnos y cuadres de todas las cajas (M16) | `shifts.view` |
| ver costos y ganancias | `costs.view` |

Roles de partida (M16): Administrador, Supervisor, Cajero, Cajero con apertura, Inventario (`StockKeeperId`), Contador (`AccountantId`).

## Otros

- Encabezado del equipo: `X-Device` (antes `X-Dispositivo`).
- Códigos de error: `Modulo.Motivo` en inglés, por ejemplo `Sales.TotalChanged` (antes `Ventas.TotalCambio`) y `Approval.Required` (antes `Autorizacion.Requerida`).
- Comandos de consola: `prepare-service` y `decrypt-backup` (tienda); `grant-platform-admin` y `revoke-platform-admin` (nube).
- Equipo de Toki en la nube (TK-189): superadministrador = `PlatformAdmin` (claim `platform_role`), consola = `/api/platform`, cliente = `Tenant`, desactivar = `disable`, revocar caja = `revoke`. Latido del nodo (TK-190) = `NodeHeartbeatV1`.
- Miembros del panel en la nube (TK-217): miembro = `TenantMember`, rol = `MemberRole` (Dueño = `Owner`, Solo lectura = `ReadOnly`), invitación = `TenantInvite` (enlace de un solo uso, `/invite#{token}` en el panel), equipo = `/api/members`, cambiar de negocio = `POST /api/me/tenant`, política de escritura = `PanelOwner`. Correo saliente = `IEmailSender`.
- Datos que ve el dueño, como los nombres de los roles sembrados (Administrador, Supervisor, Cajero), siguen en español: son contenido, no código. En el código, las constantes que los identifican van en inglés (`RoleIds.Administrator`).
