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

/// <summary>Issues e-CF inside the caller's transaction: reserves the next e-NCF (no gaps), signs locally and queues it for the 72-hour submission.</summary>
public interface IFiscalDocuments
{
    Task<Result<IssuedDocument>> IssueAsync(DocumentToIssue document, CancellationToken ct);

    /// <summary>e-NCF of documents issued in a DGII test environment (TesteCF, CerteCF) in the period: the 606/607 leave them out.</summary>
    Task<IReadOnlySet<string>> TestENcfsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    /// <summary>R8 #3: e-NCF signed in the period that were voided before sending (TK-205) or rejected by the DGII and are not replaced yet. They are
    /// not a valid receipt (the 608 lists them), so the 607 leaves them out and is not exported while there are any.</summary>
    Task<IReadOnlySet<string>> UnreplacedENcfsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

/// <summary>One line of the DGII 608 (voided receipts), columns in the order of DGII's format: NCF, date of the receipt (AAAAMMDD in the TXT)
/// and type of void, a two-digit code of DGII's table (01 deterioro … 10 pérdida o hurto de talonarios).</summary>
public sealed record Report608Row(string Ncf, DateOnly DocumentDate, string VoidType);

/// <summary>TK-192: what the DGII 608 reports, from the e-NCF this store consumed without a valid receipt.</summary>
public interface IFiscalReports
{
    Task<IReadOnlyList<Report608Row>> Report608Async(DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>The DGII taxpayer registry the owner loaded (DGII_RNC.TXT): names by RNC or cédula.</summary>
public interface ITaxpayerRegistry
{
    /// <summary>The registered name (razón social), or null when the RNC is not in the loaded registry.</summary>
    Task<string?> NameAsync(string rnc, CancellationToken ct);
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
