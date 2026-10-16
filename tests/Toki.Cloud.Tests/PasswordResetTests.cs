using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Shouldly;

namespace Toki.Cloud.Tests;

/// <summary>TK-230: "¿La olvidó?" with Identity's endpoints; e-mails in Spanish with the code after "#" of a panel link (R8 #12).</summary>
public sealed class PasswordResetTests(MailingCloudApp app) : IClassFixture<MailingCloudApp>
{
    /// <summary>The values after "#" of the panel link in the last e-mail to <paramref name="to"/>.</summary>
    private Dictionary<string, string> LinkValues(string to, string path)
    {
        var body = app.Mail.Sent.Last(m => m.To == to).Body;
        var start = body.IndexOf(path + "#", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(-1, body);
        var fragment = body[(start + path.Length + 1)..].Split('\n')[0].Trim();
        return QueryHelpers.ParseQuery("?" + fragment).ToDictionary(q => q.Key, q => q.Value.ToString());
    }

    [Fact]
    public async Task A_confirmed_owner_resets_the_password_from_the_emailed_link()
    {
        var owner = await app.OwnerAsync();
        var signup = app.Mail.Sent.Single(m => m.To == owner.Email);
        signup.Subject.ShouldBe("Confirme su correo de Toki");
        var confirm = LinkValues(owner.Email, "/app/confirmar");
        var client = app.CreateClient();
        (await client.GetAsync($"/api/auth/confirmEmail?userId={Uri.EscapeDataString(confirm["userId"])}&code={Uri.EscapeDataString(confirm["code"])}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/api/auth/forgotPassword", new { email = owner.Email })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var reset = app.Mail.Sent.Last(m => m.To == owner.Email);
        reset.Subject.ShouldBe("Cambie su contraseña de Toki");
        reset.Body.ShouldNotContain("/api/"); // the link opens the panel, not the API
        var values = LinkValues(owner.Email, "/app/restablecer");
        values["email"].ShouldBe(owner.Email);

        (await client.PostAsJsonAsync("/api/auth/resetPassword", new { email = owner.Email, resetCode = values["code"], newPassword = "Nueva2026x" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/auth/login", new { email = owner.Email, password = "Nueva2026x" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/auth/login", new { email = owner.Email, password = CloudApp.Password })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The code works once.
        (await client.PostAsJsonAsync("/api/auth/resetPassword", new { email = owner.Email, resetCode = values["code"], newPassword = "Otra2026xx" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Forgot_password_answers_the_same_for_an_unknown_or_unconfirmed_email_and_sends_nothing()
    {
        var owner = await app.OwnerAsync(); // registered, never confirmed
        var sent = app.Mail.Sent.Count;
        var client = app.CreateClient();

        var unknown = await client.PostAsJsonAsync("/api/auth/forgotPassword", new { email = $"nadie{Guid.NewGuid():N}@example.com" });
        var unconfirmed = await client.PostAsJsonAsync("/api/auth/forgotPassword", new { email = owner.Email });

        (unknown.StatusCode, await unknown.Content.ReadAsStringAsync()).ShouldBe((unconfirmed.StatusCode, await unconfirmed.Content.ReadAsStringAsync()));
        unknown.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Mail.Sent.Count.ShouldBe(sent);
    }
}
