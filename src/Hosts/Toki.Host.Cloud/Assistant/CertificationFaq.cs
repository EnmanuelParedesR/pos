namespace Toki.Host.Cloud.Assistant;

public sealed record FaqEntry(string Question, string Answer);

/// <summary>TK-256: what the assistant may say about the DGII certification, from docs/servicio-certificacion.md and docs/contingencia-ecf.md.
/// Fixed text compiled into Toki (never from a tenant's data). Update it when those documents change.</summary>
public static class CertificationFaq
{
    public static readonly IReadOnlyList<FaqEntry> Entries =
    [
        new("¿Qué necesito antes de empezar?",
            "RNC activo y al día con la DGII; acceso a la Oficina Virtual (OFV) de la empresa; alta de NCF; un representante vinculado al RNC que será el Administrador e-CF; " +
            "su certificado digital de persona física para procedimientos tributarios como archivo .p12 o .pfx (no en token USB ni en la nube), comprado a Avansi (Viafirma) " +
            "o a la Cámara de Comercio y Producción de Santo Domingo (Digifirma); y la PC principal con Toki instalado, encendida y con internet."),
        new("¿Toki necesita mi clave de la OFV?",
            "No. Toki nunca pide, guarda ni ve la clave de la OFV ni la contraseña del certificado. Los pasos que la piden los hace el dueño en su navegador; " +
            "la contraseña del .p12 se escribe una sola vez al importarlo en Toki. Si alguien le pide la clave en nombre de Toki, no la dé."),
        new("¿Dónde sigo la certificación en Toki?",
            "En Administración → Comprobantes electrónicos → Certificación con la DGII. Cada paso tiene ayuda y la tarjeta \"¿Qué hago ahora?\" dice lo siguiente. " +
            "Marque cada paso cuando lo termine."),
        new("¿Cuánto tarda?",
            "No hay una fecha fija: varios pasos dependen de la DGII o de la entidad que emite el certificado. Lo que hace Toki (set de pruebas, simulación, firma de archivos) " +
            "se puede completar en una tarde cuando la DGII abre cada paso."),
        new("¿Qué pasa si la DGII rechaza un comprobante del set de pruebas?",
            "El set se reinicia en la DGII: descargue el Excel nuevo del portal, súbalo otra vez en Toki y vuelva a enviar."),
        new("¿Qué pasa si la DGII rechaza la solicitud FI-GDF-016?",
            "Lea el motivo en el buzón de la OFV, corríjalo con la DGII o su contador (por ejemplo, declaraciones pendientes) y vuelva a enviarla."),
        new("El certificado no entra en Toki",
            "Toki solo acepta .p12 o .pfx del mismo RNC del negocio y ya vigente. Si es de otra persona u otro RNC, o vino en token, pida uno correcto a la entidad certificadora."),
        new("¿Puedo pedir ayuda de una persona?",
            "Sí: en Comprobantes electrónicos toque \"Solicitar acompañamiento\". Un técnico de Toki le guía en dos sesiones remotas cortas, con usted presente; " +
            "usted escribe sus claves y el técnico pausa la vista mientras tanto."),
        new("¿Y si no llego a certificarme antes del 15 de noviembre de 2026?",
            "Plan B para poco volumen: emitir con el Facturador Gratuito de la DGII (en la OFV) mientras termina la certificación con Toki. " +
            "Los pasos están en la ayuda de Comprobantes electrónicos (\"Qué hacer si no se puede facturar con e-CF\"). Confirme con la DGII o su contador si su negocio califica."),
        new("¿Qué hago si no puedo emitir e-CF (sin internet, certificado vencido)?",
            "Sin internet o con la DGII caída, siga vendiendo: los comprobantes salen solos al volver (72 horas de plazo). Sin certificado o con la PC dañada, " +
            "declare la contingencia en la OFV y actívela en Toki para facturar con serie B (máximo 15 días)."),
    ];
}
