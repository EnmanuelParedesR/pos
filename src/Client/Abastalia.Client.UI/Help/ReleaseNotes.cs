using System.Reflection;

namespace Abastalia.Client.UI.Help;

/// <param name="Highlights">3 to 5 short lines for the card on Resumen.</param>
/// <param name="More">The rest, only on the full list (admin/whats-new).</param>
public sealed record ReleaseNote(string Version, IReadOnlyList<string> Highlights, IReadOnlyList<string> More);

/// <summary>TK-226: what is new in each version, in the owner's words (no ticket numbers). Newest first.
/// Add the new version's entry here when it ships; the card shows once per user per version.</summary>
public static class ReleaseNotes
{
    /// <summary>"1.0.0" from the assembly's informational version, without the "+commit" suffix.</summary>
    public static string Current { get; } =
        (typeof(ReleaseNotes).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static readonly IReadOnlyList<ReleaseNote> All =
    [
        new("1.0.0",
            [
                "Encargos y cotizaciones: guarde el pedido con fecha de entrega y abono, y cóbrelo después con el peso real.",
                "Pedido sugerido: vea lo que está en su mínimo y cree la compra a su último suplidor en un clic.",
                "Catálogo con QR: sus clientes ven sus productos y le piden por WhatsApp.",
                "Permisos más finos: abrir y cerrar caja, cobrar abonos de crédito, pagar a suplidores y ver costos se dan por separado.",
                "Usted decide qué acciones piden autorización de un supervisor en su tienda.",
            ],
            [
                "Reporte 608 de comprobantes anulados, en Excel y en el formato de la DGII.",
                "Cierre Z y estado de cuenta del cliente en PDF de una página, listos para imprimir o enviar.",
                "Prueba gratis de 14 días desde la primera venta, con aviso antes de que termine; la licencia se compra sin reinstalar.",
                "¿Ya emite comprobantes electrónicos con otro programa? Comprobantes electrónicos le guía para pasarse a Abastalia.",
                "Pida acompañamiento para certificarse en facturación electrónica desde Comprobantes electrónicos.",
                "Roles nuevos para empezar: Cajero con apertura, Inventario y Contador.",
                "Pantalla de Auditoría: quién cambió qué y cuándo, por fechas, usuario y acción.",
                "Productos con presentaciones y etiquetas de balanza; el nombre del cliente sale solo al escribir su RNC.",
                "Panel en la nube: invite por correo a otras personas del negocio.",
            ]),
    ];

    public static ReleaseNote? For(string version) => All.FirstOrDefault(n => n.Version == version);
}
