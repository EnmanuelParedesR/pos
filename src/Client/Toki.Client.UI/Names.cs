using Toki.Client.UI.Api;

namespace Toki.Client.UI;

/// <summary>How API enum names read on screen.</summary>
public static class Names
{
    public static string DocumentType(string type) => type switch
    {
        "FiscalCredit31" => "Crédito fiscal (E31)",
        "Consumer32" => "Consumo (E32)",
        "CreditNote34" => "Nota de crédito (E34)",
        _ => type,
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
