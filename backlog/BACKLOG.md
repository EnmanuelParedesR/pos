# Backlog de Toki V1

Fuente del alcance: `docs/funcionalidades-v1.md` (sección 2) y `docs/arquitectura-v1-vsa.md` (sección 14).
Estados: ✅ hecho · 🔨 en curso · ⬜ pendiente · ⏸ en espera (bloqueado por algo externo).

Cada ticket se cierra solo con: endpoint, validación, handler, prueba de integración del camino principal, prueba unitaria si hay lógica de negocio, y entrada de auditoría si cambia dinero, inventario o permisos.

Al terminar cada hito, un desarrollador senior (agente de revisión) revisa el código y las pruebas (unitarias, integración y de punta a punta). Sus hallazgos entran aquí como tickets `RV-…`.

## M0 · Esqueleto

| Ticket | Qué | Estado |
|---|---|---|
| TK-001 | Solución .NET 10, Central Package Management, analizadores como error, `nuget.config` | ✅ |
| TK-002 | Bloques base: `Result`/`Error`, comandos y consultas propias (sin MediatR), decoradores de validación, transacción y autorización | ✅ |
| TK-003 | Una conexión y una transacción por petición compartida por los `DbContext` de todos los módulos | ✅ |
| TK-004 | Outbox escrito en la misma transacción que el cambio | ✅ |
| TK-005 | Auditoría encadenada por hash (SHA-256) y verificador | ✅ |
| TK-006 | Pruebas: unitarias, integración (API real en memoria sobre SQLite o SQL Server en contenedor) y arquitectura (ArchUnitNET, sin SP/triggers, sin paquetes comerciales) | ✅ |
| TK-007 | CI en GitHub Actions: compilar y probar sobre SQLite y sobre SQL Server 2025 en contenedor | ✅ |
| TK-008 | README, ADRs y este backlog | ✅ |

## M1 · Identidad

| Ticket | Qué | Estado |
|---|---|---|
| TK-010 | Instalación: primer administrador y PC principal (solo si no hay usuarios) | ✅ |
| TK-011 | Entrar con código y PIN: hash PBKDF2, bloqueo progresivo (5 fallos → 5 min, luego el doble, máx. 1 h), sesión de 12 h ligada al dispositivo | ✅ |
| TK-012 | Usuarios (crear, editar, desactivar, cambiar PIN), roles editables con permisos por acción; el rol Administrador no se edita | ✅ |
| TK-013 | Emparejar cajas con código de un solo uso (10 min) y revocarlas (cierra sus sesiones) | ✅ |
| TK-014 | Autorización de supervisor de un solo uso (2 min, ligada a acción, recurso y dispositivo) | ✅ |

## M2 · Configuración e inventario (catálogo)

| Ticket | Qué | Estado |
|---|---|---|
| TK-020 | Datos del negocio: nombre, RNC, dirección, teléfono, pie del recibo, tope de descuento sin supervisor | ✅ |
| TK-021 | Categorías con color y notas predefinidas ("molida", "sin grasa") | ✅ |
| TK-022 | Productos: por unidad o por peso (lb), códigos de barras, ITBIS 18/16/0/exento, precio, costo, vender sin existencia | ✅ |
| TK-023 | Presentaciones (libra, paquete, caja) con factor, precio y código propios | ✅ |
| TK-024 | Niveles de precio (detalle, mayorista) | ✅ |
| TK-025 | Buscar por código, código de barras o nombre; leer etiquetas de balanza EAN-13 (prefijos 20–29, peso o precio, dígito verificador) | ✅ |
| TK-026 | Almacenes (tienda, cuarto frío) | ✅ |

## M3 · Caja

| Ticket | Qué | Estado |
|---|---|---|
| TK-030 | Cajas físicas | ✅ |
| TK-031 | Abrir turno: caja + cajero + fondo inicial (un turno abierto por caja) | ✅ |
| TK-032 | Entradas, retiros (supervisor) y gastos menores con motivo; no se editan | ✅ |
| TK-033 | Cierre ciego con conteo por denominaciones, esperado, diferencia, aprobación de supervisor y reporte Z | ✅ |

## M4 · Ventas

| Ticket | Qué | Estado |
|---|---|---|
| TK-040 | Motor de totales: ITBIS 18/16/0/exento con precios con ITBIS incluido, descuentos por línea y globales (% o monto), redondeo único | ✅ |
| TK-041 | Registrar venta en una sola transacción (venta, inventario, caja, e-CF, outbox, auditoría); idempotente por `VentaId` | ✅ |
| TK-042 | Pagos: efectivo, tarjeta, transferencia y fiado; mixto; solo el efectivo da devuelta | ✅ (fiado y saldo a favor llegan con TK-071) |
| TK-043 | Ventas en espera (varias por caja) | ✅ |
| TK-044 | Modelo de comprobante para imprimir y reimpresión | ✅ |
| TK-045 | Anular venta dentro del turno con supervisor y motivo (repone inventario, saca el dinero del turno) | ✅ |
| TK-046 | Devolución parcial contra la factura: por línea, repone inventario, devuelve efectivo o deja saldo a favor, emite nota de crédito | ✅ |
| TK-047 | Notas por línea (predefinidas por categoría o libres) | ✅ |

## M5 · Fiscal (e-CF simulado, ver `pos-ecf-deferred`)

| Ticket | Qué | Estado |
|---|---|---|
| TK-050 | Secuencias e-NCF por tipo: cargar rango, asignar sin huecos dentro de la venta, alertas al 80 % y por vencimiento | ✅ |
| TK-051 | Documento e-CF, firmador y proveedor simulados (`IEcfProvider`), cola de contingencia de 72 h con trabajador en segundo plano | ✅ `IEcfProveedor` simulado, `EnvioEcfWorker` (apagado por defecto: `Fiscal:EnvioAutomatico`), reintentos 1→60 min, `GET /api/fiscal/cola` |
| TK-052 | Nota de crédito e-CF 34 | ✅ |
| TK-053 | Consulta de razón social por RNC (padrón importado) | ✅ `POST /api/fiscal/padron` (DGII_RNC.TXT, reemplazo en una transacción), `GET /api/fiscal/contribuyentes/{rnc}` |
| TK-054 | Reportes 606 y 607 | ✅ `/api/reportes/dgii/607` y `/dgii/606` en JSON, CSV o Excel. El TXT oficial de la DGII queda para TK-055 |
| TK-055 | Proveedor e-CF real y certificación DGII | ⏸ decisión del dueño |

## M6 · Movimientos de inventario y compras

| Ticket | Qué | Estado |
|---|---|---|
| TK-060 | Kardex (movimientos que no se borran), existencia actual y costo promedio ponderado | ✅ |
| TK-061 | Ajustes con motivo (merma, daño, consumo, corrección) | ✅ |
| TK-062 | Conteo físico ciego que genera ajustes | ✅ |
| TK-063 | Transferencias entre almacenes | ✅ |
| TK-064 | Suplidores (RNC) | ✅ |
| TK-065 | Compras: borrador → aplicada → anulada; a crédito genera cuenta por pagar | ✅ |
| TK-066 | Gasto con NCF sin productos | ✅ |
| TK-067 | Pagos a suplidores (a las facturas más viejas) y estado de cuenta | ✅ |

## M7 · Clientes y fiado

| Ticket | Qué | Estado |
|---|---|---|
| TK-070 | Clientes: nombre, RNC/cédula, teléfono, tipo de comprobante, nivel de precio, límite; alta rápida desde la caja | ✅ |
| TK-071 | Fiado: cargos por venta, abonos a las facturas más viejas, límite (supervisor para excederlo), antigüedad 30/60/90 y estado de cuenta | ✅ |

## M8 · Reportes

| Ticket | Qué | Estado |
|---|---|---|
| TK-080 | Ventas por día, categoría, producto y cajero | ✅ |
| TK-081 | Utilidad con el costo histórico de cada venta (sin anuladas) | ✅ |
| TK-082 | Resumen de turnos y cuadres | ✅ |
| TK-083 | Valor de inventario | ✅ |
| TK-084 | Cuentas por cobrar y por pagar | ✅ `/api/reportes/por-cobrar` y `/por-pagar` (exportables), con la misma lógica que las pantallas de Clientes y Compras |
| TK-085 | Exportar a CSV y Excel | ✅ `?formato=csv` o `xlsx` en cada reporte (CSV con BOM y sin fórmulas) |

## M9 · Plataforma

| Ticket | Qué | Estado |
|---|---|---|
| TK-088 | Procesador del outbox y subida a la nube (Sync) | 🟡 Procesador en proceso (en orden, al menos una vez, `IManejadorEvento<T>`), health `outbox`; primer consumidor: borra las ventas en espera del turno cerrado. ⏸ La subida a la nube espera el backend en la nube (fase 2) |
| TK-089 | Licencia firmada y funciones por plan (`IFeatureService`) | ✅ ADR-0006: JWS ES256 verificado con la clave pública embebida, estados Prueba/Activa/Gracia (15 días)/Restringida, límite de cajas al emparejar, `GET /api/configuracion/funciones`, regla de arquitectura: Ventas, Fiscal y Caja no dependen de la licencia |
| TK-090 | Migraciones EF por módulo para SQL Server y guardia de migraciones | ✅ ADR-0005: `Persistence/Migraciones` por módulo, `MigrateAsync` al arrancar en SQL Server, prueba que falla si falta una migración |
| TK-091 | Emparejamiento con certificados (mTLS) en lugar del secreto de dispositivo | ⏸ Se diseña junto con la app de la caja (el CSR y el almacén de llaves viven en el cliente). Para el piloto: HTTPS con certificado del nodo fijado al emparejar + secreto de dispositivo |
| TK-092 | Respaldo programado y cifrado | ✅ Nocturno (23:30) y a pedido; AES-256-GCM con la frase del dueño (PBKDF2), se conservan 14, copia opcional fuera de la PC, health `respaldo`, `descifrar-respaldo` por línea de comandos |
| TK-093 | Servicio de Windows escuchando en la red con HTTPS (certificado del nodo), logs a archivo, health checks propios (outbox, cola e-CF, reloj). **Sin HTTPS no hay piloto** (R1 #9) | 🟡 HTTPS en 5443 con certificado propio del nodo (almacén de Windows; los terminales lo fijan al emparejar), logs diarios en ProgramData, health checks `reloj`, `cola-ecf` y `auditoria`. Health `outbox` listo (TK-088). Instalador `deploy/instalar.ps1` + `preparar-servicio` (cuenta virtual sin admin, permiso sobre la llave del certificado, login de SQL, firewall solo en red privada). Falta probarlo una vez en una VM Windows limpia |
| TK-094 | Verificación de la auditoría por endpoint y tarea nocturna | ✅ `GET /api/auditoria/verificar`, verificación a las 03:00 (log crítico); detecta también registros borrados al final |

## M10 · Pruebas de punta a punta

| Ticket | Qué | Estado |
|---|---|---|
| TK-095 | Suite e2e: el proceso real de la API y un día completo de tienda por HTTP (instalar, catálogo, abrir caja, vender, anular, devolver, comprar, cerrar, reportes) | ✅ `tests/Toki.E2ETests`: proceso real (SQLite o SQL Server en CI), 3 cajas en paralelo, reinicio a mitad del día, fiado, compras, cierres ciegos; al final cuadran cajas, kardex, e-NCF, documentos, clientes, suplidores y auditoría, sin ningún 500 |

## M11 · Frontend (caja y administración)

Diseño: canvas "Toki" (pantallas) y `docs/critica-diseno.md`. Decisión: ADR-0007.

| Ticket | Qué | Estado |
|---|---|---|
| TK-100 | Escuadra (RCL): tokens, botones, teclas visibles, avisos en línea, diálogo (único con sombra, foco al primer campo), teclado numérico, PIN, dinero, íconos, logo C5 | ✅ |
| TK-101 | Cliente de la API tipado: dispositivo + sesión en cada pedido, ProblemDetails con `code`, sesión vencida → PIN, sin servidor → aviso ámbar | ✅ |
| TK-102 | Configurar este equipo: primera instalación o unir con el código de 8 dígitos | ✅ |
| TK-103 | Entrar con PIN (teclado en pantalla y físico), dueño/supervisor a administración, cajero a la caja | ✅ |
| TK-104 | Abrir caja con el fondo contado por denominación | ✅ |
| TK-105 | Venta: escáner (código, PLU, etiquetas de balanza), mosaicos por categoría, carrito, cantidad/peso (F3), precio (F4), descuento (F7), quitar, anular, teclas F | ✅ |
| TK-106 | Cobro: efectivo en pesos enteros con montos rápidos y devuelta, tarjeta, transferencia, mixto, fiado, saldo a favor; total confirmado por el servidor (`Ventas.TotalCambio`), idempotente por `VentaId` | ✅ |
| TK-107 | Diálogo único "Autoriza un supervisor": la pantalla manda el comando, si la API pide un permiso se autoriza y se reintenta | ✅ |
| TK-108 | Cliente y comprobante (E32/E31), RNC escrito para una sola factura, alta rápida | ✅ |
| TK-109 | Ventas en espera (F5): guardar, retomar, descartar | ✅ |
| TK-110 | Caja (F9): entradas y salidas de efectivo, cambiar de usuario, ir a administración | ✅ |
| TK-111 | Ventas del turno: ver, reimprimir, anular y devolver líneas con supervisor | ✅ |
| TK-112 | Cerrar caja ciego por denominación, cierre con supervisor y resultado (cuadró/sobró/faltó, por medio) | ✅ |
| TK-113 | Administración: Resumen (hoy, 7 días, más vendidos, cajas, alertas de comprobantes), Productos y categorías, Turnos y Z, Configuración (negocio, cajas, comprobantes, equipos, usuarios, respaldos, licencia) | ✅ |
| TK-114 | App de la tienda: WPF + BlazorWebView, token del equipo cifrado con DPAPI | 🟡 Compila; falta abrirla una vez en una PC de caja (y en la VM limpia) |
| TK-115 | Host web de desarrollo (`Toki.Client.Web`) para ver y probar las pantallas en el navegador | ✅ |
| TK-116 | Pruebas bUnit: carrito, cobro con total corregido, supervisor, PIN equivocado, F7 | ✅ |
| TK-117 | Balanza (`IScale`) por marca; hoy el peso se escribe | ⬜ |
| TK-118 | Impresora térmica ESC/POS y gaveta; hoy la factura es una vista previa | ⬜ |
| TK-119 | Incluir IBM Plex Sans (OFL) en `wwwroot/fuentes`; hoy usa la instalada o Segoe UI | ⬜ |
| TK-120 | Administración que falta: inventario (ajustes, conteo, transferencias), compras, suplidores y gastos, clientes y abonos, facturas, reportes descargables, roles | ⬜ |
| TK-121 | Abono de un cliente desde la caja | ⬜ |
| TK-122 | Fijar el certificado del nodo en el cliente al emparejar (con TK-091) | ⬜ |
| TK-123 | Pruebas de punta a punta de la interfaz (Playwright) contra la API real | ⬜ |

## Revisiones

| Revisión | Hito | Estado |
|---|---|---|
| R1 (`backlog/reviews/R1-M0-M1.md`) | M0 + M1 | ✅ Corregidos 1–8, 10–17 y 18 en parte. Pendiente: 9 (HTTPS, ver TK-093), fijar la imagen de SQL Server de CI con etiqueta fechada |
| R2 (`backlog/reviews/R2-M2.md`) | M2 | ✅ Corregidos 1–13, 15–17 (14 colación solo en SQL Server). Pruebas nuevas: conteo con ventas en medio, costo promedio, última libra en paralelo, kardex = existencia |
| R6 (`backlog/reviews/R6-M9.md`) | M9 (migraciones, outbox, respaldos) + R5 | ✅ Corregidos 1–15. Respaldo también al arrancar si el último tiene más de 20 h; SQL Server escribe el `.bak` en `respaldos\trabajo` (el instalador le da permiso), que se limpia en cada corrida; archivo `.parcial` hasta estar completo; 1 h de tiempo límite; error claro si la clave guardada no se puede leer (anillo de Data Protection en `C:\ProgramData\Toki\claves`); la copia fuera de la PC tiene su propia retención y no frena nada; formato de respaldo v2 (clave por archivo con HKDF, encabezado autenticado); el outbox lee cada mensaje una vez por corrida y usa un scope por mensaje; semillas literales y prueba que impide reescribir datos del dueño; migraciones en `StartingAsync` con 30 min; el 606 usa el medio de pago registrado; el e2e cuadra también el ITBIS del 607. Pendiente: verificar 2, 5 y 10 en la VM Windows limpia con el instalador |
| R5 (`backlog/reviews/R5-M8-M9.md`) | M8 + M9 + e2e | ✅ Corregidos 1–9, 11–16 y 10 en parte. 607 y 606 en el orden de columnas de la DGII con montos sin ITBIS; los reportes restan las devoluciones el día en que ocurren y usan nombres sin ambigüedad (`VentasConItbis`, `NetoSinItbis`, `VendidoConItbis`); un timeout del proveedor e-CF ya no detiene el servicio; la auditoría se verifica al arrancar y a las 03:00; el certificado se lee sin permisos de escritura; horas locales en CSV/Excel; Excel limitado a 50 000 filas; el Z imprime la cabeza de la auditoría; la suite e2e cuadra reportes con los Z, el 607 y el inventario. Pendiente: (10) importar el padrón en segundo plano o con `SqlBulkCopy` si con el archivo real tarda más de un minuto; (3) que el contador confirme un mes de 606/607; (6) probar en una VM limpia qué cuenta usa el servicio y el permiso sobre la llave del certificado (con el instalador, TK-093) |
| R4 (`backlog/reviews/R4-M6-M7.md`) | M6 + M7 | ✅ Corregidos 1–13, 15–19. Bloqueo de la fila de la compra, el costo promedio vuelve atrás al anular una compra, abonos con id de la caja (idempotentes), NCF únicos (índices filtrados) e ids del cliente en compras, gastos y pagos, anulación de gastos, pagos a suplidores y abonos, estado de cuenta que cuadra con anulaciones, abonos en efectivo en pesos enteros con redondeo, devoluciones y anulaciones pagan primero las deudas viejas, pago a suplidor desde la caja, ITBIS/total de la factura, saldos abiertos en una sola consulta, abonos por medio en el Z. Pendiente: las pruebas en paralelo solo compiten de verdad en el job de SQL Server; pasos de compras y fiado en la suite e2e (TK-095) |
| R3 (`backlog/reviews/R3-M3-M4.md`) | M3 + M4 | ✅ Corregidos 1–13, 15–20. Bloqueo de la fila de venta en anular/devolver, etiqueta leída en el servidor, reembolso por el mismo medio, tope y costo por línea, devuelta en pesos enteros (`Redondeo`), e-CF simulado, código de modificación y tipo de pago, caja ligada al equipo. Pendiente: 14 (enlace QR por tipo, con TK-055); el carrito en espera de un turno cerrado se purga cuando exista el despachador del outbox; el reembolso en efectivo puede llevar centavos. Las pruebas en paralelo solo compiten de verdad en el job de SQL Server |

## Fuera de V1 (no se construye ahora)

DOP/USD, cotizaciones y conduces, combos, lotes, multisucursal, host en la nube, módulos vendibles (`funcionalidades-v1.md` §5).
