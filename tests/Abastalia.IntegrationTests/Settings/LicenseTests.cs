using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Shouldly;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Settings;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>TK-089. Own app: it installs licenses and moves the clock.</summary>
public sealed class LicenseTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    private LicenseData License(int registers, DateTime expires) =>
        new("Carnicería La Esquina", "Negocio", ["ButcherModule"], new Dictionary<string, int> { ["Registers"] = registers }, app.Clock.GetUtcNow().UtcDateTime, expires);

    [Fact]
    public void Only_a_license_signed_with_the_Abastalia_key_is_valid()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jws = Jws.Sign(License(3, DateTime.UtcNow.AddDays(30)), app.LicenseKey);
        Jws.Verify(jws, app.LicenseKey).Value.Limits["Registers"].ShouldBe(3);
        Jws.Verify(jws, other).IsFailure.ShouldBeTrue();
        // Changing the body (more registers) breaks the signature.
        var parts = jws.Split('.');
        var body = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(License(99, DateTime.UtcNow.AddDays(30)), JsonSerializerOptions.Web)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Jws.Verify($"{parts[0]}.{body}.{parts[2]}", app.LicenseKey).IsFailure.ShouldBeTrue();
        Jws.Verify("no.es.licencia", app.LicenseKey).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task The_license_sets_how_many_registers_can_pair_and_expires_with_grace()
    {
        var admin = app.Client(await app.AdminAsync());
        async Task<HttpResponseMessage> Pair()
        {
            // Registers pair seconds apart in a store; at one fake instant SQL Server would order them by Guid, not by time.
            app.Clock.Advance(TimeSpan.FromSeconds(1));
            var code = (await (await admin.PostAsJsonAsync("/api/identity/devices/pairing-codes", new { DeviceName = "Caja" })).Json()).GetProperty("code").GetString();
            return await app.Anonymous().PostAsJsonAsync("/api/identity/devices/pair", new { Code = code });
        }
        async Task<JsonElement> Features() => await admin.GetFromJsonAsync<JsonElement>("/api/settings/features");

        // Without a license: trial with two registers.
        (await Features()).GetProperty("status").GetString().ShouldBe("Trial");
        (await Pair()).EnsureSuccessStatusCode();
        (await Pair()).EnsureSuccessStatusCode();
        (await (await Pair()).ErrorCode()).ShouldBe("Identity.RegisterLimitReached");

        // A forged license is refused; the vendor's one opens a third register.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (await (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(License(10, DateTime.UtcNow.AddDays(30)), other) })).ErrorCode())
            .ShouldBe("License.Invalid");
        var expires = app.Clock.GetUtcNow().UtcDateTime.AddDays(30);
        (await admin.PutAsJsonAsync("/api/settings/license", new { License = Jws.Sign(License(3, expires), app.LicenseKey) })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var features = await Features();
        (features.GetProperty("status").GetString(), features.GetProperty("registers").GetInt32(), features.GetProperty("plan").GetString()).ShouldBe(("Active", 3, "Negocio"));
        features.GetProperty("enabled").EnumerateArray().Select(f => f.GetString()).ShouldBe(["ButcherModule"]);
        (await Pair()).EnsureSuccessStatusCode();
        (await (await Pair()).ErrorCode()).ShouldBe("Identity.RegisterLimitReached");

        // Expired: 15 days of grace keep everything; after that, paid functions go off (selling never does).
        app.Clock.Advance(TimeSpan.FromDays(31));
        admin = app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, (await app.AdminAsync()).Device));
        (await Features()).GetProperty("status").GetString().ShouldBe("Grace");
        app.Clock.Advance(LicenseService.GracePeriod);
        admin = app.Client(await app.SignInAsync("admin", AbastaliaApp.AdminPin, (await app.AdminAsync()).Device));
        features = await Features();
        (features.GetProperty("status").GetString(), features.GetProperty("registers").GetInt32()).ShouldBe(("Restricted", LicenseService.RestrictedRegisters));
        features.GetProperty("enabled").GetArrayLength().ShouldBe(0);
    }
}
