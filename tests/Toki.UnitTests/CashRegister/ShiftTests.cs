using Shouldly;
using Toki.CashRegister.Contracts;
using Toki.CashRegister.Domain;

namespace Toki.UnitTests.CashRegister;

public sealed class ShiftTests
{
    private static CashMovement M(CashMovementType type, decimal amount, PaymentMethod method = PaymentMethod.Cash, Guid? sale = null) =>
        new() { Type = type, Amount = amount, Method = method, ReferenceId = sale };

    [Fact]
    public void Expected_cash_follows_the_balance_formula()
    {
        var s1 = Guid.NewGuid();
        CashMovement[] movements =
        [
            M(CashMovementType.SalePayment, 1000, sale: s1),
            M(CashMovementType.SalePayment, 500, PaymentMethod.Card, Guid.NewGuid()),
            M(CashMovementType.CashIn, 200),
            M(CashMovementType.Withdrawal, 300),
            M(CashMovementType.Expense, 50),
            M(CashMovementType.Refund, 100),
            M(CashMovementType.SalePaymentVoid, 1000, sale: s1),
        ];
        // 2000 + 1000 + 200 − 300 − 50 − 100 − 1000; card money never counts as cash.
        ShiftBalance.ExpectedCash(2000, movements).ShouldBe(1750m);
    }

    [Fact]
    public void The_count_multiplies_each_denomination()
    {
        DenominationCount[] count = [new(2000, 1), new(500, 3), new(25, 4), new(1, 7)];
        ShiftBalance.Counted(count).ShouldBe(3607m);
    }

    [Fact]
    public void The_Z_report_splits_methods_and_counts_voided_sales()
    {
        var voided = Guid.NewGuid();
        var shift = new Shift { Id = Guid.NewGuid(), CashierName = "Ana", OpeningFloat = 1000 };
        CashMovement[] movements =
        [
            M(CashMovementType.SalePayment, 300, sale: voided),
            M(CashMovementType.SalePayment, 200, sale: Guid.NewGuid()),
            M(CashMovementType.SalePayment, 450, PaymentMethod.Card, Guid.NewGuid()),
            M(CashMovementType.SalePaymentVoid, 300, sale: voided),
        ];
        var z = ZReport.Build(shift, "Caja 1", movements, null, "#7 ABCDEF12");

        z.SaleCount.ShouldBe(2);
        z.VoidedCount.ShouldBe(1);
        z.ByMethod.Single(p => p.Method == PaymentMethod.Cash).Net.ShouldBe(200m);
        z.ByMethod.Single(p => p.Method == PaymentMethod.Card).Net.ShouldBe(450m);
        z.ExpectedCash.ShouldBe(1200m);
    }
}
