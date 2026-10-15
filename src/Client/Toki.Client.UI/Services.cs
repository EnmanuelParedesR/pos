using Microsoft.Extensions.DependencyInjection;
using Toki.Client.UI.Api;
using Toki.Client.UI.State;

namespace Toki.Client.UI;

public static class Services
{
    /// <summary>The register's services. Each host adds its own <see cref="ILocalStore"/>.</summary>
    public static IServiceCollection AddTokiClient(this IServiceCollection services, Uri api, HttpMessageHandler? handler = null)
    {
        // One connection pool for the whole app, however many scopes (the web host makes one per browser tab).
        var shared = handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        services.AddScoped(_ => new HttpClient(shared, disposeHandler: false) { BaseAddress = api, Timeout = TimeSpan.FromSeconds(30) });
        services.AddScoped<Session>();
        services.AddScoped<TokiApi>();
        services.AddScoped<Approver>();
        services.AddScoped<Cart>();
        return services;
    }
}
