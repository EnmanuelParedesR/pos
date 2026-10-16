using Microsoft.Extensions.DependencyInjection;
using Abastalia.BuildingBlocks;

namespace Abastalia.Sales;

public static class SalesModule
{
    public static IServiceCollection AddSalesModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<SalesDbContext>();
        services.AddScoped<Contracts.ISalesReports, SalesReports>();
        services.AddScoped<Abastalia.Sync.Contracts.ICloudSnapshot, Features.CatalogSnapshot>();
        return services;
    }
}
