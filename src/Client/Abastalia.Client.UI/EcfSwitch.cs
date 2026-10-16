using System.Globalization;
using Abastalia.Client.UI.Api;

namespace Abastalia.Client.UI;

/// <summary>TK-221: moving to Abastalia when the business already issues e-CF with another program (no new certification, to be confirmed with
/// the DGII: docs/ecf-propio.md §11 #40). Pure logic behind the guided path on "Comprobantes electrónicos".</summary>
public static class EcfSwitch
{
    /// <summary>The first number Abastalia issues after the last one the other program used.</summary>
    public sealed record NextNumber(string Type, long Number, string ENcf);

    /// <summary>A checklist row. <c>Done</c> null: Abastalia cannot see it, the owner confirms it.</summary>
    public sealed record Item(string Title, bool? Done, string Hint);

    /// <summary>"E320000000123" → Consumer32, 124, "E320000000124". Null when it is not an e-NCF of a type Abastalia issues (31 to 34) or has no next number.</summary>
    public static NextNumber? After(string? lastUsed)
    {
        var s = lastUsed?.Trim().ToUpperInvariant();
        if (s is not { Length: 13 } || s[0] != 'E'
            || !int.TryParse(s.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            || !long.TryParse(s.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
        var type = code switch { 31 => "FiscalCredit31", 32 => "Consumer32", 33 => "DebitNote33", 34 => "CreditNote34", _ => null };
        return type is null || n >= 9_999_999_999 ? null : new(type, n + 1, $"E{code}{n + 1:D10}");
    }

    /// <summary>What is left before Abastalia issues for real. Only what the store can see is ticked; the DGII directory and the old program are the owner's.</summary>
    public static List<Item> Checklist(EcfCertificateStatus? certificate, EcfReceiverStatus? receiver, IEnumerable<EcfSequence> sequences, string? environment) =>
    [
        new("Certificado digital importado y vigente", certificate is { Installed: true, DaysLeft: >= 0 },
            "Sirve el mismo .p12 que usaba el otro programa si sigue vigente. Se importa en Configuración → Comprobantes."),
        new("Receptor en la nube activo", receiver?.Active == true,
            "Compras → Recibidos (e-CF) → Receptor en la nube. Da las direcciones que se registran en la DGII."),
        new("Direcciones de Abastalia en \"Mantenimiento Directorio FE\" de la OFV", null,
            "Lo hace usted en la Oficina Virtual con su clave. Reemplace las del otro programa por las de Abastalia, completas."),
        new("Secuencias de producción cargadas", sequences.Any(s => s.Environment == "eCF" && s.Available > 0),
            "Desde el número siguiente al último que usó el otro programa, o una secuencia nueva de la DGII."),
        new("Dejar de facturar con el otro programa", null,
            "Dos programas con la misma secuencia repiten números. Desde el cambio, cada e-NCF sale solo de Abastalia."),
        new("Ambiente en Producción", environment == "eCF",
            "El técnico de Abastalia lo cambia en la PC principal cuando lo anterior esté listo."),
    ];
}
