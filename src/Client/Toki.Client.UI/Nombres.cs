namespace Toki.Client.UI;

/// <summary>How API enum names read on screen.</summary>
public static class Nombres
{
    public static string Comprobante(string tipo) => tipo switch
    {
        "CreditoFiscal31" => "Crédito fiscal (E31)",
        "Consumo32" => "Consumo (E32)",
        "NotaCredito34" => "Nota de crédito (E34)",
        _ => tipo,
    };

    public static string Itbis(string tasa) => tasa switch
    {
        "General18" => "18 %",
        "Reducida16" => "16 %",
        "Cero" => "0 %",
        _ => "Exento",
    };

    public static string Unidad(string unidad) => unidad == "Libra" ? "Por libra" : "Por unidad";
}
