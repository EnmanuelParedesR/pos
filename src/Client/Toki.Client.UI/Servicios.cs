using Microsoft.Extensions.DependencyInjection;
using Toki.Client.UI.Api;
using Toki.Client.UI.Estado;

namespace Toki.Client.UI;

public static class Servicios
{
    /// <summary>The register's services. Each host adds its own <see cref="IAlmacenLocal"/>.</summary>
    public static IServiceCollection AddTokiCliente(this IServiceCollection s, Uri api, HttpMessageHandler? manejador = null)
    {
        // One connection pool for the whole app, however many scopes (the web host makes one per browser tab).
        var compartido = manejador ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        s.AddScoped(_ => new HttpClient(compartido, disposeHandler: false) { BaseAddress = api, Timeout = TimeSpan.FromSeconds(30) });
        s.AddScoped<Sesion>();
        s.AddScoped<TokiApi>();
        s.AddScoped<Autorizador>();
        s.AddScoped<Carrito>();
        return s;
    }
}
