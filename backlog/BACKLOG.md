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
| TK-055 | Proveedor e-CF real y certificación DGII | ↪ Reemplazado por M13 (camino propio con la DGII, sin proveedor) |

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
| TK-088 | Procesador del outbox y subida a la nube (Sync) | ✅ Procesador en proceso (en orden, al menos una vez, `IManejadorEvento<T>`), health `outbox`; primer consumidor: borra las ventas en espera del turno cerrado. La subida a la nube es TK-130 |
| TK-089 | Licencia firmada y funciones por plan (`IFeatureService`) | ✅ ADR-0006: JWS ES256 verificado con la clave pública embebida, estados Prueba/Activa/Gracia (15 días)/Restringida, límite de cajas al emparejar, `GET /api/configuracion/funciones`, regla de arquitectura: Ventas, Fiscal y Caja no dependen de la licencia |
| TK-090 | Migraciones EF por módulo para SQL Server y guardia de migraciones | ✅ ADR-0005: `Persistence/Migraciones` por módulo, `MigrateAsync` al arrancar en SQL Server, prueba que falla si falta una migración |
| TK-091 | Emparejamiento con certificados (mTLS) en lugar del secreto de dispositivo | 🟡 Paso para el piloto hecho: HTTPS con el certificado del nodo fijado al emparejar (TK-122, ADR-0009) + secreto de dispositivo. Falta el mTLS con el plan de ADR-0009: CA de la tienda, CSR de la caja al emparejar, Kestrel `RequireCertificate`, la caja fija la CA (renovar sin re-emparejar) y se retira `X-Device` |
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
| TK-114 | App de la tienda: WPF + BlazorWebView, token del equipo cifrado con DPAPI (escritura atómica), WebView2 cerrado (sin recargar, zoom, deslizar ni menú), errores no manejados al registro | 🟡 Compila; falta abrirla una vez en una PC de caja (y en la VM limpia) y comprobar a mano que Ctrl+R y deslizar no hacen nada (R7 #11) |
| TK-115 | Host web de desarrollo (`Toki.Client.Web`) para ver y probar las pantallas en el navegador | ✅ |
| TK-116 | Pruebas bUnit: carrito, cobro con total corregido, supervisor, PIN equivocado, F7; y de R7: sesión vencida, cobro incierto, devolución y cierre reintentados, conteo sin salir del campo, presentación, doble confirmación, F3, pantalla que falla | ✅ |
| TK-117 | Balanza (`IScale`) por marca; hoy el peso se escribe | 🟡 Falta probar con equipo real. `Toki.Hardware.Scales`: CAS (PD-II, ER), Toledo 8217 y envío continuo por COM (fuentes en su README); peso en vivo y "Esperando peso estable" en la caja; peso escrito con motivo (va en la nota de la línea); "Leer balanza" en Equipos de la caja |
| TK-118 | Impresora térmica ESC/POS y gaveta; hoy la factura es una vista previa | 🟡 Falta probar con equipo real. `Toki.Hardware.EscPos`: factura en ESC/POS (58 y 80 mm, PC850, QR modelo 2, corte, gaveta) por la cola de Windows (RAW) o COM; imprime al cobrar si "Imprimir factura" (se recuerda en la PC) y reimprime desde la factura; "Abrir la gaveta sin venta" en Caja pide `register.open-drawer` o un supervisor (falta el endpoint auditado de §6.3); simulada en desarrollo y en el host web |
| TK-119 | Incluir IBM Plex Sans (OFL) en `wwwroot/fuentes`; hoy usa la instalada o Segoe UI | ✅ Latin-1 de `@ibm/plex-sans` 1.1.0 (400, 500, 600, 700) en `Toki.Escuadra/wwwroot/fonts`, con `OFL.txt` y su origen en `README.md`; el panel lo toma del mismo CSS |
| TK-120 | Administración que falta (dividido en TK-184..187) | ↪ Reemplazado por TK-184, TK-185, TK-186 y TK-187 |
| TK-184 | Administración: inventario (existencias, ajustes, conteo físico, transferencias, kardex por producto) | ✅ |
| TK-185 | Administración: compras, suplidores, gastos con NCF y pagos a suplidores | ✅ |
| TK-186 | Administración: clientes, fiado (estado de cuenta, abonos, anular abono) y roles y permisos | ✅ Clientes (lista con deuda y antigüedad, crear y editar respetando `credit.manage`, estado de cuenta con balance, abono a una caja abierta, anular abono con supervisor) y Configuración → Roles y permisos (Administrador no se edita). La API da `paymentId` en el estado de cuenta y `address`/`fiscalCredit` en la búsqueda |
| TK-187 | Administración: facturas (buscar, ver, reimprimir) y reportes descargables (CSV/Excel, 606/607) | ✅ Facturas (número o e-NCF, periodo, estado, detalle, vista para reimprimir) y Reportes (periodo; Excel y CSV de ventas por día/producto/categoría/cajero, turnos, inventario, por cobrar y por pagar, 606 y 607). Descarga con `escuadra.download` (blob); en la app de la tienda Windows pregunta dónde guardar |
| TK-121 | Abono de un cliente desde la caja | ✅ F9 → Abono de cliente: buscar, ver la deuda, efectivo en pesos enteros (F5/F6/F7), id idempotente, recibo de abono; sin respuesta no deja cambiar de cliente |
| TK-122 | Fijar el certificado del nodo en el cliente al emparejar (con TK-091) | ✅ ADR-0009: confianza en el primer uso al emparejar, huella de 64 bits visible en "Unir a una tienda" y junto al código del dueño; después la caja solo acepta ese certificado (`NodeCertificatePin` en el `SocketsHttpHandler`) y avisa "servidor desconocido" sin enviar nada; revocar olvida la huella. Pendiente (shortcut): si el servidor cambia de certificado hay que re-emparejar la caja (lo resuelve TK-091) |
| TK-123 | Pruebas de punta a punta de la interfaz (Playwright) contra la API real; incluye las de R7 #5 (escaneo ignorado en efectivo/PIN) y #8 (el foco nunca se pierde) | ✅ `tests/Toki.UiTests` (Playwright, job `ui` de CI): un día de caja con teclado contra la API real y un humo del panel; arregló el foco que `focusFirst` robaba (checkbox de imprimir, PIN del supervisor) |
| TK-124 | Prueba de contratos en `Toki.IntegrationTests`: llamar a los endpoints reales y deserializar cada respuesta con `TokiApi.Json` en los records de `Contracts.cs` (R7 #13b) | ✅ `ClientContractTests`: el flujo de la caja de punta a punta con los records del cliente; falla si llega un campo no anulable vacío (también en registros anidados) |
| TK-125 | Crítica de diseño (`docs/critica-diseno.md`) y canvas actualizado: logo C5, marca fija, 7 pantallas nuevas, fase 2 marcada | ✅ |
| TK-126 | Reescribir todo el código en inglés (UI en español): glosario `docs/glosario-codigo.md`, mapas `docs/rename-map*.md`, migraciones nuevas | ✅ |
| TK-127 | Pruebas contra SQL Server Express local (`TOKI_TEST_SQLSERVER`) y arreglo de `AbonosFiado.Origen` (columna corta) con prueba de guarda | ✅ |
| TK-171 | Investigación de usuarios y crítica de la administración (`docs/investigacion-admin.md`, `docs/critica-admin.md`) | ✅ |
| TK-172 | Validar con 6 dueños reales (entrevistas y prueba de usabilidad en el celular) | ⬜ Con el piloto |
| TK-173 | Rediseño de la administración y del panel ("Escuadra Admin"): resumen que cuenta una historia, nueva paleta, navegación moderna | ✅ 3 rondas con capturas; detalle en `docs/critica-admin.md` |
| TK-128 | Corregir los hallazgos de R7 | ✅ Lo que quedó pendiente está en TK-123, TK-124, TK-168 y TK-169 |

## M12 · Nube

Decisión: ADR-0008. Despliegue: `docs/despliegue-azure.md`.

| Ticket | Qué | Estado |
|---|---|---|
| TK-130 | Sync: conectar la tienda con el código del panel y subir el outbox | ✅ Módulo `Toki.Sync`: `/api/sync/connect`, `/status` y `/disconnect`; secreto del nodo cifrado con Data Protection; lotes de 200 con gzip cada 5 s, marca solo los ids aceptados, reintentos de 1 s a 15 min con jitter y nunca pierde un evento; health `sync` |
| TK-131 | Relevo e-CF en el nodo | ✅ `RelayEcfProvider` manda el e-CF firmado a `/relay/v1/ecf` como el nodo; `Fiscal:Provider` = `Simulated` (por defecto) o `Relay`; contrato en ADR-0008 |
| TK-132 | API nube (registro, tiendas, ingesta, réplica) | ✅ `Toki.Host.Cloud`: cuentas con Identity y tenant propio, tiendas, código de conexión de 8 dígitos (30 min, un solo uso), nodos con secreto (solo el hash), ingesta gzip idempotente por evento, aplicador por evento y proyectores a `replica.*` |
| TK-133 | API del panel | ✅ Resumen del día, ventas por día, más vendidos, por cajero, medios de pago, cuadres y estado de sincronización, siempre filtrado por el tenant del token. Pendiente: nombres de cajeros (los eventos solo traen el id) |
| TK-134 | Asistente (Azure OpenAI) | ✅ *Function calling* por HTTP con 6 herramientas de solo lectura (máximo 92 días); sin configuración responde 503. Pendiente: probarlo contra el modelo real y la herramienta de documentos fiscales pendientes |
| TK-135 | Relevo e-CF en la nube (simulado; DGII en espera) | ✅ `POST /relay/v1/ecf` guarda el XML firmado y responde `Simulated`; un e-NCF repetido devuelve la misma respuesta. El proveedor real es TK-055 |
| TK-136 | Bicep de Azure | ✅ Validado y desplegado en `rg-toki-prod`; SQL en región aparte (`sqlLocation`), ajustes de la Web App después del rol de Key Vault |
| TK-137 | Panel web del dueño (React) | 🟡 `src/Cloud/panel` (Vite 6, React 19, TypeScript estricto, react-router 7, sin librerías de UI ni de gráficas): entrar / crear cuenta (errores de Identity en español), resumen del día por tienda (cifras, ventas por hora, medios de pago, "para revisar", última actualización en ámbar después de 24 h), ventas (por día, más vendidos, por cajero), cuadres con filtro de diferencias, tiendas con estado por caja y código de conexión con cuenta regresiva, asistente con aviso ámbar si no está configurado. Usa `escuadra.css` de la caja tal cual. Token en memoria y refresh en `localStorage`, renueva una vez ante un 401. `npm run check` (tsc + 12 pruebas de Vitest) pasa. Pendiente: `vite build` y `npm run dev` necesitan Node ≥ 18 (la PC tiene Node 16); con Node 22, pasar Vitest 0.34 → 3 y jsdom 22 → 26 (hoy fijados por Node 16); nombres de cajeros (TK-133); publicar en la Static Web App |
| TK-138 | Caja: Configuración → Nube (conectar con el código del panel, estado, desconectar) | ✅ Probado de punta a punta: caja → API local → nube → panel |
| TK-139 | Desplegar en Azure (Bicep, base, API nube, panel, modelo del asistente) y probar el asistente con el modelo real | ✅ API, panel, SQL (centralus) y `gpt-5-nano` en la suscripción MPN; salud y prueba de humo OK. Ver `docs/despliegue-azure.md` |
| TK-188 | Análisis de la competencia: Poszy (poszy.com) frente a Toki; brechas y tickets nuevos | ✅ `docs/competencia-poszy.md`; tickets TK-191..TK-203 en M15 |
| TK-189 | Consola de superadministrador (equipo de Toki): todos los clientes, tiendas, cajas, equipos, licencias, versión y estado de sincronización, con acceso de solo lectura y auditado. Hecho: rol `PlatformAdmin` (config `Platform:Admins` o `grant-platform-admin`), `/api/platform/*` auditado, desactivar cliente y revocar caja, área Plataforma en el panel. Pendiente: plan/licencia (la nube no la conoce) | 🟡 |
| TK-190 | Métricas de producto: activación, uso, retención y salud por tienda (latido del nodo, eventos de uso agregados, sin datos personales) en la consola. Hecho: `NodeHeartbeatV1`, `/api/platform/metrics/*`, tablero y `docs/metricas-producto.md`. Pendiente: balanza e impresora configuradas (las conoce la caja, no el servidor) | 🟡 |

## M13 · e-CF propio con la DGII

Diseño: `docs/ecf-propio.md` (alcance, criterios y dependencias de cada ticket en su §12; lo implementado de TK-144 a TK-150, en §10.9). Sin proveedor de terceros: la PC principal firma y envía directo a la DGII; la nube publica los servicios de receptor.

| Ticket | Qué | Estado |
|---|---|---|
| TK-140 | Investigación y diseño del camino propio (servicios, formatos, firma, certificación, arquitectura, riesgos) | ✅ `docs/ecf-propio.md` |
| TK-141 | Acceso a TesteCF, certificado de prueba y ADR-0009 | ⬜ |
| TK-142 | Firma XMLDSig con el certificado del almacén de Windows (`CertStoreEcfSigner`) e importación del `.p12` | ✅ `CertificateEcfSigner` (`Fiscal:Signer` = `Certificate`); pendiente la pantalla de importación del `.p12` y el aviso de vencimiento (`docs/ecf-propio.md` §10.8) |
| TK-143 | XML de e-CF 31/32/33/34 según el formato oficial, validado contra el XSD | ✅ `EcfXml` y XSD oficiales en `Toki.Fiscal/Schemas`; falta la prueba en TesteCF (TK-141) (`docs/ecf-propio.md` §10.8) |
| TK-144 | Cliente DGII en el nodo (semilla → token, recepción, RFCE, resultados, TrackIds, anulación, directorio) | 🟡 Falta probar en TesteCF. `DgiiClient`: semilla firmada → token en caché hasta 5 min antes de `expira` (y ante un 401), recepción, resultado, RFCE, consulta de RFCE, TrackIds, anulación, directorio y aprobación comercial; probado con la DGII simulada (`docs/ecf-propio.md` §10.9) |
| TK-145 | Resumen de factura de consumo (RFCE) y umbral de RD$250,000 | 🟡 Falta probar en TesteCF. RFCE armado desde el e-CF firmado, validado contra `RFCE 32 v.1.0.xsd`, firmado y guardado junto al e-CF; la 32 de RD$250,000 o más va completa |
| TK-146 | Cola de envío directo a la DGII (TrackId → resultado) y relevo convertido en archivo | 🟡 Falta probar en TesteCF. Cola `Pending → Sent (TrackId) → resultado`, RFCE inmediato, consulta antes de reenviar tras un fallo, salud 24/60 h; el relevo pasó a `POST /fiscal/v1/archive` (solo archivo); falta almacenamiento frío |
| TK-147 | Rechazos de la DGII: e-CF de reemplazo, no nota de crédito | 🟡 Falta probar en TesteCF. `GET /api/fiscal/documents?rejectedOnly=true` y `POST /api/fiscal/documents/{id}/replace`: e-CF nuevo con los mismos datos, ligado al rechazado; la venta pasa al nuevo e-NCF. Falta la pantalla "Rechazados por la DGII" y el aviso de aceptado condicional |
| TK-148 | Representación impresa y QR por tipo y ambiente, leyenda de contingencia, PDF para certificación | 🟡 Falta probar en TesteCF. QR `consultatimbre`/`consultatimbrefc` por ambiente, fecha de firma, "válido hasta" y leyenda de contingencia en el recibo. Falta dibujar el QR en el ESC/POS (TK-118) y el PDF para la certificación |
| TK-149 | Secuencias por ambiente y vencimiento opcional (32/34) | ✅ `Environment` en secuencias y documentos (lo existente queda `Simulated`), vencimiento opcional en la 32 y la 34, rango de TesteCF cargado solo, el 607 ignora TesteCF/CerteCF. La migración no tiene prueba propia en SQL Server |
| TK-150 | Anulación de e-NCF (ANECF) | 🟡 Falta probar en TesteCF. `POST /api/fiscal/sequences/{id}/void` arma, valida, firma y encola el ANECF de los números sin usar; `GET /api/fiscal/voids`. Falta la pantalla y anular e-CF firmados que nunca salieron |
| TK-151 | Copia del certificado en Key Vault con consentimiento y firmador de nube restringido | ⬜ |
| TK-152 | Servicios de receptor en la nube: Recepción (acuse firmado) y Aprobación Comercial | ⬜ |
| TK-153 | Entrega de la 31 al comprador electrónico | ⬜ |
| TK-154 | Comprobantes recibidos de suplidores en la tienda (y aprobación comercial opcional) | ⬜ |
| TK-155 | Asistente de certificación (set de pruebas, simulación, firmar XML de la DGII) | ⬜ |
| TK-156 | Operación: salud, estatus de la DGII y guía de contingencia | ⬜ |
| TK-157 | Contingencia por imposibilidad de emitir (serie B y regularización) | ⬜ |
| TK-158 | Pruebas de contrato y de punta a punta contra TesteCF | ⬜ |
| TK-159 | (Después de V1) Receptor sin custodia por túnel y E41 de compras | ⬜ |
| TK-176 | Importar el `.p12` desde Configuración → Comprobantes y avisar 30 días antes de que venza el certificado (resto de TK-142) | ✅ `POST/GET /api/fiscal/certificate`: contraseña una sola vez, nunca guardada; `LocalMachine\My` no exportable (o `CurrentUser\My` de la cuenta del servicio), carpeta de llaves cifrada en desarrollo; aviso a 30 días en el Resumen y en la barra de la caja (`docs/ecf-propio.md` §10.10) |
| TK-177 | Consultar a la DGII las preguntas abiertas de `docs/ecf-propio.md` §11 (peso con 3 decimales, comprador vacío en la 32, fecha límite de pago, saldo a favor como forma de pago, receptor por tienda) | ⬜ Necesita al dueño |
| TK-204 | Pantallas (Escuadra Admin) para "Rechazados por la DGII" (reemplazo) y anulación de rangos (ANECF) | ✅ `/admin/ecf` "Comprobantes electrónicos": ambiente, cola, rechazados con Reemplazar, últimos comprobantes y ANECF con vista previa; alerta de rechazados en el Resumen (§10.10) |
| TK-205 | Anular e-CF firmados que nunca se enviaron | ✅ `POST /api/fiscal/documents/{id}/void`: ANECF de ese número (antes pregunta a la DGII si hubo un envío fallido); el anulado se puede reemplazar (§10.10, §11 #26–27) |
| TK-206 | El cargo de fiado debe mostrar el e-NCF de reemplazo tras un rechazo | ✅ Clientes escucha `FiscalDocumentReplacedV1`: el cargo y el estado de cuenta pasan al e-NCF nuevo (§10.10) |
| TK-207 | Archivo frío del relevo en la nube (retención de 10 años) | ⬜ |
| TK-208 | Decisión del dueño: ¿la caja espera hasta 5 s el resultado de una 31 antes de imprimir? | ⬜ Necesita al dueño |
| TK-209 | Prueba de las migraciones nuevas de Fiscal sobre SQL Server | ✅ `SqlServerUpgradeTests` (V1 con datos → `DgiiDirect`) y todas las pruebas de integración verdes en SQL Express (§10.10) |

## M14 · Pendientes de las revisiones y de la nube

| Ticket | Qué | Estado |
|---|---|---|
| TK-160 | Probar instalador y servicio en una VM Windows limpia: cuenta del servicio, llave del certificado, `backups\work` (R5 #6, R6 2/5/10, TK-093) | ⬜ |
| TK-161 | Que un contador revise un mes de 606/607 (R5 #3) | ⬜ |
| TK-162 | Importar el padrón en segundo plano o con `SqlBulkCopy` si con el archivo real tarda más de un minuto (R5 #10) | ✅ La subida responde 202 y la carga corre en segundo plano (una a la vez, 409 si ya hay una), con progreso en `GET /api/fiscal/taxpayer-registry/import`; SQL Server usa `SqlBulkCopy` a una tabla de paso y cambia las filas en una transacción corta (las consultas siguen viendo el padrón anterior); Sqlite sigue con lotes de EF. Configuración → Comprobantes → Padrón de RNC sube el archivo y muestra el avance |
| TK-163 | Fijar la imagen de SQL Server de CI con etiqueta fechada (R1) | ✅ `2025-CU9-GDR1-ubuntu-22.04` en `tests/SqlServerImage.cs` |
| TK-164 | Reembolso en efectivo con centavos (R3) | ✅ El efectivo se devuelve en pesos enteros hacia abajo y los centavos quedan como `Rounding` (como la devuelta); la nota de crédito y los reembolsos del Z guardan el monto exacto |
| TK-165 | Nombres de cajeros y cajas en la nube (evento desde la tienda) para el panel y el asistente | ✅ `UserProfileChangedV1` y `RegisterChangedV1`, foto inicial al conectar; `cashierName`/`registerName` en el panel y el asistente |
| TK-166 | Devoluciones con líneas en `ReturnRecordedV1` para restarlas en los más vendidos | ✅ `ReturnRecordedV2` con líneas (V1 se sigue proyectando) |
| TK-167 | Proyectar en la réplica compras, clientes, apertura de turno y movimientos de caja | ✅ Más `GET /api/panel/receivables` y la herramienta `receivables` del asistente. Pendiente: abonos anulados y nombres de clientes no tienen evento |
| TK-168 | Precio mayorista en la caja (R7 #16) | ✅ Con cliente mayorista las líneas toman `WholesalePrice` (si existe) y la venta va con `PriceLevel.Wholesale`; "Precio mayorista" en la línea; otro cliente vuelve al detalle |
| TK-169 | Pendientes de R7: cola e-CF en la barra (#18), "Salir" con carrito (#26), recargar precios tras `Sales.TotalChanged` (#27) | ✅ "e-CF en cola: N" (ámbar si el más viejo pasa de 24 h) solo con `fiscal.manage`; "Salir" de administración pide espera o anular; `GET /api/inventory/products/prices` (solo `sales.create`) re-precia las líneas |
| TK-170 | Panel: Node 22, `vite build`, Vitest 3 y refrescar Tiendas al conectar una caja | ✅ Vitest 3, jsdom 30, Tiendas se refresca al conectar una caja |
| TK-174 | Nube: evento de saldo de clientes (incluye anulación de abonos) y nombres de clientes, para que "Te deben" sea exacto y diga nombres | ⬜ |
| TK-175 | Renombrar cajas desde Configuración (y enviar `RegisterChangedV1`) | ✅ `PUT /api/cash/registers/{id}` con `RegisterChangedV1` en la misma transacción; prueba de integración |
| TK-178 | Bug: `GET /api/inventory/stock?belowMinimumOnly=true` responde 500 | ✅ |
| TK-179 | Reportes en la tienda: mezcla de medios de pago y ventas por hora (hoy el Resumen suma los Z y no compara a la misma hora) | ⬜ |
| TK-180 | Nube: unidad (lb/unidad) en más vendidos | ⬜ |
| TK-181 | Panel: subir Vitest a 5 (aviso de npm audit en tinypool, solo desarrollo) | ✅ Vitest 5.0.3; `npm audit` sin hallazgos |
| TK-182 | Encontrar y arreglar la prueba que falló una vez en la suite (posible inestabilidad) | ✅ No se reprodujo (6 corridas completas, 140 de proyectos en paralelo, 720 de las clases bUnit sospechosas); no quedó registrado cuál prueba falló (la corrida fue en la copia principal que comparten varios agentes) |
| TK-183 | Modo oscuro de Escuadra Admin (variables listas) | ⬜ |

## M15 · Brechas frente a la competencia

Origen: `docs/competencia-poszy.md` (TK-188). Alcance y criterios de aceptación de cada ticket en su §8. Tamaño: S (≤ 2 días), M (≤ 1 semana), L (más).

| Ticket | Qué | Estado |
|---|---|---|
| TK-191 | Mínimos, máximos y pedido sugerido con el último suplidor; crear compra en borrador (M, depende de TK-178) | ⬜ |
| TK-192 | Reporte 608 de comprobantes anulados (CSV/Excel y TXT oficial) (S) | ⬜ |
| TK-193 | Encargos: fecha y hora de entrega, abono como anticipo, conversión en venta con el peso real, recordatorio (M) | ✅ `/api/orders` (`sales.Orders`); F10 en la caja guarda el carrito como encargo y lista los de hoy y mañana (también en el Resumen). El abono entra al Z como cobro a cliente y queda como saldo a favor; "Cobrar" carga el encargo a precio de hoy y el cobro usa ese saldo primero; la venta lo marca entregado en la misma transacción. Cancelar pide `sales.void` y devuelve el abono desde la caja. Pruebas de integración (Z, cancelar) y bUnit |
| TK-194 | Cotizaciones que se convierten en venta (primer paso del módulo Mayorista) (M) | ✅ Mismo `Order` con `Kind = Quote` y vigencia; vista para imprimir o guardar PDF (`/quotes/{id}`); al cobrarla la venta sale a precio de hoy y la caja muestra la diferencia. No mueve inventario ni caja |
| TK-195 | Catálogo público con QR y pedido por WhatsApp, desde la réplica de la nube (M) | ✅ Administración → Pedidos y catálogo: productos, WhatsApp, enlace y QR (`Cloud:PanelUrl`). `CatalogItemChangedV1` y `CatalogSettingsChangedV1` por el uploader; la nube los proyecta y sigue `PriceChangedV1`; `/api/public/catalog/{storeId}` sin sesión y sin costo ni existencia; página `/c/{storeId}` en el panel con "Pedir por WhatsApp" |
| TK-196 | Prueba gratis autoservicio: instalador desde la web, asistente de un equipo, datos de ejemplo, 30 días (M) | ⬜ |
| TK-197 | Sitio: precios públicos y páginas Carnicería, Colmado y Deli con FAQ (S–M, depende de TK-203) | ⬜ |
| TK-198 | Contenido e-CF: "¿Ya te toca?" y plazo del 15/11/2026 (S) | ⬜ |
| TK-199 | Programa de referidos para revendedores y contadores (S, decisión del dueño) | ⬜ |
| TK-200 | Cierre de turno (Z) y estado de cuenta de fiado en PDF de una página (S) | ⬜ |
| TK-201 | Costo, utilidad y valor de inventario visibles solo con permiso, en caja, administración, panel y exportaciones (S) | ⬜ |
| TK-202 | Investigación: caja ligera o toma de pedidos en tablet Android contra la PC principal (M, spike → ADR) | ⬜ |
| TK-203 | Revisar el plan de entrada frente a Poszy (colmado de una caja) (S, decisión del dueño) | ⬜ |
| TK-210 | Autorizaciones de supervisor opcionales por tienda (issue #179): Configuración → Cajas → "Pedir autorización de supervisor para…", un interruptor por acción (`/api/settings/approvals`, `IApprovalPolicy` en el decorador y en la venta). Por defecto las de hoy encendidas; "Quitar líneas o anular la venta en curso" (`sales.remove-line`, nueva) apagada. Cada autorización concedida queda en la auditoría (`approval.granted`). Probado también en SQL Express | ✅ |

## Revisiones

| Revisión | Hito | Estado |
|---|---|---|
| R7 (`backlog/reviews/R7-M11.md`) | M11 (frontend) | ✅ Corregidos 1–12, 14–17, 19–25, 28–31; 13, 18, 26 y 27 en parte. Sesión vencida → PIN sin perder el carrito; cobro incierto bloquea el carrito hasta consultar `GET /api/sales/{id}`; ids estables en devoluciones y movimientos (`movementId` nuevo en el servidor); conteo con `oninput` y cierre en dos pasos; lector ignorado en efectivo/cantidad/PIN; foco que no se pierde; `ErrorBoundary`; WebView2 cerrado; `errors.permission` en `Approval.Required`. Pendiente: contratos (TK-124), Playwright (TK-123), cola de e-CF en la barra, "Salir" de administración con carrito, recargar precios tras `Sales.TotalChanged` |
| R1 (`backlog/reviews/R1-M0-M1.md`) | M0 + M1 | ✅ Corregidos 1–8, 10–17 y 18 en parte. Pendiente: 9 (HTTPS, ver TK-093), fijar la imagen de SQL Server de CI con etiqueta fechada |
| R2 (`backlog/reviews/R2-M2.md`) | M2 | ✅ Corregidos 1–13, 15–17 (14 colación solo en SQL Server). Pruebas nuevas: conteo con ventas en medio, costo promedio, última libra en paralelo, kardex = existencia |
| R6 (`backlog/reviews/R6-M9.md`) | M9 (migraciones, outbox, respaldos) + R5 | ✅ Corregidos 1–15. Respaldo también al arrancar si el último tiene más de 20 h; SQL Server escribe el `.bak` en `respaldos\trabajo` (el instalador le da permiso), que se limpia en cada corrida; archivo `.parcial` hasta estar completo; 1 h de tiempo límite; error claro si la clave guardada no se puede leer (anillo de Data Protection en `C:\ProgramData\Toki\claves`); la copia fuera de la PC tiene su propia retención y no frena nada; formato de respaldo v2 (clave por archivo con HKDF, encabezado autenticado); el outbox lee cada mensaje una vez por corrida y usa un scope por mensaje; semillas literales y prueba que impide reescribir datos del dueño; migraciones en `StartingAsync` con 30 min; el 606 usa el medio de pago registrado; el e2e cuadra también el ITBIS del 607. Pendiente: verificar 2, 5 y 10 en la VM Windows limpia con el instalador |
| R5 (`backlog/reviews/R5-M8-M9.md`) | M8 + M9 + e2e | ✅ Corregidos 1–9, 11–16 y 10 en parte. 607 y 606 en el orden de columnas de la DGII con montos sin ITBIS; los reportes restan las devoluciones el día en que ocurren y usan nombres sin ambigüedad (`VentasConItbis`, `NetoSinItbis`, `VendidoConItbis`); un timeout del proveedor e-CF ya no detiene el servicio; la auditoría se verifica al arrancar y a las 03:00; el certificado se lee sin permisos de escritura; horas locales en CSV/Excel; Excel limitado a 50 000 filas; el Z imprime la cabeza de la auditoría; la suite e2e cuadra reportes con los Z, el 607 y el inventario. Pendiente: (10) importar el padrón en segundo plano o con `SqlBulkCopy` si con el archivo real tarda más de un minuto; (3) que el contador confirme un mes de 606/607; (6) probar en una VM limpia qué cuenta usa el servicio y el permiso sobre la llave del certificado (con el instalador, TK-093) |
| R4 (`backlog/reviews/R4-M6-M7.md`) | M6 + M7 | ✅ Corregidos 1–13, 15–19. Bloqueo de la fila de la compra, el costo promedio vuelve atrás al anular una compra, abonos con id de la caja (idempotentes), NCF únicos (índices filtrados) e ids del cliente en compras, gastos y pagos, anulación de gastos, pagos a suplidores y abonos, estado de cuenta que cuadra con anulaciones, abonos en efectivo en pesos enteros con redondeo, devoluciones y anulaciones pagan primero las deudas viejas, pago a suplidor desde la caja, ITBIS/total de la factura, saldos abiertos en una sola consulta, abonos por medio en el Z. Pendiente: las pruebas en paralelo solo compiten de verdad en el job de SQL Server; pasos de compras y fiado en la suite e2e (TK-095) |
| R3 (`backlog/reviews/R3-M3-M4.md`) | M3 + M4 | ✅ Corregidos 1–13, 15–20. Bloqueo de la fila de venta en anular/devolver, etiqueta leída en el servidor, reembolso por el mismo medio, tope y costo por línea, devuelta en pesos enteros (`Redondeo`), e-CF simulado, código de modificación y tipo de pago, caja ligada al equipo. Pendiente: 14 (enlace QR por tipo, con TK-055); el carrito en espera de un turno cerrado se purga cuando exista el despachador del outbox; el reembolso en efectivo puede llevar centavos. Las pruebas en paralelo solo compiten de verdad en el job de SQL Server |

## Fuera de V1 (no se construye ahora)

DOP/USD, cotizaciones y conduces, combos, lotes, multisucursal, host en la nube, módulos vendibles (`funcionalidades-v1.md` §5).
