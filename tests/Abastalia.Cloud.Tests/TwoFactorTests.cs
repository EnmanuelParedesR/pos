using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Abastalia.Host.Cloud.Platform;
using Shouldly;

namespace Abastalia.Cloud.Tests;

/// <summary>TK-298: the panel's Seguridad page (Identity's /manage/2fa), the backup codes and the staff reset for a lost phone.</summary>
public sealed class TwoFactorTests(CloudApp app) : IClassFixture<CloudApp>
{
    private static async Task<JsonElement> TwoFactorAsync(Owner who, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await who.Client.PostAsJsonAsync("/api/auth/manage/2fa", body);
        response.StatusCode.ShouldBe(expected);
        return expected == HttpStatusCode.OK ? await response.Json() : default;
    }

    /// <summary>Turns 2FA on and returns the key and the backup codes shown once.</summary>
    private static async Task<(string Key, string[] Codes)> EnableAsync(Owner who)
    {
        var key = (await TwoFactorAsync(who, new { })).GetProperty("sharedKey").GetString()!;
        var on = await TwoFactorAsync(who, new { enable = true, twoFactorCode = Totp.Code(key) });
        on.GetProperty("isTwoFactorEnabled").GetBoolean().ShouldBeTrue();
        return (key, on.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToArray());
    }

    private Task<HttpResponseMessage> LoginAsync(Owner who, string? twoFactorCode = null, string? twoFactorRecoveryCode = null) =>
        app.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = who.Email, password = CloudApp.Password, twoFactorCode, twoFactorRecoveryCode });

    [Fact]
    public async Task Turning_it_off_or_new_backup_codes_need_the_current_code_or_a_backup_code()
    {
        var owner = await app.OwnerAsync();
        var (key, codes) = await EnableAsync(owner);
        codes.Length.ShouldBe(10);

        // A stolen session alone cannot remove the second step.
        await TwoFactorAsync(owner, new { enable = false }, HttpStatusCode.BadRequest);
        await TwoFactorAsync(owner, new { resetSharedKey = true }, HttpStatusCode.BadRequest);
        await TwoFactorAsync(owner, new { resetRecoveryCodes = true, twoFactorCode = "not-a-code" }, HttpStatusCode.BadRequest);
        (await TwoFactorAsync(owner, new { })).GetProperty("isTwoFactorEnabled").GetBoolean().ShouldBeTrue();

        // Lost phone: a backup code (used up) gives new codes; the app's code turns it off.
        var renewed = await TwoFactorAsync(owner, new { resetRecoveryCodes = true, twoFactorCode = codes[0].ToLowerInvariant() });
        renewed.GetProperty("recoveryCodes").GetArrayLength().ShouldBe(10);
        await TwoFactorAsync(owner, new { enable = false, twoFactorCode = codes[0] }, HttpStatusCode.BadRequest); // the old codes no longer work
        (await TwoFactorAsync(owner, new { enable = false, twoFactorCode = Totp.Code(key) })).GetProperty("isTwoFactorEnabled").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_backup_code_signs_in_once_instead_of_the_app()
    {
        var owner = await app.OwnerAsync();
        var (_, codes) = await EnableAsync(owner);

        (await LoginAsync(owner, twoFactorRecoveryCode: codes[1])).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(owner, twoFactorRecoveryCode: codes[1])).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Staff_reset_turns_it_off_and_closes_every_session()
    {
        var owner = await app.OwnerAsync();
        var (key, _) = await EnableAsync(owner);
        var refresh = (await (await LoginAsync(owner, Totp.Code(key))).Json()).GetProperty("refreshToken").GetString();
        (await LoginAsync(owner)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // RequiresTwoFactor

        (await PlatformCli.RunAsync(app.Services, PlatformCli.ResetTwoFactor, owner.Email, "Llamó al teléfono del negocio")).ShouldBe(0);

        (await app.InDbAsync(db => db.PlatformAudit.AnyAsync(a => a.Method == "CLI" && a.Path == $"{PlatformCli.ResetTwoFactor} {owner.Email} --reason Llamó al teléfono del negocio"))).ShouldBeTrue();
        (await app.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var again = await app.SignInAsync(owner.Email); // the password alone, then 2FA on again with a new key and new codes
        var status = await TwoFactorAsync(again, new { });
        (status.GetProperty("isTwoFactorEnabled").GetBoolean(), status.GetProperty("sharedKey").GetString() == key).ShouldBe((false, false));
        (await EnableAsync(again)).Codes.Length.ShouldBe(10);
    }
}
