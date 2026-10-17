using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Abastalia.Host.Cloud.Platform;

namespace Abastalia.Cloud.Tests;

/// <summary>R12 #2–#5 (backlog/reviews/R12-nube-arreglos.md): the second step of the panel accounts.</summary>
public sealed class R12TwoFactorTests(MailingCloudApp app) : IClassFixture<MailingCloudApp>
{
    private static async Task<(string Key, string[] Codes)> EnableAsync(Owner who)
    {
        var key = (await (await who.Client.PostAsJsonAsync("/api/auth/manage/2fa", new { })).Json()).GetProperty("sharedKey").GetString()!;
        var on = await (await who.Client.PostAsJsonAsync("/api/auth/manage/2fa", new { enable = true, twoFactorCode = Totp.Code(key) })).Json();
        return (key, on.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToArray());
    }

    private Task<HttpResponseMessage> LoginAsync(Owner who, string? twoFactorCode = null, string? twoFactorRecoveryCode = null) =>
        app.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = who.Email, password = CloudApp.Password, twoFactorCode, twoFactorRecoveryCode });

    private Task<string?> TokenAsync(Owner who, string name) => app.InDbAsync(db =>
        db.UserTokens.Where(t => t.UserId == db.Users.Single(u => u.Email == who.Email).Id && t.Name == name).Select(t => t.Value).SingleOrDefaultAsync());

    [Fact]
    public async Task R12_2_wrong_codes_on_the_security_page_count_and_lock_it()
    {
        var owner = await app.OwnerAsync();
        var (key, _) = await EnableAsync(owner);
        for (var i = 0; i < 5; i++)
            (await owner.Client.PostAsJsonAsync("/api/auth/manage/2fa", new { enable = false, twoFactorCode = "000000" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var locked = await owner.Client.PostAsJsonAsync("/api/auth/manage/2fa", new { enable = false, twoFactorCode = Totp.Code(key) });
        locked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests); // even the right code waits now
        (await locked.ErrorCode()).ShouldBe("Auth.TooManyCodes");
        (await app.InDbAsync(db => db.Users.SingleAsync(u => u.Email == owner.Email))).AccessFailedCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task R12_3_backup_codes_are_hashed_and_the_key_protected_and_old_plain_ones_still_work_once()
    {
        var owner = await app.OwnerAsync();
        var (key, codes) = await EnableAsync(owner);
        var stored = (await TokenAsync(owner, "RecoveryCodes"))!;
        stored.Split(';').ShouldAllBe(c => c.StartsWith("sha256:"));
        codes.ShouldAllBe(c => !stored.Contains(c));
        (await TokenAsync(owner, "AuthenticatorKey"))!.ShouldStartWith("dp:");
        (await LoginAsync(owner, twoFactorRecoveryCode: stored.Split(';')[0])).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // the hash is no code
        // R12 #12: as typed, with spaces and in lowercase.
        (await LoginAsync(owner, twoFactorRecoveryCode: $" {codes[0].ToLowerInvariant()} ")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // An account from before R12: plain codes and key.
        await app.InDbAsync(db => db.UserTokens.Where(t => t.UserId == db.Users.Single(u => u.Email == owner.Email).Id && t.Name == "RecoveryCodes")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Value, "AAAAA-11111;BBBBB-22222")));
        await app.InDbAsync(db => db.UserTokens.Where(t => t.UserId == db.Users.Single(u => u.Email == owner.Email).Id && t.Name == "AuthenticatorKey")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Value, key)));
        (await LoginAsync(owner, twoFactorRecoveryCode: "AAAAA-11111")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TokenAsync(owner, "RecoveryCodes")).ShouldBe(Abastalia.Host.Cloud.Accounts.CloudUserStore.HashCode("BBBBB-22222")); // the rest hashed
        (await LoginAsync(owner, twoFactorRecoveryCode: "BBBBB-22222")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(owner, Totp.Code(key))).StatusCode.ShouldBe(HttpStatusCode.OK); // the plain key still reads
    }

    [Fact]
    public async Task R12_4_an_authenticator_code_works_once()
    {
        var owner = await app.OwnerAsync();
        var (key, _) = await EnableAsync(owner);
        (await LoginAsync(owner, Totp.Code(key))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(owner, Totp.Again(key))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var spaced = Totp.Code(key);
        (await LoginAsync(owner, $"{spaced[..3]} {spaced[3..]}")).StatusCode.ShouldBe(HttpStatusCode.OK); // R12 #12: as the app shows it
    }

    [Fact]
    public async Task R12_5_reset_2fa_needs_a_reason_force_for_staff_and_tells_the_account()
    {
        var owner = await app.OwnerAsync();
        await EnableAsync(owner);
        (await PlatformCli.RunAsync(app.Services, PlatformCli.ResetTwoFactor, owner.Email)).ShouldBe(1);
        (await PlatformCli.RunAsync(app.Services, PlatformCli.ResetTwoFactor, owner.Email, "Correo desde su buzón y llamada al negocio")).ShouldBe(0);
        (await app.InDbAsync(db => db.PlatformAudit.AnyAsync(a => a.Path.Contains("--reason Correo desde su buzón")))).ShouldBeTrue();
        lock (app.Mail.Sent) app.Mail.Sent.ShouldContain(m => m.To == owner.Email && m.Subject.Contains("verificación en dos pasos"));

        var staff = await app.OwnerAsync();
        await EnableAsync(staff);
        (await PlatformCli.RunAsync(app.Services, PlatformCli.Grant, staff.Email)).ShouldBe(0);
        (await PlatformCli.RunAsync(app.Services, PlatformCli.ResetTwoFactor, staff.Email, "Perdió el teléfono")).ShouldBe(1);
        (await app.InDbAsync(db => db.Users.SingleAsync(u => u.Email == staff.Email))).TwoFactorEnabled.ShouldBeTrue();
        (await PlatformCli.RunAsync(app.Services, PlatformCli.ResetTwoFactor, staff.Email, "Perdió el teléfono", force: true)).ShouldBe(0);
    }
}
