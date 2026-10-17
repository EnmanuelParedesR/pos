using Abastalia.Fiscal.Contracts;

namespace Abastalia.Host.Cloud.Assistant;

/// <param name="Link">A panel path (/app/…) or a public page (/precios) the answer can give as is; null for a screen of the store's app.</param>
/// <param name="Who">Who can use it: "Dueño" or "Todos" (the Dueño and Solo lectura members).</param>
public sealed record HelpEntry(string Title, string Text, string? Link = null, string? Who = null);

/// <summary>TK-301: what Talia knows about Abastalia itself (the panel map, the store's Configuración, plans, license states, certification),
/// from Panel.tsx, Settings.razor, docs/cobro-paddle.md, docs/cuotas.md and docs/licencias.md. Fixed text compiled into Abastalia (never a
/// tenant's data). No prices: they come from Paddle and are on /precios and "Plan y facturación". Update it when those screens or documents change.</summary>
public static class PanelHelp
{
    private const string Store = "En la app de la tienda (Administración)";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<HelpEntry>> Topics = new Dictionary<string, IReadOnlyList<HelpEntry>>
    {
        ["panel"] =
        [
            new("Resumen", "Ventas de hoy y de los últimos 7 días, cómo le pagaron y lo que necesita atención.", "/app", "Todos"),
            new("Ventas", "Ventas por día, lo más vendido y ventas por cajero en las fechas que elija.", "/app/sales", "Todos"),
            new("Cuadres (turnos)", "Cada cierre de turno con el efectivo esperado, el contado y la diferencia; toque uno para ver e imprimir su Z.", "/app/shifts", "Todos"),
            new("Tiendas", "Sus tiendas y sus cajas conectadas. El Dueño crea tiendas, pide el código para conectar una caja y ve el Consumo del mes " +
                "(preguntas al asistente, facturas leídas con IA, correos, invitaciones, respaldos en la nube, usado contra el límite del plan).", "/app/stores", "Todos"),
            new("Asistente", "Talia: preguntas sobre ventas, cuadres, cuentas por cobrar, la certificación y cómo usar Abastalia. Con una tarjeta que usted confirma, " +
                "enciende o apaga sus avisos y, para el Dueño, invita o cancela invitaciones, cambia el rol o «Ver costos» de otro miembro, crea tiendas, " +
                "saca el código de conexión y acepta o rechaza comprobantes recibidos.", "/app/assistant", "Todos"),
            new("Recibidos", "Comprobantes electrónicos que le enviaron sus suplidores y su acuse.", "/app/received", "Todos"),
            new("Avisos", "Qué correos de aviso quiere recibir (e-CF rechazados o sin enviar, certificado por vencer, avance de la certificación).", "/app/notifications", "Todos"),
            new("Seguridad", "Verificación en dos pasos (código del teléfono al entrar) y sus códigos de respaldo.", "/app/security", "Todos"),
            new("Equipo", "Invitar personas al panel como Dueño o Solo lectura, permitir ver costos y quitar acceso. Las invitaciones del mes dependen del plan.", "/app/members", "Dueño"),
            new("Plan y facturación (Facturación)", "Ver su plan y su estado, elegir o cambiar el plan, agregar adicionales por cantidad, administrar el pago " +
                "(tarjeta, facturas, cancelar) y la tarjeta Recomiende Abastalia. Ver el tema planes.", "/app/billing", "Dueño"),
            new("Productos, inventario, compras, clientes, reportes", Store + ": Productos, Inventario, Compras y gastos, Clientes, Pedidos, Facturas, " +
                "Reportes (606, 607, 608), Turnos y Comprobantes electrónicos. El panel no los cambia."),
        ],
        ["planes"] =
        [
            new("Dónde se activan", "En Plan y facturación (solo el Dueño): «Elegir plan» y, en «Adapte su plan», la cantidad de cada adicional " +
                "(sin suscripción: elija las cantidades y luego el plan; con suscripción: «Actualizar adicionales»). Mensual o anual se elige al pagar; " +
                "un cambio se prorratea al momento. Paddle cobra por Abastalia, calcula los impuestos y envía la factura.", "/app/billing", "Dueño"),
            new("Si el pago en línea no está disponible", "La página lo dice («El pago en línea todavía no está disponible»): escríbanos y Abastalia lo activa.", "/precios#contacto"),
            new("Precios", "Los precios están en /precios y en Plan y facturación (vienen de Paddle). No los diga de memoria.", "/precios"),
            new("Todos los planes", "Tienen todas las funciones: balanza y carnicería, mayoreo, e-CF, inventario, respaldo en la nube y asistente. Cambian en tamaño."),
            new("Esencial", "Solo la PC principal, 2 cajeros, 2 invitaciones al panel por mes, 100 preguntas al asistente y 20 facturas leídas con IA por mes."),
            new("Estándar", "PC principal + 1 caja, 4 cajeros, 3 invitaciones, 300 preguntas y 20 facturas por mes."),
            new("Pro", "PC principal + 3 cajas, 8 cajeros, 5 invitaciones, 600 preguntas, 20 facturas por mes y soporte más rápido."),
            new("Varias sucursales", "Ya no se vende; sigue funcionando para quien lo tiene."),
            new("Adicionales (por cantidad)", "Caja adicional: 1 caja y 2 cajeros más en cada tienda. Cajero adicional: 1 cajero. Sucursal adicional: 1 tienda más con licencia " +
                "y 4 cajeros. Lectura de facturas con IA: +300 facturas al mes. Asistente +1,000 preguntas. Usuarios del panel +5: 5 invitaciones más al mes."),
            new("Límites del mes", "Lo exacto de su negocio y lo usado está en Consumo del mes, en Tiendas. Se renueva el día 1.", "/app/stores", "Dueño"),
            new("Prueba", "14 días con todo desde la primera venta, sin tarjeta; 30 días si llegó recomendado."),
            new("Recomiende Abastalia", "En Plan y facturación: copie su enlace; cuando un negocio recomendado paga un plan, usted recibe un descuento en su próximo pago (hasta 3 por mes).", "/app/billing", "Dueño"),
            new("Pago atrasado", "Paddle reintenta el cobro. Actualice la tarjeta en «Administrar pago»; si no se paga, la licencia vence (ver el tema licencia).", "/app/billing", "Dueño"),
        ],
        ["licencia"] =
        [
            new("Prueba", "Sin licencia: 14 días (30 si llegó recomendado) desde la primera venta, todo encendido."),
            new("Activa", "Licencia vigente. Con el plan pagado en línea, la tienda conectada la recibe sola de la nube."),
            new("Gracia", "Venció hace menos de 15 días: todo sigue igual."),
            new("Restringido", "Terminó la prueba o la gracia: funciones pagas apagadas, la PC principal y una caja, 2 cajeros. Todavía vende y factura; el aviso dice el día en que se detiene la venta."),
            new("Bloqueada", "30 días después del vencimiento o del fin de la prueba, o si Abastalia bloqueó el negocio. No vende ni abre turnos ni emite e-CF nuevos. " +
                "Sí cierra el turno abierto, envía a la DGII lo ya emitido, muestra reportes, exporta 606/607/608 y respalda. Ningún dato se pierde."),
            new("Cómo salir", "Pague en Plan y facturación: la tienda conectada recibe la licencia nueva sola en minutos. O instale una licencia en la tienda: " +
                "Configuración → Licencia → «Activar licencia». Atrasar el reloj de la PC no la estira.", "/app/billing", "Dueño"),
        ],
        ["tienda"] =
        [
            new("Dónde", Store + " → Configuración. Cada sección se guarda por separado; cada una la ve quien tiene el permiso."),
            new("Negocio", "Nombre, RNC, dirección, teléfono, pie de la factura y logo (PNG o JPG hasta 512 KB) para la factura y el catálogo."),
            new("Cajas", "Crear y nombrar las cajas y su fondo estándar (abrir con otro monto pide un supervisor)."),
            new("Comprobantes", "Secuencias NCF/e-NCF, el certificado digital (.p12 o .pfx) y el ambiente de la DGII: Pre-certificación (TesteCF), Certificación (CerteCF) o Producción (eCF)."),
            new("Equipos y Equipos de la caja", "Generar el código para unir otra caja de la misma tienda (vale 10 minutos); probar impresora y balanza en cada caja."),
            new("Usuarios y Roles", "Crear cajeros con su rol y PIN. El plan limita cuántos cajeros hay habilitados; desactivar a uno libera su lugar y guarda su historial."),
            new("Respaldos", "Frase de los respaldos (guárdela en papel), «Respaldar ahora» y restaurar. Con la tienda conectada, también se copian a la nube."),
            new("Nube", "Conectar la tienda al panel con el código de Tiendas → Conectar una caja, y desconectarla.", "/app/stores", "Dueño"),
            new("Licencia", "Estado de la licencia y «Activar licencia» con el archivo o el código."),
            new("Salud y Actualizaciones", "Salud dice qué revisar. Las actualizaciones se instalan solas: la caja al abrir, el servidor sin turnos abiertos."),
            new("Comprobantes electrónicos", Store + " → Comprobantes electrónicos: lo enviado a la DGII, lo rechazado, la certificación paso a paso, el receptor y la contingencia."),
        ],
        ["certificacion"] =
        [
            new("Dónde", Store + " → Comprobantes electrónicos → Certificación con la DGII. «¿Qué hago ahora?» dice el paso siguiente. " +
                "Para el paso de cada tienda use certification_status; para dudas, certification_faq."),
            .. CertificationGuide.Steps.Select((s, i) => new HelpEntry($"Paso {i + 1}: {s.Title}", s.Hint, Who: s.Who)),
        ],
    };
}
