using Toki.CashRegister.Contracts;
using Toki.SharedKernel;

namespace Toki.CashRegister.Domain;

internal sealed class Register
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>The paired device this register belongs to (set on its first shift). Its shifts are only operated from there.</summary>
    public Guid? DeviceId { get; set; }
}

internal enum ShiftStatus { Open, Closed }

/// <summary>A shift: one physical register + one cashier, from the opening float to the blind count.</summary>
internal sealed class Shift
{
    public Guid Id { get; set; }
    public Guid RegisterId { get; set; }
    public Guid CashierId { get; set; }
    public string CashierName { get; set; } = "";
    public DateTime OpenedUtc { get; set; }
    public decimal OpeningFloat { get; set; }
    public ShiftStatus Status { get; set; }
    public DateTime? ClosedUtc { get; set; }
    public decimal? ExpectedCash { get; set; }
    public decimal? CountedCash { get; set; }
    public decimal? Difference { get; set; }
    public Guid? ClosingSupervisorId { get; set; }
    /// <summary>JSON of the denomination count.</summary>
    public string? CashCount { get; set; }
    public int? ZNumber { get; set; }
    /// <summary>Head of the audit chain at the closing, as printed on the Z.</summary>
    public string? AuditChainHead { get; set; }
    public int Version { get; set; }
}

/// <summary>Rounding: centavos the store keeps because change is given in whole pesos (there are no coins below RD$ 1).</summary>
internal enum CashMovementType { SalePayment, SalePaymentVoid, Refund, CashIn, Withdrawal, Expense, CustomerPayment, Rounding, CustomerPaymentVoid }

internal enum MovementReason { ExtraChange, CashPickup, BankDeposit, SupplierPayment, PettyExpense, Other }

/// <summary>Cash-book row. Never edited: a mistake is fixed with another movement.</summary>
internal sealed class CashMovement
{
    public long Sequence { get; set; }
    public Guid Id { get; set; }
    public Guid ShiftId { get; set; }
    public CashMovementType Type { get; set; }
    public PaymentMethod Method { get; set; }
    /// <summary>Always positive; the type says whether money comes in or goes out.</summary>
    public decimal Amount { get; set; }
    public MovementReason? Reason { get; set; }
    public string? Note { get; set; }
    public Guid? ReferenceId { get; set; }
    public string? Reference { get; set; }
    public Guid? UserId { get; set; }
    public Guid? SupervisorId { get; set; }
    public DateTime DateUtc { get; set; }

    public bool IsInflow => Type is CashMovementType.SalePayment or CashMovementType.CashIn or CashMovementType.CustomerPayment or CashMovementType.Rounding;
}

/// <summary>Dominican peso bills and coins for the closing count.</summary>
internal static class Denominations
{
    public static readonly IReadOnlyList<decimal> Dop = [2000, 1000, 500, 200, 100, 50, 25, 10, 5, 1];
}

internal sealed record DenominationCount(decimal Denomination, int Quantity);

/// <summary>Totals of a shift: the expected cash formula and the Z report lines.</summary>
internal static class ShiftBalance
{
    /// <summary>Expected cash = float + cash sales − cash refunds − voided cash + cash in − withdrawals − petty expenses.</summary>
    public static decimal ExpectedCash(decimal openingFloat, IEnumerable<CashMovement> movements) =>
        openingFloat + movements.Where(m => m.Method == PaymentMethod.Cash).Sum(m => m.IsInflow ? m.Amount : -m.Amount);

    public static decimal Counted(IEnumerable<DenominationCount> count) => count.Sum(c => c.Denomination * c.Quantity);
}

internal static class CashRegisterErrors
{
    public static readonly Error RegisterNotFound = Error.NotFound("CashRegister.RegisterNotFound", "La caja no existe o está desactivada.");
    public static readonly Error ShiftNotFound = Error.NotFound("CashRegister.ShiftNotFound", "El turno no existe.");
    public static readonly Error RegisterInUse = Error.Conflict("CashRegister.RegisterInUse", "Esta caja ya tiene un turno abierto.");
    public static readonly Error CashierHasShift = Error.Conflict("CashRegister.CashierHasShift", "Este cajero ya tiene un turno abierto en otra caja.");
    public static readonly Error ShiftClosed = Error.Conflict("CashRegister.ShiftClosed", "El turno ya está cerrado.");
    public static readonly Error NotYourShift = Error.Forbidden("CashRegister.NotYourShift", "Este turno es de otro cajero.");
    public static readonly Error OtherDevice = Error.Forbidden("CashRegister.OtherDevice", "Esta caja está asignada a otro equipo.");
    public static readonly Error InvalidDenomination = Error.Validation("CashRegister.InvalidDenomination", "Esa denominación no existe.");
}
