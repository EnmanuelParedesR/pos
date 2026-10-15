using Toki.Client.UI.Api;

namespace Toki.Client.UI;

/// <summary>How API enum names read on screen.</summary>
public static class Names
{
    public static string DocumentType(string type) => type switch
    {
        "FiscalCredit31" => "Crédito fiscal (E31)",
        "Consumer32" => "Consumo (E32)",
        "DebitNote33" => "Nota de débito (E33)",
        "CreditNote34" => "Nota de crédito (E34)",
        _ => type,
    };

    /// <summary>Where the store's e-CF go (docs/ecf-propio.md §2.1).</summary>
    public static string FiscalEnvironment(string environment) => environment switch
    {
        "Simulated" => "Simulado",
        "TesteCF" => "Pre-certificación (TesteCF)",
        "CerteCF" => "Certificación (CerteCF)",
        "eCF" => "Producción",
        _ => environment,
    };

    /// <summary>An e-CF's way to the DGII, and the pill it wears: amber while it waits, red only when the DGII said no.</summary>
    public static (string Text, string Pill) EcfStatus(string status) => status switch
    {
        "Pending" => ("Por enviar", "warning"),
        "Sent" => ("Esperando a la DGII", "warning"),
        "Accepted" => ("Aceptado", "success"),
        "AcceptedConditional" => ("Aceptado condicional", "success"),
        "Rejected" => ("Rechazado", "error"),
        "Voided" => ("Anulado sin enviar", "neutral"),
        _ => ("Simulado", "neutral"),
    };

    public static string Itbis(string rate) => rate switch
    {
        "General18" => "18 %",
        "Reduced16" => "16 %",
        "Zero" => "0 %",
        _ => "Exento",
    };

    public static string Unit(string unit) => unit == "Pound" ? "Por libra" : "Por unidad";

    public static string PaymentMethod(PaymentMethod method) => method switch
    {
        Api.PaymentMethod.Cash => "Efectivo",
        Api.PaymentMethod.Card => "Tarjeta",
        Api.PaymentMethod.Transfer => "Transferencia",
        Api.PaymentMethod.OnAccount => "A crédito (fiado)",
        _ => "Saldo a favor",
    };

    /// <summary>"Ramón Duarte" → "RD", for the avatar next to a person's name.</summary>
    public static string Initials(string? name) =>
        string.Concat((name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public static string SaleStatus(string status) => status == "Voided" ? "Anulada" : "Completada";

    public static string LicenseStatus(string status) => status switch
    {
        "Trial" => "Prueba",
        "Active" => "Activa",
        "Grace" => "Gracia",
        "Restricted" => "Restringida",
        _ => status,
    };

    public static string Feature(string feature) => feature switch
    {
        "ButcherModule" => "Módulo de carnicería",
        "WholesaleModule" => "Módulo mayorista",
        "CloudBackup" => "Respaldo en la nube",
        "AiAssistant" => "Asistente IA",
        _ => feature,
    };
}
