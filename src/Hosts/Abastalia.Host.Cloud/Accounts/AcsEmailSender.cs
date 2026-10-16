using Azure;
using Azure.Communication.Email;
using Azure.Identity;

namespace Abastalia.Host.Cloud.Accounts;

/// <summary>TK-222: e-mail through Azure Communication Services (docs/despliegue-azure.md). <c>Email:Provider</c> = <c>AzureCommunicationServices</c>
/// with <c>Email:Sender</c> (a verified address of the ACS e-mail domain) and <c>Email:Endpoint</c> (the managed identity signs in; it needs the
/// "Communication and Email Service Owner" role) or <c>Email:ConnectionString</c> (a secret: app setting or Key Vault reference, never in the repo).
/// Anything else keeps <see cref="LoggingEmailSender"/>.</summary>
internal sealed class AcsEmailSender(EmailClient client, string sender) : IEmailSender
{
    public const string Provider = "AzureCommunicationServices";

    public bool Delivers => true;

    /// <summary>Waits only until ACS accepts the message, not until it is delivered.</summary>
    public Task SendAsync(string to, string subject, string body, CancellationToken ct) =>
        client.SendAsync(WaitUntil.Started, sender, to, subject, htmlContent: null, plainTextContent: body, cancellationToken: ct);

    public static IServiceCollection AddEmail(IServiceCollection services, IConfiguration config)
    {
        if (config["Email:Provider"] != Provider) return services.AddSingleton<IEmailSender, LoggingEmailSender>();
        var sender = config["Email:Sender"] ?? throw new InvalidOperationException("Email:Provider AzureCommunicationServices needs Email:Sender.");
        var client = config["Email:ConnectionString"] is { Length: > 0 } connection
            ? new EmailClient(connection)
            : new EmailClient(new Uri(config["Email:Endpoint"] ?? throw new InvalidOperationException("Set Email:Endpoint or Email:ConnectionString.")),
                new DefaultAzureCredential());
        return services.AddSingleton<IEmailSender>(new AcsEmailSender(client, sender));
    }
}
