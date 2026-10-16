using Microsoft.AspNetCore.Identity;
using Shouldly;
using Abastalia.Identity.Domain;

namespace Abastalia.UnitTests.Identity;

public sealed class UserTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Pepper = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    private static User NewUser()
    {
        var u = new User { Id = Guid.NewGuid(), Code = "01" };
        u.ChangePin("1234", Pepper);
        return u;
    }

    [Fact]
    public void A_right_pin_does_not_wipe_the_wrong_ones_until_an_hour_without_failures()
    {
        // R11 A4: guessing between two right PINs used to start the count again each time.
        var u = NewUser();
        for (var i = 0; i < 4; i++) u.TryPin("0000", Now).ShouldBe(PinResult.Incorrect);
        u.TryPin("1234", Now).ShouldBe(PinResult.Correct);
        u.FailedAttempts.ShouldBe(4);
        u.TryPin("0000", Now).ShouldBe(PinResult.Incorrect);
        u.LockedUntilUtc.ShouldBe(Now.AddMinutes(5));

        var later = NewUser();
        later.TryPin("0000", Now);
        later.TryPin("1234", Now.Add(User.FailureMemory)).ShouldBe(PinResult.Correct);
        later.FailedAttempts.ShouldBe(0);
    }

    [Fact]
    public void Five_failures_lock_for_five_minutes_and_then_it_doubles()
    {
        var u = NewUser();
        for (var i = 0; i < 5; i++) u.TryPin("0000", Now, Pepper);
        u.LockedUntilUtc.ShouldBe(Now.AddMinutes(5));
        u.TryPin("1234", Now.AddMinutes(4), Pepper).ShouldBe(PinResult.Locked);

        u.TryPin("0000", Now.AddMinutes(6), Pepper).ShouldBe(PinResult.Incorrect);
        u.LockedUntilUtc.ShouldBe(Now.AddMinutes(16));
    }

    [Fact]
    public void The_lockout_never_exceeds_one_hour()
    {
        var u = NewUser();
        u.FailedAttempts = 20;
        u.LastFailedUtc = Now;
        u.TryPin("0000", Now, Pepper);
        u.LockedUntilUtc.ShouldBe(Now.AddMinutes(60));
    }

    [Fact]
    public void An_inactive_user_cannot_sign_in()
    {
        var u = NewUser();
        u.IsActive = false;
        u.TryPin("1234", Now, Pepper).ShouldBe(PinResult.Inactive);
    }

    [Fact]
    public void A_pin_hash_is_useless_without_the_server_pepper()
    {
        // R11 A3: a PinHash read from a backup cannot be tried offline: the PIN is keyed with a secret that is not in the database.
        var u = NewUser();
        u.PinHash.ShouldStartWith("p1:");
        new PasswordHasher<object>().VerifyHashedPassword(null!, u.PinHash.Split(':', 3)[2], "1234").ShouldBe(PasswordVerificationResult.Failed);
        Pin.Verify(u.PinHash, "1234", [.. Enumerable.Repeat((byte)7, 32)]).Ok.ShouldBeFalse();
        Pin.IsForeign(u.PinHash, [.. Enumerable.Repeat((byte)7, 32)]).ShouldBeTrue();
        Pin.IsForeign(u.PinHash, Pepper).ShouldBeFalse();
    }

    [Fact]
    public void A_hash_from_before_the_pepper_still_works_and_is_replaced_at_the_next_right_pin()
    {
        var u = new User { Id = Guid.NewGuid(), Code = "02", PinHash = new PasswordHasher<object>().HashPassword(null!, "4321") };
        u.TryPin("4321", Now, Pepper).ShouldBe(PinResult.Correct);
        u.PinHash.ShouldStartWith("p1:");
        u.TryPin("4321", Now, Pepper).ShouldBe(PinResult.Correct);
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

internal static class PepperedPins
{
    private static readonly byte[] Pepper = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

    public static PinResult TryPin(this User u, string pin, DateTime now) => u.TryPin(pin, now, Pepper);
}
