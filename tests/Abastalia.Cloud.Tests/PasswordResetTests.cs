using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Abastalia.Cloud.Tests;

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

    /// <summary>Registered through /api/auth/register, as the panel does, without signing in (R11 B-High1: it cannot before confirming).</summary>
    private async Task<string> RegisterAsync()
    {
        var email = $"owner{Guid.NewGuid():N}@example.com";
        (await app.CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password = CloudApp.Password })).EnsureSuccessStatusCode();
        return email;
    }

    /// <summary>The panel's /app/confirmar page: the link's values to POST /api/auth/confirm (R11 B-Low1: never in a URL).</summary>
    private async Task<HttpResponseMessage> ConfirmAsync(string email) =>
        await app.CreateClient().PostAsJsonAsync("/api/auth/confirm", LinkValues(email, "/app/confirmar"));

    private Task<HttpResponseMessage> LoginAsync(string email, string password = CloudApp.Password) =>
        app.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password });

    [Fact]
    public async Task A_confirmed_owner_resets_the_password_from_the_emailed_link()
    {
        var owner = new { Email = await RegisterAsync() };
        var signup = app.Mail.Sent.Single(m => m.To == owner.Email);
        signup.Subject.ShouldBe("Confirme su correo de Abastalia");
        var client = app.CreateClient();
        (await ConfirmAsync(owner.Email)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/api/auth/forgotPassword", new { email = owner.Email })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var reset = app.Mail.Sent.Last(m => m.To == owner.Email);
        reset.Subject.ShouldBe("Cambie su contraseña de Abastalia");
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
        var email = await RegisterAsync(); // never confirmed
        var sent = app.Mail.Sent.Count;
        var client = app.CreateClient();

        var unknown = await client.PostAsJsonAsync("/api/auth/forgotPassword", new { email = $"nadie{Guid.NewGuid():N}@example.com" });
        var unconfirmed = await client.PostAsJsonAsync("/api/auth/forgotPassword", new { email });

        (unknown.StatusCode, await unknown.Content.ReadAsStringAsync()).ShouldBe((unconfirmed.StatusCode, await unconfirmed.Content.ReadAsStringAsync()));
        unknown.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Mail.Sent.Count.ShouldBe(sent);
    }

    [Fact]
    public async Task TK231_R11_an_unconfirmed_account_cannot_sign_in_and_the_resent_link_confirms_it()
    {
        var email = await RegisterAsync();
        var refused = await LoginAsync(email);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await refused.Json()).GetProperty("detail").GetString().ShouldBe("NotAllowed");

        (await app.CreateClient().PostAsJsonAsync("/api/auth/resendConfirmationEmail", new { email })).StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Mail.Sent.Count(m => m.To == email && m.Subject == "Confirme su correo de Abastalia").ShouldBe(2); // sign-up and the resend
        var confirmed = await ConfirmAsync(email);
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await confirmed.Json()).GetProperty("firstConfirmation").GetBoolean().ShouldBeTrue();

        var owner = await app.SignInAsync(email);
        (await owner.Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
        (await ConfirmAsync(email)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // the same link again: the first confirmation rotated the stamp
        (await app.CreateClient().PostAsJsonAsync("/api/auth/confirm", new { userId = "nadie", code = "x" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task R11_a_pre_hijacked_account_loses_what_was_added_before_the_owner_confirmed_and_every_session()
    {
        // Someone registered the owner's e-mail first and, back when sign-in did not need a confirmed e-mail, got a session, invited an accomplice
        // and added one as a member (written here directly).
        var email = await RegisterAsync();
        var user = await app.InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Email == email));
        var (userId, tenantId) = (user.Id, user.TenantId);
        await app.ConfirmEmailAsync(email);
        var refresh = (await (await LoginAsync(email)).Json()).GetProperty("refreshToken").GetString();
        await app.InDbAsync(db => db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, false)));
        var accomplice = await app.OwnerAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        await app.InDbAsync(async db =>
        {
            db.TenantMembers.Add(new Abastalia.Host.Cloud.Persistence.TenantMember { TenantId = tenantId, UserId = await db.Users.Where(u => u.Email == accomplice.Email).Select(u => u.Id).SingleAsync(), Role = Abastalia.Host.Cloud.Persistence.MemberRole.Owner, JoinedUtc = now });
            db.TenantInvites.Add(new Abastalia.Host.Cloud.Persistence.TenantInvite { Id = Guid.NewGuid(), TenantId = tenantId, Email = "otro@example.com", TokenHash = new byte[32], CreatedUtc = now, ExpiresUtc = now.AddDays(7) });
            return await db.SaveChangesAsync();
        });

        // The real owner confirms the address (resend + link): the members and invites from before go, and the old session cannot renew.
        (await app.CreateClient().PostAsJsonAsync("/api/auth/resendConfirmationEmail", new { email })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await ConfirmAsync(email)).Json()).GetProperty("firstConfirmation").GetBoolean().ShouldBeTrue();
        (await app.InDbAsync(db => db.TenantMembers.Where(m => m.TenantId == tenantId).Select(m => m.UserId).ToListAsync())).ShouldBe([userId]);
        (await app.InDbAsync(db => db.TenantInvites.AnyAsync(i => i.TenantId == tenantId && i.RevokedUtc == null))).ShouldBeFalse();
        (await app.CreateClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task R11_register_answers_the_same_for_a_taken_email_and_identity_emails_are_capped_per_recipient()
    {
        var email = await RegisterAsync();
        var again = await app.CreateClient().PostAsJsonAsync("/api/auth/register", new { email, password = CloudApp.Password });
        again.StatusCode.ShouldBe(HttpStatusCode.OK); // no "ese correo ya tiene una cuenta" for anyone who asks
        app.Mail.Sent.Count(m => m.To == email).ShouldBe(1);

        // Sign-up + 2 resends = 3 an hour to one address; more are not sent, and the answer does not change.
        for (var i = 0; i < 4; i++)
            (await app.CreateClient().PostAsJsonAsync("/api/auth/resendConfirmationEmail", new { email })).StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Mail.Sent.Count(m => m.To == email).ShouldBe(3);
    }

    [Fact]
    public async Task TK231_resend_answers_the_same_for_an_unknown_email()
    {
        var owner = new { Email = await RegisterAsync() };
        var client = app.CreateClient();
        var unknownEmail = $"nadie{Guid.NewGuid():N}@example.com";

        var unknown = await client.PostAsJsonAsync("/api/auth/resendConfirmationEmail", new { email = unknownEmail });
        var known = await client.PostAsJsonAsync("/api/auth/resendConfirmationEmail", new { email = owner.Email });

        (unknown.StatusCode, await unknown.Content.ReadAsStringAsync()).ShouldBe((known.StatusCode, await known.Content.ReadAsStringAsync()));
        unknown.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Mail.Sent.ShouldNotContain(m => m.To == unknownEmail);
    }

    [Fact]
    public async Task TK231_resend_is_throttled_per_address_of_origin()
    {
        await using var strict = app.WithWebHostBuilder(b => b.UseSetting("Auth:ResendConfirmationPerMinute", "2"));
        var client = strict.CreateClient();
        var email = $"nadie{Guid.NewGuid():N}@example.com";
        for (var i = 0; i < 2; i++)
            (await client.PostAsJsonAsync("/api/auth/resendConfirmationEmail", new { email })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/auth/resendConfirmationEmail", new { email })).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await client.PostAsJsonAsync("/api/auth/forgotPassword", new { email })).StatusCode.ShouldBe(HttpStatusCode.OK); // only the resend
    }
}
