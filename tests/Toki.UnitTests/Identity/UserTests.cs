using Shouldly;
using Toki.Identity.Domain;

namespace Toki.UnitTests.Identity;

public sealed class UserTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static User NewUser()
    {
        var u = new User { Id = Guid.NewGuid(), Code = "01" };
        u.ChangePin("1234");
        return u;
    }

    [Fact]
    public void The_right_pin_resets_the_attempts()
    {
        var u = NewUser();
        u.TryPin("0000", Now).ShouldBe(PinResult.Incorrect);
        u.TryPin("1234", Now).ShouldBe(PinResult.Correct);
        u.FailedAttempts.ShouldBe(0);
    }

    [Fact]
    public void Five_failures_lock_for_five_minutes_and_then_it_doubles()
    {
        var u = NewUser();
        for (var i = 0; i < 5; i++) u.TryPin("0000", Now);
        u.LockedUntilUtc.ShouldBe(Now.AddMinutes(5));
        u.TryPin("1234", Now.AddMinutes(4)).ShouldBe(PinResult.Locked);

        u.TryPin("0000", Now.AddMinutes(6)).ShouldBe(PinResult.Incorrect);
        u.LockedUntilUtc.ShouldBe(Now.AddMinutes(16));
    }

    [Fact]
    public void The_lockout_never_exceeds_one_hour()
    {
        var u = NewUser();
        u.FailedAttempts = 20;
        u.TryPin("0000", Now);
        u.LockedUntilUtc.ShouldBe(Now.AddMinutes(60));
    }

    [Fact]
    public void An_inactive_user_cannot_sign_in()
    {
        var u = NewUser();
        u.IsActive = false;
        u.TryPin("1234", Now).ShouldBe(PinResult.Inactive);
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345678", true)]
    [InlineData("123", false)]
    [InlineData("123456789", false)]
    [InlineData("12a4", false)]
    [InlineData(null, false)]
    public void Pin_format(string? pin, bool valid) => Pin.IsValid(pin).ShouldBe(valid);
}
