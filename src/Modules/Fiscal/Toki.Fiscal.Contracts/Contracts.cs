using Toki.SharedKernel;

namespace Toki.Fiscal.Contracts;

/// <summary>Electronic receipt types (e-CF) used in V1.</summary>
public enum EcfType { FiscalCredit31 = 31, Consumer32 = 32, DebitNote33 = 33, CreditNote34 = 34 }

public sealed record FiscalLine(string Description, decimal Quantity, decimal UnitPrice, decimal Discount, ItbisRate Rate, decimal Itbis, decimal Total);

/// <summary>DGII modification code of a credit note: 1 cancels the whole document (a void), 3 corrects amounts (a return).</summary>
public enum ModificationCode { Void = 1, AmountCorrection = 3 }

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

/// <param name="QrUrl">What the printed QR code points to.</param>
public sealed record IssuedDocument(Guid DocumentId, string ENcf, string SecurityCode, DateTime SignedUtc, string QrUrl);

/// <summary>Issues e-CF inside the caller's transaction: reserves the next e-NCF (no gaps), signs locally and queues it for the 72-hour submission.</summary>
public interface IFiscalDocuments
{
    Task<Result<IssuedDocument>> IssueAsync(DocumentToIssue document, CancellationToken ct);
}

public sealed record FiscalDocumentIssuedV1(Guid DocumentId, string ENcf, EcfType Type, Guid ReferenceId, decimal Total, decimal Itbis) : IntegrationEvent(DocumentId);
