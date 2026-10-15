using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;

namespace Toki.Sales;

public static class SalesModule
{
    public static IServiceCollection AddSalesModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<SalesDbContext>();
        services.AddScoped<Contracts.ISalesReports, SalesReports>();
        services.AddScoped<Toki.Sync.Contracts.ICloudSnapshot, Features.CatalogSnapshot>();
        return services;
    }
}
