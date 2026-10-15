using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Toki.BuildingBlocks.Persistence;

/// <summary>Lets dotnet-ef build a module's context to write its SQL Server migrations (each module owns its own, ADR-0005).
/// Nothing connects at design time; the connection string only names the provider.</summary>
public abstract class DesignTimeFactory<T> : IDesignTimeDbContextFactory<T> where T : ModuleDbContext
{
    public T CreateDbContext(string[] args) =>
        (T)Activator.CreateInstance(typeof(T),
            new DbContextOptionsBuilder<T>().UseSqlServer("Server=.;Database=toki_design;Integrated Security=True;TrustServerCertificate=True").Options)!;
}

internal sealed class InfraDesignTimeFactory : DesignTimeFactory<InfraDbContext>;
