using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;

namespace Toki.Ventas;

public static class VentasModule
{
    public static IServiceCollection AddVentasModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<VentasDbContext>();
        services.AddScoped<Contracts.IVentasReportes, VentasReportes>();
        return services;
    }
}
