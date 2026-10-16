using Shouldly;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.SharedKernel;

namespace Abastalia.UnitTests.SharedKernel;

public sealed class SharedKernelTests
{
    [Theory]
    [InlineData(1.005, 1.01)]
    [InlineData(1.004, 1.00)]
    [InlineData(-1.005, -1.01)]
    public void Money_rounds_away_from_zero(decimal amount, decimal expected) => Money.Round(amount).ShouldBe(expected);

    [Fact]
    public void The_business_day_is_Santo_Domingo_time()
    {
        // 02:30 UTC on the 16th is still 22:30 on the 15th in the Dominican Republic.
        BusinessDay.Of(new DateTime(2026, 10, 16, 2, 30, 0, DateTimeKind.Utc)).ShouldBe(new DateOnly(2026, 10, 15));
        var (from, to) = BusinessDay.UtcRange(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15));
        from.ShouldBe(new DateTime(2026, 10, 15, 4, 0, 0));
        from.Kind.ShouldBe(DateTimeKind.Utc);
        to.Kind.ShouldBe(DateTimeKind.Utc);
        to.ShouldBe(new DateTime(2026, 10, 16, 4, 0, 0));
    }

    [Fact]
    public void The_audit_chain_detects_a_change()
    {
        var r = new AuditRecord { Sequence = 1, Id = Guid.NewGuid(), DateUtc = DateTime.UtcNow, Action = "a", ResourceType = "t", Data = "{}", PreviousHash = new byte[32] };
        r.Hash = AuditChain.Hash(r);
        var copy = AuditChain.Hash(r);
        copy.ShouldBe(r.Hash);

        r.Data = "{\"amount\":1}";
        AuditChain.Hash(r).ShouldNotBe(copy);
    }

    [Theory]
    [InlineData(118.00, ItbisRate.General18, 18.00)]
    [InlineData(116.00, ItbisRate.Reduced16, 16.00)]
    [InlineData(100.00, ItbisRate.Exempt, 0)]
    [InlineData(100.00, ItbisRate.Zero, 0)]
    [InlineData(185.00, ItbisRate.General18, 28.22)]
    public void Included_itbis_is_taken_out_of_the_price(decimal amount, ItbisRate rate, decimal itbis) => Itbis.Included(amount, rate).ShouldBe(itbis);

    [Theory]
    [InlineData("131245678", true)]
    [InlineData("00112345678", true)]
    [InlineData("1312456", false)]
    [InlineData("13124567A", false)]
    public void Rnc_or_cedula_format(string value, bool valid) => Rnc.IsValid(value).ShouldBe(valid);

    [Fact]
    public void A_payment_goes_to_the_oldest_invoices_first_without_exceeding_their_balance()
    {
        var open = new[] { ("A", 100m), ("B", 0m), ("C", 250m), ("D", 50m) };
        Allocation.InOrder(300m, open).ShouldBe([("A", 100m), ("C", 200m)]);
        Allocation.InOrder(1000m, open).Sum(x => x.Amount).ShouldBe(400m); // the rest (600) is left to the caller
        Allocation.InOrder(0m, open).ShouldBeEmpty();
    }

    [Fact]
    public void A_failed_result_gives_no_value()
    {
        Result<int> r = Error.NotFound("X", "no");
        r.IsFailure.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => r.Value);
    }
}
