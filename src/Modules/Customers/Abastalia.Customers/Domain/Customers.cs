using Abastalia.CashRegister.Contracts;
using Abastalia.Customers.Contracts;
using Abastalia.SharedKernel;

namespace Abastalia.Customers.Domain;

internal sealed class Customer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? TaxId { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    /// <summary>Asks for a credit fiscal receipt (31) by default.</summary>
    public bool FiscalCredit { get; set; }
    public PriceLevel PriceLevel { get; set; }
    /// <summary>Most the customer may owe. Zero means no credit.</summary>
    public decimal CreditLimit { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedUtc { get; set; }
    /// <summary>Bumped to lock the row: limit checks, payments and balance use run one at a time per customer.</summary>
    public int Version { get; set; }
}

/// <summary>A credit sale. Its balance is Amount minus the live payments applied to it. Aging uses its date.</summary>
internal sealed class OnAccountCharge
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid SaleId { get; set; }
    public string Document { get; set; } = "";
    public DateTime DateUtc { get; set; }
    public decimal Amount { get; set; }
    public bool IsVoided { get; set; }
    public DateTime? VoidedUtc { get; set; }
    /// <summary>What had been paid on it when the sale was voided: that money went to other charges or to credit balance.</summary>
    public decimal PaidAtVoid { get; set; }
}

/// <summary>Register: paid at the register. Return: a return credited to the account. SaleVoid: what was paid on a voided sale, moved to other charges.</summary>
internal enum CustomerPaymentSource { Register, Return, SaleVoid }

internal sealed class CustomerPayment
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public DateTime DateUtc { get; set; }
    /// <summary>What goes to the debt (cash centavos kept as rounding are not part of it).</summary>
    public decimal Amount { get; set; }
    public CustomerPaymentSource Source { get; set; }
    public PaymentMethod? Method { get; set; }
    public Guid? ShiftId { get; set; }
    public Guid? ReferenceId { get; set; }
    public string? Reference { get; set; }
    public Guid? UserId { get; set; }
    /// <summary>A voided payment stays for history; its applications no longer count.</summary>
    public bool IsVoided { get; set; }
    public DateTime? VoidedUtc { get; set; }
    public string? VoidReason { get; set; }
    public List<CustomerPaymentApplication> Applications { get; set; } = [];
}

internal sealed class CustomerPaymentApplication
{
    public Guid CustomerPaymentId { get; set; }
    public Guid ChargeId { get; set; }
    public decimal Amount { get; set; }
}

internal enum CreditBalanceMovementType { Return, UsedInSale, UseVoided, VoidedChargePaid, Deposit, DepositRefund }

/// <summary>Credit balance ledger: positive adds, negative uses. The balance is always the sum.</summary>
internal sealed class CreditBalanceMovement
{
    public long Sequence { get; set; }
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public CreditBalanceMovementType Type { get; set; }
    public decimal Amount { get; set; }
    public Guid ReferenceId { get; set; }
    public DateTime DateUtc { get; set; }
}

internal static class CustomersErrors
{
    public static readonly Error CustomerNotFound = Error.NotFound("Customers.CustomerNotFound", "El cliente no existe o está desactivado.");
    public static readonly Error InsufficientCreditBalance = Error.Conflict("Customers.InsufficientCreditBalance", "El cliente no tiene ese saldo a favor.");
    public static readonly Error PaymentExceedsDebt = Error.Validation("Customers.PaymentExceedsDebt", "El abono es mayor que la deuda del cliente.");
    public static readonly Error DuplicateTaxId = Error.Conflict("Customers.DuplicateTaxId", "Ya hay un cliente con ese RNC o cédula.");
    public static readonly Error CreditPermissionRequired = Error.Forbidden("Customers.CreditPermissionRequired",
        "Dar crédito, precio mayorista o reactivar un cliente necesita el permiso de fiado.");
    public static readonly Error PaymentNotFound = Error.NotFound("Customers.PaymentNotFound", "El abono no existe o no se cobró en caja.");
    public static readonly Error PaymentAlreadyVoided = Error.Conflict("Customers.PaymentAlreadyVoided", "El abono ya está anulado.");
    public static readonly Error PaymentOnVoidedSale = Error.Conflict("Customers.PaymentOnVoidedSale",
        "Ese abono pagó una venta que ya se anuló; su dinero ya se movió. Corrija con un nuevo movimiento.");

    public static Error CreditLimitExceeded(decimal available) =>
        Error.Forbidden("Customers.CreditLimitExceeded", $"El fiado pasa del límite del cliente (disponible RD$ {available:N2}). Necesita un supervisor.");
}
