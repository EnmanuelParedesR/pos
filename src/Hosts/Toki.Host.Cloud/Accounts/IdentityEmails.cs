using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Toki.Host.Cloud.Persistence;

namespace Toki.Host.Cloud.Accounts;

/// <summary>TK-230: the e-mails of Identity's endpoints (/api/auth: register, forgotPassword, resendConfirmationEmail) in Spanish, through the
/// cloud's <see cref="IEmailSender"/> (logged in dev, ACS when configured). Links go to the panel with the code after "#" (R8 #12), never to the API.
/// Identity sends a reset code only to a confirmed address and answers forgotPassword the same either way (no account enumeration).</summary>
internal sealed class IdentityEmails(IEmailSender email, IConfiguration config) : IEmailSender<CloudUser>
{
    /// <summary>TK-231: POST /api/auth/resendConfirmationEmail, per IP (Program.cs).</summary>
    public const string ResendRateLimit = "auth-resend-confirmation";

    /// <summary>Identity's link points to its own GET /api/auth/confirmEmail (HTML-encoded); the panel page calls it with the same values.</summary>
    public Task SendConfirmationLinkAsync(CloudUser user, string to, string confirmationLink)
    {
        var query = QueryHelpers.ParseQuery(new Uri(WebUtility.HtmlDecode(confirmationLink)).Query);
        var values = string.Join('&', query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value.ToString())}"));
        return email.SendAsync(to, "Confirme su correo de Toki",
            $"""
            Hola:

            Confirme que este es su correo para el panel de Toki:
            {MemberEndpoints.PanelUrl(config)}/app/confirmar#{values}

            Así podrá recuperar su contraseña si la olvida. Si usted no creó una cuenta en Toki, ignore este correo.
            """, CancellationToken.None);
    }

    public Task SendPasswordResetCodeAsync(CloudUser user, string to, string resetCode) =>
        email.SendAsync(to, "Cambie su contraseña de Toki",
            $"""
            Hola:

            Alguien pidió cambiar la contraseña de su cuenta del panel de Toki. Para crear una nueva, abra este enlace:
            {MemberEndpoints.PanelUrl(config)}/app/restablecer#email={Uri.EscapeDataString(to)}&code={WebUtility.HtmlDecode(resetCode)}

            El enlace sirve una sola vez y por un día. Si usted no lo pidió, ignore este correo: su contraseña no cambia.
            """, CancellationToken.None);

    /// <summary>Not used by MapIdentityApi (it sends the code); kept for the interface.</summary>
    public Task SendPasswordResetLinkAsync(CloudUser user, string to, string resetLink) =>
        email.SendAsync(to, "Cambie su contraseña de Toki", $"Para crear una contraseña nueva, abra este enlace:\n{WebUtility.HtmlDecode(resetLink)}", CancellationToken.None);
}
