# Backlog de Toki V1 (backend primero)

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
| TK-054 | Reportes 606 y 607 | 🟡 607 listo (`/api/reportes/dgii/607`, CSV/Excel); falta 606 (contrato de Compras) y el TXT oficial (con TK-055) |
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
| TK-084 | Cuentas por cobrar y por pagar | 🟡 consultas en `/api/clientes/por-cobrar` y `/api/compras/por-pagar`; falta exportarlas desde Reportes (después de R4) |
| TK-085 | Exportar a CSV y Excel | ✅ `?formato=csv` o `xlsx` en cada reporte (CSV con BOM y sin fórmulas) |

## M9 · Plataforma

| Ticket | Qué | Estado |
|---|---|---|
| TK-088 | Procesador del outbox y subida a la nube (Sync) | ⬜ |
| TK-089 | Licencia firmada y funciones por plan (`IFeatureService`) | ⬜ |
| TK-090 | Migraciones EF por módulo para SQL Server y guardia de migraciones | ⬜ |
| TK-091 | Emparejamiento con certificados (mTLS) en lugar del secreto de dispositivo | ⬜ |
| TK-092 | Respaldo programado y cifrado | ⬜ |
| TK-093 | Servicio de Windows escuchando en la red con HTTPS (certificado del nodo), logs a archivo, health checks propios (outbox, cola e-CF, reloj). **Sin HTTPS no hay piloto** (R1 #9) | ⬜ |
| TK-094 | Verificación de la auditoría por endpoint y tarea nocturna | ✅ `GET /api/auditoria/verificar`, verificación a las 03:00 (log crítico); detecta también registros borrados al final |

## M10 · Pruebas de punta a punta

| Ticket | Qué | Estado |
|---|---|---|
| TK-095 | Suite e2e: el proceso real de la API y un día completo de tienda por HTTP (instalar, catálogo, abrir caja, vender, anular, devolver, comprar, cerrar, reportes) | ⬜ |

## Revisiones

| Revisión | Hito | Estado |
|---|---|---|
| R1 (`backlog/reviews/R1-M0-M1.md`) | M0 + M1 | ✅ Corregidos 1–8, 10–17 y 18 en parte. Pendiente: 9 (HTTPS, ver TK-093), fijar la imagen de SQL Server de CI con etiqueta fechada |
| R2 (`backlog/reviews/R2-M2.md`) | M2 | ✅ Corregidos 1–13, 15–17 (14 colación solo en SQL Server). Pruebas nuevas: conteo con ventas en medio, costo promedio, última libra en paralelo, kardex = existencia |
| R3 (`backlog/reviews/R3-M3-M4.md`) | M3 + M4 | ✅ Corregidos 1–13, 15–20. Bloqueo de la fila de venta en anular/devolver, etiqueta leída en el servidor, reembolso por el mismo medio, tope y costo por línea, devuelta en pesos enteros (`Redondeo`), e-CF simulado, código de modificación y tipo de pago, caja ligada al equipo. Pendiente: 14 (enlace QR por tipo, con TK-055); el carrito en espera de un turno cerrado se purga cuando exista el despachador del outbox; el reembolso en efectivo puede llevar centavos. Las pruebas en paralelo solo compiten de verdad en el job de SQL Server |

## Fuera de V1 (no se construye ahora)

Pantallas (WPF + Blazor), hardware (balanza, impresora, gaveta), DOP/USD, cotizaciones y conduces, combos, lotes, multisucursal, host en la nube, módulos vendibles (`funcionalidades-v1.md` §5).
