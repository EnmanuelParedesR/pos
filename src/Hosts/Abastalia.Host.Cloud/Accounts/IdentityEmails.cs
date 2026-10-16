using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Abastalia.Host.Cloud.Persistence;
using Abastalia.Host.Cloud.Quotas;

namespace Abastalia.Host.Cloud.Accounts;

/// <summary>TK-230: the e-mails of Identity's endpoints (/api/auth: register, forgotPassword, resendConfirmationEmail) in Spanish, through the
/// cloud's <see cref="IEmailSender"/> (logged in dev, ACS when configured). Links go to the panel with the code after "#" (R8 #12), never to the API.
/// Identity sends a reset code only to a confirmed address and answers forgotPassword the same either way (no account enumeration).</summary>
/// <remarks>TK-248: they count as the business's e-mails but are never refused (a reset must always arrive); the per-IP limits hold them back,
/// and R11 B-Med2 caps them per recipient (<see cref="IdentityEmailCap"/>).
/// MapIdentityApi resolves this once from the root provider, so the scoped <see cref="IQuota"/> is taken from a scope per e-mail (e2e 2026-10-16:
/// with scope validation, as in Development, the host did not start).</remarks>
internal sealed class IdentityEmails(IEmailSender email, IConfiguration config, IServiceScopeFactory scopes, IdentityEmailCap cap, ILogger<IdentityEmails> log)
    : IEmailSender<CloudUser>
{
    /// <summary>TK-231: POST /api/auth/resendConfirmationEmail, per IP (Program.cs).</summary>
    public const string ResendRateLimit = "auth-resend-confirmation";

    /// <summary>Identity's link points to its own GET /api/auth/confirmEmail (HTML-encoded); the panel page calls it with the same values.</summary>
    public Task SendConfirmationLinkAsync(CloudUser user, string to, string confirmationLink)
    {
        var query = QueryHelpers.ParseQuery(new Uri(WebUtility.HtmlDecode(confirmationLink)).Query);
        var values = string.Join('&', query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value.ToString())}"));
        return SendAsync(user, to, "Confirme su correo de Abastalia",
            $"""
            Hola:

            Confirme que este es su correo para el panel de Abastalia:
            {MemberEndpoints.PanelUrl(config)}/app/confirmar#{values}

            Así podrá recuperar su contraseña si la olvida. Si usted no creó una cuenta en Abastalia, ignore este correo.
            """);
    }

    public Task SendPasswordResetCodeAsync(CloudUser user, string to, string resetCode) =>
        SendAsync(user, to, "Cambie su contraseña de Abastalia",
            $"""
            Hola:

            Alguien pidió cambiar la contraseña de su cuenta del panel de Abastalia. Para crear una nueva, abra este enlace:
            {MemberEndpoints.PanelUrl(config)}/app/restablecer#email={Uri.EscapeDataString(to)}&code={WebUtility.HtmlDecode(resetCode)}

            El enlace sirve una sola vez y por un día. Si usted no lo pidió, ignore este correo: su contraseña no cambia.
            """);

    /// <summary>Not used by MapIdentityApi (it sends the code); kept for the interface.</summary>
    public Task SendPasswordResetLinkAsync(CloudUser user, string to, string resetLink) =>
        SendAsync(user, to, "Cambie su contraseña de Abastalia", $"Para crear una contraseña nueva, abra este enlace:\n{WebUtility.HtmlDecode(resetLink)}");

    private async Task SendAsync(CloudUser user, string to, string subject, string body)
    {
        if (!cap.TryTake(to))
        {
            log.LogWarning("Identity e-mail to user {UserId} not sent: the per-recipient hourly cap was reached", user.Id);
            return;
        }
        await email.SendAsync(to, subject, body, CancellationToken.None);
        if (user.TenantId == Guid.Empty) return;
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IQuota>().AddAsync(user.TenantId, Meter.Emails, 1);
    }
}
