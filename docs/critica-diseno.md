## Crítica de diseño: Abastalia V1 (caja y administración)

Revisión del canvas de pantallas (Entrar, Abrir caja, Venta y sus 6 excepciones, Cobrar, Venta lista, Cerrar caja,
Resumen, Turnos, Productos, Inventario, Compra, Conteo, celular y Escuadra) contra el backend V1 ya construido.
Etapa: refinamiento antes de implementar en Blazor. 15 de octubre de 2026.

### Impresión general

La pantalla de venta está muy bien resuelta: el total y la balanza mandan, el escáner nunca pierde el foco y cada
acción tiene su tecla visible. El riesgo principal no es visual sino de alcance: el canvas todavía dibuja una marca
por revendedor y funciones que el backend V1 no tiene, y le faltan las pantallas que el backend sí exige
(emparejar, autorización de supervisor, anular y devolver, crédito).

### Usabilidad

| Hallazgo | Severidad | Recomendación |
|---|---|---|
| No hay pantalla para emparejar un equipo ni para la instalación inicial; sin `X-Dispositivo` ninguna caja puede entrar | 🔴 Crítico | Pantalla "Configurar este equipo": instalar (primera vez) o escribir el código de 6 dígitos que da el dueño |
| El diálogo de PIN solo existe para "quitar línea"; el backend pide supervisor para descuento, cambio de precio, anular, devolver, retiro de caja y ver el resultado del cuadre | 🔴 Crítico | Un solo diálogo "Autoriza un supervisor" con el motivo arriba, usuario + PIN, y que devuelva la autorización a la acción que la pidió |
| No hay lista de ventas del turno; anular y devolver no tienen dónde vivir | 🔴 Crítico | Pantalla "Ventas de hoy" (F9 → Ventas) con buscar por número, ver, reimprimir, anular y devolver líneas |
| "Vender sin el servidor" (VentaSinServidor) promete algo que V1 no hace: las cajas dependen del servidor de la tienda | 🟡 Moderado | Cambiarla a "Sin conexión con el servidor: reintentando", en ámbar, con el carrito intacto y Cobrar deshabilitado |
| Cuadre pide "Cierre del verifone"; el backend cuenta solo efectivo y separa tarjeta y transferencia por método | 🟡 Moderado | Quitar el campo; en el resultado mostrar efectivo esperado vs contado, y tarjeta/transferencia como informativo |
| Elegir cliente (F6) no dice si tiene crédito, límite o saldo a favor; vender a crédito no está dibujado | 🟡 Moderado | Buscador de cliente con saldo y límite visibles; "A crédito" como forma de pago cuando el cliente lo permite |
| Ventas en espera (F5) guarda pero no hay forma de ver ni retomar las guardadas | 🟡 Moderado | Lista "En espera" con hora, artículos y total; Enter retoma |
| Entradas y salidas de caja (retiro a bóveda, pago a suplidor desde la gaveta) no tienen pantalla | 🟡 Moderado | F9 Caja → "Entrada" / "Salida" con monto, motivo y supervisor si es salida |
| Asistente en Resumen y pantallas del celular son fase 2 pero se ven como V1 | 🟢 Menor | Sacarlos de la fila V1 o marcarlos "Fase 2" en el canvas |
| Faltan las pantallas de Configuración (negocio, secuencias e-NCF, licencia, respaldos, usuarios, equipos) | 🟢 Menor | Una página con secciones; cada sección es un formulario corto |

### Jerarquía visual

- **Lo primero que se ve**: en Venta, el total del carrito y la lectura de la balanza; en Cobrar, el total a 72 px.
  Correcto: es lo que la cajera y el cliente necesitan.
- **Flujo de lectura**: barra de estado → escáner → mosaicos → carrito → Cobrar. La barra de teclas abajo cierra el
  recorrido sin competir.
- **Énfasis**: el azul Abastalia se reserva para la acción principal (Cobrar, Confirmar). En Resumen hay seis tarjetas
  del mismo peso; "Para revisar" debería ir antes que las cifras porque es lo que pide acción.

### Consistencia

| Elemento | Problema | Recomendación |
|---|---|---|
| Marca | `[LOGO]` provisional y propiedad `brand` con 3 colores en cada pantalla; contradice la decisión de marca propia | Logo C5 (etiqueta con el ojo) en la barra de estado y azul #0B5CAD fijo; quitar la propiedad |
| Bordes | Las tarjetas usan #C9CED4 en unas pantallas y #B4BBC3 en el sistema | Un solo token `--borde-tarjeta: #C9CED4`; controles siguen con #7A838C |
| Diálogo de supervisor | Dibujado solo dentro de Venta | Componente único de Escuadra, igual en caja y administración |
| Escuadra | La hoja aún dice que el color lo pone cada revendedor y muestra el logo viejo | Actualizar a marca fija y logo C5 |
| Estados del cuadre | El paso 3 "Resultado (supervisor)" usa un modelo propio | Usar la misma autorización `VerResultadoCuadre` que el resto |

### Accesibilidad

- **Contraste**: texto #14181D sobre #EEF0F2 ≈ 15:1; texto secundario #4A535D sobre blanco ≈ 7.6:1; blanco sobre
  #0B5CAD ≈ 6.6:1; ámbar #8A4B00 sobre blanco ≈ 7:1; #A9B2BC sobre el marco #14181D ≈ 8.8:1. Todo pasa AA, casi todo AAA.
- **Bordes de tarjeta**: #C9CED4 sobre #EEF0F2 es ≈ 1.3:1. Aceptable porque las tarjetas no son controles; los
  controles usan #7A838C (≥ 3:1).
- **Áreas táctiles**: 48 px mínimo y 72 px en dinero se cumplen. Las píldoras de categoría deben medir 48 px de alto,
  no 40.
- **Lectura**: 15 px base con escala 13–72 y cifras tabulares. Los avisos no dependen solo del color (llevan ícono y
  texto).
- **Teclado**: cada acción tiene tecla visible. Falta definir el orden del foco en los diálogos (PIN → Autorizar → Esc).

### Lo que funciona bien

- La balanza arriba con PESO / PRECIO / IMPORTE, y el escáner que nunca se pierde.
- Avisos en línea en vez de ventanas (código no encontrado, etiqueta repetida).
- El cuadre a ciegas: la cajera no ve el esperado.
- La paleta sobria con un solo color de acción; sin sombras ni animaciones.

### Recomendaciones prioritarias

1. **Completar el ciclo que el backend exige**: emparejar/instalar, diálogo de supervisor único, ventas del turno con
   anular y devolver. Sin esto la caja no puede operar en una tienda real.
2. **Alinear el alcance con V1**: marca fija con logo C5, sin "vender sin servidor", sin verifone, asistente y celular
   marcados fase 2.
3. **Crédito y espera**: cliente con saldo y límite, venta a crédito, lista de ventas en espera, entradas y salidas de
   caja.
