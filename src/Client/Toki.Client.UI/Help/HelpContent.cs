namespace Toki.Client.UI.Help;

public sealed record HelpTopic(string Title, IReadOnlyList<string> Tips, IReadOnlyList<(string Key, string Action)> Shortcuts);

/// <summary>TK-242: every help text of the app in one place, so whoever edits it does not hunt through the screens.
/// F1 opens the panel of the screen on view (HelpPanel); the "Primer día" guide is a page of its own, reached from Resumen.</summary>
public static class HelpContent
{
    private static readonly (string, string)[] AdminKeys = [("F1", "Esta ayuda"), ("F2", "Ir a vender")];

    /// <summary>By route (without the leading slash); the longest one that starts the current address wins.</summary>
    private static readonly Dictionary<string, HelpTopic> Topics = new()
    {
        ["sale"] = new("Vender",
            [
                "Escanee el producto o escriba su nombre o código y pulse Enter. Lo pesado con etiqueta de balanza entra con su peso.",
                "Toque una línea para elegirla; después cambie su cantidad, su precio o dele descuento.",
                "Para cobrar pulse F12, elija cómo paga el cliente y confirme. Sin conexión con el servidor no se cobra: la venta espera en pantalla.",
                "Si el cliente vuelve luego, ponga la venta en espera (F5) y atienda al siguiente.",
            ],
            [("F1", "Esta ayuda"), ("F2", "Ir al escáner"), ("F3", "Cantidad"), ("F4", "Precio"), ("F5", "Poner en espera"), ("F6", "Cliente"),
             ("F7", "Descuento"), ("F8", "Anular la venta"), ("F9", "Menú de caja"), ("F10", "Pedidos"), ("F12", "Cobrar"),
             ("↑ ↓", "Elegir línea"), ("Supr", "Quitar la línea elegida")]),
        ["open-register"] = new("Abrir caja",
            ["Elija la caja y cuente el fondo con que empieza, billete por billete. Ese monto es la base del cuadre al cerrar."],
            [("F1", "Esta ayuda"), ("F12", "Abrir caja y vender")]),
        ["close-register"] = new("Cerrar caja",
            [
                "Cuente el efectivo de la gaveta sin mirar el sistema: el cierre es a ciegas y la diferencia sale al confirmar.",
                "Antes, cobre, ponga en espera o anule la venta que quede en pantalla.",
                "El cierre no se puede deshacer: F12 pide confirmación y la segunda vez cierra.",
            ],
            [("F1", "Esta ayuda"), ("F12", "Cerrar (dos veces para confirmar)")]),
        ["shift-sales"] = new("Ventas de este turno",
            ["Aquí ve, reimprime o anula las ventas del turno abierto. Anular pide un motivo."],
            [("F1", "Esta ayuda")]),
        ["sign-in"] = new("Entrar",
            ["Toque su nombre y escriba su PIN. Si lo olvidó, el dueño se lo cambia en Configuración → Usuarios."],
            [("F1", "Esta ayuda")]),
        ["setup"] = new("Configurar este equipo",
            [
                "En la PC principal elija «Primera instalación». En otra caja elija «Unir a una tienda» y escriba el código de Configuración → Equipos → Generar código (vale 10 minutos).",
                "La huella del servidor debe ser igual a la que ve el dueño. Si no es igual, no una el equipo y avise al técnico.",
            ],
            [("F1", "Esta ayuda")]),
        ["admin"] = new("Resumen",
            [
                "Arriba va lo que necesita su atención: faltantes en caja, comprobantes por agotarse, pedidos para hoy.",
                "Las ventas del periodo se comparan con las mismas horas de la semana anterior.",
                "¿Primer día con Toki? Abra la guía «Primer día» desde este Resumen.",
            ],
            AdminKeys),
        ["admin/products"] = new("Productos",
            ["Los precios llevan el ITBIS incluido. Toque un producto para cambiar su precio, su código o si se vende por libra."],
            AdminKeys),
        ["admin/inventory"] = new("Inventario",
            ["Cuánto hay de cada producto. Toque uno para ver sus movimientos; los conteos físicos se hacen a ciegas."],
            AdminKeys),
        ["admin/purchasing"] = new("Compras y gastos",
            ["Registre la mercancía que entra con su factura: sube la existencia y se actualiza el costo. Los gastos con NCF van al 606."],
            AdminKeys),
        ["admin/shifts"] = new("Turnos y cuadres",
            ["Cada turno con lo esperado y lo contado en efectivo. Toque uno para ver su cierre Z."],
            AdminKeys),
        ["admin/customers"] = new("Clientes",
            ["Quién le debe y cuánto. Toque un cliente para ver su estado de cuenta o cambiar su límite de crédito."],
            AdminKeys),
        ["admin/orders"] = new("Pedidos y catálogo",
            ["Los encargos con su abono y las cotizaciones. El catálogo es la lista que sus clientes ven para pedir por WhatsApp."],
            AdminKeys),
        ["admin/invoices"] = new("Facturas",
            ["Busque por número o e-NCF, o toque una factura para verla y reimprimirla."],
            AdminKeys),
        ["admin/reports"] = new("Reportes",
            ["Descargue en Excel o CSV lo que necesita para revisar el negocio o para su contador (606, 607, 608)."],
            AdminKeys),
        ["admin/audit"] = new("Auditoría",
            ["Quién hizo qué y cuándo. Si alguien cambia un registro en la base de datos, la cadena lo muestra."],
            AdminKeys),
        ["admin/ecf"] = new("Comprobantes electrónicos",
            ["Lo enviado a la DGII, lo rechazado y los números que sobran. Un e-CF tiene 72 horas para llegar a la DGII."],
            AdminKeys),
        ["admin/settings"] = new("Configuración",
            ["Datos del negocio, cajas, comprobantes, equipos, usuarios, respaldos y licencia. Cada sección se guarda por separado."],
            AdminKeys),
        ["admin/first-day"] = new("Primer día", ["Siga los pasos en orden. Puede volver a esta guía desde el Resumen."], AdminKeys),
    };

    private static readonly HelpTopic General = new("Ayuda", ["Pulse F1 en cualquier pantalla para ver qué se hace en ella y sus atajos."], [("F1", "Esta ayuda")]);

    public static HelpTopic For(string route)
    {
        var path = route.Split('?', '#')[0].Trim('/');
        return Topics.Where(t => path == t.Key || path.StartsWith(t.Key + "/", StringComparison.Ordinal))
            .OrderByDescending(t => t.Key.Length).Select(t => t.Value).FirstOrDefault() ?? General;
    }

    /// <summary>The "Primer día" guide: what the owner does once, in order, before the first sale.</summary>
    public static readonly IReadOnlyList<(string Title, string Text, string Href)> FirstDay =
    [
        ("Datos del negocio", "Nombre, RNC, dirección y pie de la factura. Salen en cada factura.", "admin/settings"),
        ("Usuarios y PIN", "Cree a cada cajero con su rol y su PIN. Nadie comparte el PIN.", "admin/settings"),
        ("Productos", "Cargue sus productos con precio (ITBIS incluido) y si se venden por libra.", "admin/products"),
        ("Comprobantes", "Cargue sus rangos de comprobantes y, para facturar e-CF, su certificado digital (.p12).", "admin/settings"),
        ("Impresora y balanza", "En cada caja, Configuración → Equipos de la caja: pruebe la impresora y la balanza.", "admin/settings"),
        ("Respaldos", "Escriba la frase de los respaldos y guárdela en papel fuera de la tienda: sin ella no se abre un respaldo.", "admin/settings"),
        ("Primera venta", "Abra la caja con su fondo, venda y cierre al final del día. La prueba de 14 días empieza con esa venta.", "open-register"),
    ];
}
