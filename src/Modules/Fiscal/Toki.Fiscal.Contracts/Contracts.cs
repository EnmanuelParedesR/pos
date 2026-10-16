using Toki.SharedKernel;

namespace Toki.Fiscal.Contracts;

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
/// (or a number Toki does not know).</summary>
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
public sealed record FiscalHealth(int RejectedWithoutReplacement, DateTime? OldestPendingUtc, DateTime? CertificateExpiresUtc);

/// <summary>TK-225: the owner ticked (or unticked) a step of the DGII certification assistant (TK-155). The whole progress travels, so the newest
/// event alone says where the store is. <paramref name="DoneSteps"/> are step indexes (0 = the first); the last step is the authorization.</summary>
/// <param name="LastDoneUtc">When the newest step still ticked was ticked: since then the store is on the next one.</param>
public sealed record CertificationProgressedV1(IReadOnlyList<int> DoneSteps, int TotalSteps, DateTime? LastDoneUtc) : IntegrationEvent(Guid.Empty);

/// <summary>The steps of the DGII certification portal the store's assistant lists (EcfCertificationPanel.razor).</summary>
public static class CertificationSteps
{
    public const int Count = 14;
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

/// <summary>Unavailable: no answer from the DGII (offline, blocked, the store's lookup limit reached, a page Toki cannot read).</summary>
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
