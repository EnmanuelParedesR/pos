using Microsoft.Extensions.DependencyInjection;
using Abastalia.Client.UI.Api;
using Abastalia.Client.UI.State;
using Abastalia.Hardware;

namespace Abastalia.Client.UI;

public static class Services
{
    /// <summary>The register's services. Each host adds its own <see cref="ILocalStore"/> and <see cref="AddAbastaliaHardware"/>.</summary>
    public static IServiceCollection AddAbastaliaClient(this IServiceCollection services, Uri api, HttpMessageHandler? handler = null)
    {
        // One connection pool for the whole app, however many scopes (the web host makes one per browser tab).
        // Over https the store CA is pinned when this PC pairs and this PC shows its own certificate (TK-091); plain http (development) never asks.
        var pin = new NodeCertificatePin();
        services.AddSingleton(pin);
        var shared = handler ?? new StoreHandler(pin);
        services.AddSingleton(new ServerAddress(api));
        // AbastaliaApi puts the time limit on each call (30 s, longer for a backup) and the server address (it can change while joining a store).
        services.AddScoped(_ => new HttpClient(shared, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan });
        services.AddScoped<Session>();
        services.AddScoped<AbastaliaApi>();
        services.AddScoped<Approver>();
        services.AddScoped<Cart>();
        services.AddScoped<BackOffice>();
        return services;
    }

    /// <summary>The scale and printer drivers of this host (arquitectura §6.3), and the settings a PC starts with before anyone saves its own.</summary>
    public static IServiceCollection AddAbastaliaHardware(this IServiceCollection services, IHardwareDrivers drivers, HardwareSettings? defaults = null) =>
        services.AddScoped(sp => new Peripherals(sp.GetRequiredService<ILocalStore>(), drivers, defaults ?? new()));
}

/// <summary>Pages of another assembly the router serves too (the web host's /dev/escuadra gallery, in Development only).</summary>
public sealed record ExtraPages(params System.Reflection.Assembly[] Assemblies);

/// <summary>The connections to the store server. Pairing (or forgetting it) changes this PC's client certificate, and a pooled connection
/// keeps the certificate it was opened with: so a new pool is started whenever it changes.</summary>
internal sealed class StoreHandler(NodeCertificatePin pin) : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private (System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate, HttpMessageInvoker Invoker)? _current;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Current().SendAsync(request, cancellationToken);

    private HttpMessageInvoker Current()
    {
        var certificate = pin.ClientCertificate;
        lock (_lock)
        {
            if (_current is { } c && c.Certificate == certificate) return c.Invoker;
            // shortcut: the old pool is left to close its idle connections (1 min) instead of being disposed under a request in flight.
            var sockets = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = { RemoteCertificateValidationCallback = (_, server, _, errors) => pin.Validate(server, errors) },
            };
            if (certificate is not null) sockets.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;
            _current = (certificate, new HttpMessageInvoker(sockets));
            return _current.Value.Invoker;
        }
    }
}
