using Microsoft.Extensions.DependencyInjection;
using Toki.Client.UI.Api;
using Toki.Client.UI.State;
using Toki.Hardware;

namespace Toki.Client.UI;

public static class Services
{
    /// <summary>The register's services. Each host adds its own <see cref="ILocalStore"/> and <see cref="AddTokiHardware"/>.</summary>
    public static IServiceCollection AddTokiClient(this IServiceCollection services, Uri api, HttpMessageHandler? handler = null)
    {
        // One connection pool for the whole app, however many scopes (the web host makes one per browser tab).
        var shared = handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        // TokiApi puts the time limit on each call (30 s, longer for a backup).
        services.AddScoped(_ => new HttpClient(shared, disposeHandler: false) { BaseAddress = api, Timeout = Timeout.InfiniteTimeSpan });
        services.AddScoped<Session>();
        services.AddScoped<TokiApi>();
        services.AddScoped<Approver>();
        services.AddScoped<Cart>();
        services.AddScoped<BackOffice>();
        return services;
    }

    /// <summary>The scale and printer drivers of this host (arquitectura §6.3), and the settings a PC starts with before anyone saves its own.</summary>
    public static IServiceCollection AddTokiHardware(this IServiceCollection services, IHardwareDrivers drivers, HardwareSettings? defaults = null) =>
        services.AddScoped(sp => new Peripherals(sp.GetRequiredService<ILocalStore>(), drivers, defaults ?? new()));
}
