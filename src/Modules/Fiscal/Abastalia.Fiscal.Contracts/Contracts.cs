using Abastalia.SharedKernel;

namespace Abastalia.Fiscal.Contracts;

/// <summary>Electronic receipt types (e-CF) used in V1.</summary>
public enum EcfType { FiscalCredit31 = 31, Consumer32 = 32, DebitNote33 = 33, CreditNote34 = 34 }

public sealed record FiscalLine(string Description, decimal Quantity, decimal UnitPrice, decimal Discount, ItbisRate Rate, decimal Itbis, decimal Total);

/// <summary>DGII modification code: 1 cancels the whole document (a void), 3 corrects amounts (a return), 4 replaces a paper NCF (series B)
/// issued in contingency (TK-157).</summary>
public enum ModificationCode { Void = 1, AmountCorrection = 3, ContingencyReplacement = 4 }

public enum EcfPaymentType { Cash = 1, Credit = 2 }

/// <summary>DGII payment form (<c>FormaPago</c>).</summary>
public enum EcfPaymentForm { Cash = 1, CheckOrTransfer = 2, Card = 3, OnCredit = 4, GiftCertificate = 5, Barter = 6, CreditNote = 7, Other = 8 }

public sealed record FiscalPayment(EcfPaymentForm Form, decimal Amount);

/// <summary>Consumer receipts (32) of this amount or more must identify the buyer.</summary>
public static class EcfLimits
{
    public const decimal IdentifiedConsumer = 250_000m;
}

/// <param name="ModifiedENcf">For credit notes: the e-NCF being corrected (required).</param>
/// <param name="Payments">How the sale was paid; goes in the e-CF payment table (not in credit notes).</param>
public sealed record DocumentToIssue(
    EcfType Type, Guid ReferenceId, string? BuyerRnc, string? BuyerName,
    IReadOnlyList<FiscalLine> Lines, decimal Itbis, decimal Total, string? ModifiedENcf = null,
    ModificationCode? ModificationCode = null, EcfPaymentType PaymentType = EcfPaymentType.Cash,
    IReadOnlyList<FiscalPayment>? Payments = null);

/// <param name="QrUrl">What the printed QR code points to (per type, amount and DGII environment).</param>
/// <param name="SignedUtc">Signing moment: printed under the QR as "fecha y hora de firma" (DR time).</param>
/// <param name="SequenceExpiresOn">Expiry of the e-NCF range, printed on 31 and 33 only.</param>
/// <param name="Contingency">The DGII cannot be reached right now: the receipt carries the contingency legend.</param>
public sealed record IssuedDocument(Guid DocumentId, string ENcf, string SecurityCode, DateTime SignedUtc, string QrUrl,
    DateOnly? SequenceExpiresOn = null, bool Contingency = false);

/// <summary>Production: an e-CF of the eCF environment or a paper B receipt. Test: TesteCF or CerteCF. Simulated: no DGII behind it
/// (or a number Abastalia does not know).</summary>
public enum IssuedIn { Simulated, Test, Production }

/// <summary>Issues e-CF inside the caller's transaction: reserves the next e-NCF (no gaps), signs locally and queues it for the 72-hour submission.</summary>
public interface IFiscalDocuments
{
    Task<Result<IssuedDocument>> IssueAsync(DocumentToIssue document, CancellationToken ct);

    /// <summary>R9 #12: where a receipt number was issued (the trial starts only with a real sale).</summary>
    Task<IssuedIn> IssuedInAsync(string ncf, CancellationToken ct);

    /// <summary>e-NCF of documents issued in a DGII test environment (TesteCF, CerteCF) in the period: the 606/607 leave them out.</summary>
    Task<IReadOnlySet<string>> TestENcfsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>R8 #3: e-NCF signed in the period that were voided before sending (TK-205) or rejected by the DGII and are not replaced yet. They are
    /// not a valid receipt (the 608 lists them), so the 607 leaves them out and is not exported while there are any.
    /// R9 #18: also the B receipts of a contingency (paper NCF) not regularized yet.</summary>
    Task<IReadOnlySet<string>> UnreplacedENcfsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

/// <summary>One line of the DGII 608 (voided receipts), columns in the order of DGII's format: NCF, date of the receipt (AAAAMMDD in the TXT)
/// and type of void, a two-digit code of DGII's table (01 deterioro … 10 pérdida o hurto de talonarios).</summary>
public sealed record Report608Row(string Ncf, DateOnly DocumentDate, string VoidType);

/// <summary>TK-192: what the DGII 608 reports, from the e-NCF this store consumed without a valid receipt.</summary>
public interface IFiscalReports
{
    Task<IReadOnlyList<Report608Row>> Report608Async(DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>TK-222: what the cloud e-mails the owner about (the heartbeat carries it).</summary>
    Task<FiscalHealth> HealthAsync(CancellationToken ct);
}

/// <param name="RejectedWithoutReplacement">e-CF the DGII rejected in the last 30 days that have no replacement yet.</param>
/// <param name="OldestPendingUtc">Signing time of the oldest e-CF not yet delivered to the DGII (the deadline is 72 h); null when none waits.</param>
/// <param name="CertificateExpiresUtc">Expiry of the installed e-CF certificate; null without one.</param>
/// <param name="Mode">TK-238: the DGII environment the store sends to now (saved on screen, else appsettings): Simulated, TesteCF, CerteCF or eCF.</param>
public sealed record FiscalHealth(int RejectedWithoutReplacement, DateTime? OldestPendingUtc, DateTime? CertificateExpiresUtc, string Mode);

/// <summary>TK-225: the owner ticked (or unticked) a step of the DGII certification assistant (TK-155). The whole progress travels, so the newest
/// event alone says where the store is. <paramref name="DoneSteps"/> are step indexes (0 = the first); the last step is the authorization.</summary>
/// <param name="LastDoneUtc">When the newest step still ticked was ticked: since then the store is on the next one.</param>
public sealed record CertificationProgressedV1(IReadOnlyList<int> DoneSteps, int TotalSteps, DateTime? LastDoneUtc) : IntegrationEvent(Guid.Empty);

/// <summary>The steps of the DGII certification portal the store's assistant lists (EcfCertificationPanel.razor).</summary>
public static class CertificationSteps
{
    public const int Count = 14;
}

/// <summary>TK-256: one step of the certification with the help the owner reads in the app and the cloud assistant repeats.</summary>
/// <param name="Next">"¿Qué hago ahora?": the one thing to do while this is the current step.</param>
/// <param name="LinkUrl">A screen of the app (starts with "/") or a public page; null when the step needs none.</param>
public sealed record CertificationGuideStep(string Title, string Who, string Hint, string Help, string Next, string? LinkLabel = null, string? LinkUrl = null);

/// <summary>TK-256 (owner, D3: minimal accompaniment, the app and the assistant guide more): the DGII certification portal step by step
/// (docs/ecf-propio.md §8, docs/servicio-certificacion.md). The index is the step the server keeps; the texts are what the owner reads.</summary>
public static class CertificationGuide
{
    private const string Dgii = "https://dgii.gov.do";

    public static readonly IReadOnlyList<CertificationGuideStep> Steps =
    [
        new("Solicitud FI-GDF-016 en la OFV", "Dueño", "La DGII responde por el buzón con el acceso al portal",
            "Antes, tenga el RNC al día, el acceso a la Oficina Virtual (OFV), el alta de NCF y el certificado digital (.p12 o .pfx) del representante. En el formulario el software se declara \"adquirido a un proveedor externo\", con Abastalia como proveedor. Solo usted escribe su clave de la OFV: Abastalia nunca la pide.",
            "Entre a la OFV con su clave, envíe el formulario FI-GDF-016 y marque este paso. Luego revise el buzón de la OFV cada día hasta que llegue el acceso al portal.",
            "Oficina Virtual de la DGII", Dgii),
        new("Pruebas libres en Pre-certificación (TesteCF)", "Abastalia", "Ventas de prueba de cada tipo con el ambiente en TesteCF",
            "Sirven para ver que la firma y el envío funcionan antes de postularse. No cuentan para la DGII y no afectan su contabilidad.",
            "Importe el certificado en Configuración → Comprobantes, ponga el ambiente en Pre-certificación (TesteCF) y haga una venta de cada tipo que use (31, 32, 34).",
            "Configuración → Comprobantes", "/admin/settings?section=fiscal"),
        new("Postulación", "Dueño + Abastalia", "Nombre y versión del software, URL de la tienda; firme el XML aquí abajo",
            "El portal pide el nombre y la versión del software y las URL de recepción y aprobación de su tienda. Luego entrega un XML que se firma con su certificado.",
            "Llene la postulación en el portal, descargue el XML, fírmelo aquí abajo con \"Firmar archivo de la DGII\" y súbalo al portal."),
        new("Pruebas de datos e-CF (set de pruebas)", "Abastalia", "Suba el Excel del portal",
            "El portal entrega un Excel con los comprobantes que la DGII quiere ver. Abastalia arma cada uno con los mismos campos y en el mismo orden, lo firma y lo envía. Si la DGII rechaza uno, el set se reinicia.",
            "Descargue el Excel del portal, súbalo en \"Set de pruebas\" aquí abajo y toque \"Enviar y consultar\" hasta que todos digan Aceptado."),
        new("Pruebas de aprobaciones comerciales", "Abastalia", "Pendiente: llega con el servicio de la nube (TK-152)",
            "La DGII prueba que su tienda puede aceptar o rechazar comprobantes de sus suplidores. Lo hace la nube de Abastalia con el certificado de la tienda.",
            "Active el receptor en Comprobantes electrónicos (lo pide una sola vez) y siga las instrucciones del portal para este paso."),
        new("Simulación con datos reales", "Abastalia", "Ventas normales con el ambiente en CerteCF",
            "Con el ambiente en Certificación (CerteCF), venda como un día normal: cada tipo de comprobante que use. Son ventas de prueba para la DGII.",
            "Cambie el ambiente a Certificación (CerteCF) en Configuración → Comprobantes y haga ventas de cada tipo; vea el resultado en \"Simulación\" aquí abajo.",
            "Configuración → Comprobantes", "/admin/settings?section=fiscal"),
        new("Subir el PDF de cada comprobante de la simulación", "Dueño", "Descargue cada uno en \"Simulación\" aquí abajo y guárdelo como PDF",
            "La DGII revisa que la factura impresa tenga el código de seguridad, el QR y los datos obligatorios. Cada PDF puede pesar hasta 10 MB.",
            "En \"Simulación\" aquí abajo, abra la representación impresa de cada comprobante, use \"Imprimir o guardar como PDF\" y súbalos al portal."),
        new("Confirmar las URL de prueba", "Dueño", "Recepción y aprobación comercial de la tienda. Deje esta PC encendida y con internet",
            "Son las direcciones donde la DGII le envía comprobantes y aprobaciones. Las muestra la tarjeta del receptor en Comprobantes electrónicos.",
            "Copie las URL de prueba de la tarjeta del receptor, confírmelas en el portal y deje esta PC encendida y con internet."),
        new("Comunicación: la DGII envía e-CF y espera el acuse", "Nube de Abastalia + esta PC", "Esta PC firma cada acuse: manténgala encendida durante la prueba",
            "La DGII manda comprobantes de prueba a su tienda y espera el acuse de recibo firmado. La nube de Abastalia los recibe y esta PC los firma.",
            "No apague esta PC ni la desconecte de internet hasta que el portal marque este paso como completado."),
        new("Comunicación: aprobaciones comerciales", "Esta PC", "Apruebe o rechace en Compras → Recibidos; se firma y se envía desde aquí",
            "La DGII manda comprobantes que su tienda debe aprobar o rechazar comercialmente, como haría con un suplidor.",
            "Abra Compras → Recibidos, apruebe o rechace cada comprobante que llegó de la DGII y espere a que el portal lo confirme.",
            "Compras → Recibidos", "/admin/purchasing/received"),
        new("URL de producción", "Dueño", "Las mismas URL, ambiente de producción. Esta PC encendida y con internet",
            "Las URL de producción son las mismas de prueba, en el ambiente de producción.",
            "Registre en el portal las URL de producción que muestra la tarjeta del receptor."),
        new("Declaración jurada", "Dueño + Abastalia", "Descárguela, fírmela aquí abajo y súbala",
            "Es una declaración del contribuyente: léala usted antes de firmarla. Abastalia solo la firma con su certificado cuando usted lo decide.",
            "Descargue la declaración jurada del portal, léala, fírmela aquí abajo con \"Firmar archivo de la DGII\" y súbala."),
        new("Verificación del RNC por la DGII", "DGII", "Esperar la respuesta",
            "La DGII revisa que el RNC esté al día. No hay nada que hacer en Abastalia; puede tardar unos días.",
            "Revise el buzón de la OFV. Si la DGII pide algo (una declaración pendiente, por ejemplo), resuélvalo con su contador.",
            "Oficina Virtual de la DGII", Dgii),
        new("Autorización: secuencias de producción y ambiente eCF", "Dueño", "Pida las secuencias en la OFV y cárguelas en Configuración",
            "Con la autorización se habilita el menú de facturación electrónica en la OFV. Allí pide las secuencias e-NCF de producción.",
            "Pida las secuencias en la OFV, cárguelas en Configuración → Comprobantes y cambie el ambiente a Producción (eCF). Revise la primera venta real.",
            "Configuración → Comprobantes", "/admin/settings?section=fiscal"),
    ];
}

/// <summary>R9 #20: the e-CF certificate installed, so the business RNC is checked against it when it changes, not only at import.</summary>
public interface IEcfCertificateHolder
{
    /// <summary>RNC or cédula of the holder of the installed certificate; null without a certificate or when it names none.</summary>
    string? TaxId { get; }

    /// <summary>R8 #4: a certificate that names another RNC (or another cédula, for a business that is a person) does not fit: the DGII would reject
    /// everything signed with it. A cédula on the certificate of a company (9-digit RNC) is the delegated signer's and fits.</summary>
    static bool Fits(string? taxId, string? rnc) => taxId is null || rnc is null || taxId.Length != rnc.Length || taxId == rnc;
}

/// <summary>The DGII taxpayer registry the owner loaded (DGII_RNC.TXT): names by RNC or cédula.</summary>
public interface ITaxpayerRegistry
{
    /// <summary>The registered name (razón social), or null when the RNC is not in the loaded registry.</summary>
    Task<string?> NameAsync(string rnc, CancellationToken ct);
}

/// <summary>TK-252: a taxpayer as the DGII public consultation shows it today.</summary>
/// <param name="Status">As the DGII writes it: ACTIVO, SUSPENDIDO, DADO DE BAJA…</param>
public sealed record DgiiTaxpayer(string Rnc, string Name, string? TradeName, string Status, string? Category, string? PaymentRegime, bool ElectronicIssuer)
{
    public bool IsActive => Status.Equals("ACTIVO", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Unavailable: no answer from the DGII (offline, blocked, the store's lookup limit reached, a page Abastalia cannot read).</summary>
public enum DgiiAnswerKind { Found, NotRegistered, Unavailable }

public sealed record RncLookup(DgiiAnswerKind Kind, DgiiTaxpayer? Taxpayer = null);

/// <summary>Unknown: the DGII did not answer, or the receipt is an e-NCF (its public consultation needs the security code).</summary>
public enum NcfValidity { Valid, Expired, NotMatching, Unknown }

/// <param name="ValidUntil">Last day the DGII authorized that sequence ("Válido hasta").</param>
public sealed record NcfCheck(NcfValidity Validity, DateOnly? ValidUntil = null, string? IssuerName = null, string? ReceiptType = null)
{
    public static readonly NcfCheck Unknown = new(NcfValidity.Unknown);

    /// <summary>The DGII says the receipt does not belong to that RNC, or its sequence had expired by <paramref name="invoiceDate"/>.
    /// An unknown answer never refuses: the DGII being down must not stop a purchase.</summary>
    public bool RefusedOn(DateOnly invoiceDate) =>
        Validity == NcfValidity.NotMatching || (Validity == NcfValidity.Expired && ValidUntil is { } until && until < invoiceDate);
}

/// <summary>TK-252: the DGII public consultation pages (RNC and NCF), asked from the store PC (ADR-0011) with a cache and a store-wide limit.</summary>
public interface IDgiiPublicLookup
{
    Task<RncLookup> RncAsync(string rnc, CancellationToken ct);

    /// <summary>Is <paramref name="ncf"/> a receipt the DGII authorized to <paramref name="issuerRnc"/>?</summary>
    Task<NcfCheck> NcfAsync(string issuerRnc, string ncf, CancellationToken ct);
}

/// <summary>The legend the receipt of an e-CF issued in contingency carries (Decreto 587-24; DGII "Representación Impresa").</summary>
public static class EcfReceiptTexts
{
    public const string ContingencyLegend =
        "e-CF emitido en modalidad de Contingencia, el cual podrá ser consultado para su validez fiscal, a partir de las setenta y dos (72) horas.";
}

public sealed record FiscalDocumentIssuedV1(Guid DocumentId, string ENcf, EcfType Type, Guid ReferenceId, decimal Total, decimal Itbis) : IntegrationEvent(DocumentId);

/// <summary>TK-147: the DGII rejected <paramref name="RejectedENcf"/> and a new e-CF with the same data replaces it. The sale (or void, or return)
/// with the rejected number now points to the new one, so its receipt and the 607 use the valid e-CF.</summary>
public sealed record FiscalDocumentReplacedV1(Guid DocumentId, string RejectedENcf, string ENcf, EcfType Type, Guid ReferenceId, string SecurityCode,
    string QrUrl, DateTime SignedUtc, DateOnly? SequenceExpiresOn) : IntegrationEvent(DocumentId);

/// <summary>TK-156: an e-CF issued while the store's clock was off (unsigned, receipt without security code nor QR) was signed once the clock was
/// fixed. The sale with that e-NCF takes the code, the QR and the signing time, so a reprint shows them.</summary>
public sealed record FiscalDocumentSignedV1(Guid DocumentId, string ENcf, Guid ReferenceId, string SecurityCode, string QrUrl, DateTime SignedUtc)
    : IntegrationEvent(DocumentId);

/// <summary>TK-246: the store signed (and tried to send) its commercial approval or rejection of a received e-CF. The cloud only shows it.</summary>
/// <param name="CloudId">The received document's id in the cloud.</param>
/// <param name="Approval">Approved or Rejected.</param>
/// <param name="XmlGzip">The signed ACECF (gzip), kept in the cloud with the received document.</param>
public sealed record CommercialApprovalRecordedV1(Guid ReceivedId, long CloudId, string Approval, string? Reason, DateTime ApprovalUtc, DateTime? DeliveredUtc,
    DateTime? DgiiUtc, string? Message, byte[] XmlGzip) : IntegrationEvent(ReceivedId);

/// <summary>TK-240: one line of a supplier's e-CF as it reads for a purchase: the unit price without ITBIS (the e-CF may carry it included).</summary>
/// <param name="ItbisPercent">18, 16 or 0; null when the line is exempt.</param>
public sealed record SupplierEcfLine(string Description, decimal Quantity, decimal UnitCost, decimal? ItbisPercent, string? Code);

/// <summary>TK-240: an e-CF a supplier issued to this business (received through the cloud, TK-154, or a file the person uploaded), read from its XML.</summary>
/// <param name="Environment">The DGII environment it was received in (<c>ecf</c> is production); null for an uploaded file.</param>
public sealed record SupplierEcf(string IssuerRnc, string IssuerName, string ENcf, int Type, DateOnly IssuedOn, decimal Subtotal, decimal Itbis, decimal Total,
    IReadOnlyList<SupplierEcfLine> Lines, string? Environment);

/// <summary>TK-240: e-CF of suppliers for Compras: the received ones (TK-154) and the reading of an e-CF XML file.</summary>
public interface ISupplierEcfs
{
    /// <summary>A received e-CF by its id at the store; null when it does not exist.</summary>
    Task<SupplierEcf?> ReceivedAsync(Guid id, CancellationToken ct);

    /// <summary>The newest e-CF received in production from that supplier with that e-NCF; null when none arrived.</summary>
    Task<SupplierEcf?> ReceivedAsync(string issuerRnc, string encf, CancellationToken ct);

    /// <summary>An e-CF XML (31 to 34, 41, 43–47); null when it is not one.</summary>
    SupplierEcf? Read(string xml);
}
