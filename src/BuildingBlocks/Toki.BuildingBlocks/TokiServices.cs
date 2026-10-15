using System.Reflection;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;

namespace Toki.BuildingBlocks;

public static class TokiServices
{
    /// <summary>Registers the building blocks and scans the module assemblies for handlers and validators.</summary>
    public static IServiceCollection AddToki(this IServiceCollection services, IConfiguration config, IReadOnlyCollection<Assembly> modulos)
    {
        var db = config.GetSection(DatabaseOptions.Seccion).Get<DatabaseOptions>() ?? new DatabaseOptions();
        if (string.IsNullOrWhiteSpace(db.ConnectionString)) throw new InvalidOperationException("Falta Database:ConnectionString.");
        services.AddSingleton(db);
        services.AddScoped<UnitOfWork>();
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, HttpUserContext>();

        services.AddModuleDbContext<InfraDbContext>();
        services.AddScoped<IOutbox, Outbox>();
        services.AddScoped<IAuditoria, Auditoria>();
        services.AddHealthChecks().AddCheck<RelojHealthCheck>("reloj");

        services.Scan(s => s.FromAssemblies(modulos)
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime());
        services.AddValidatorsFromAssemblies(modulos, includeInternalTypes: true);

        services.TryDecorate(typeof(ICommandHandler<,>), typeof(AutorizacionDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(TransaccionDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidacionDecorator<,>));
        return services;
    }

    /// <summary>Registers a module DbContext on the request's shared connection.</summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services) where TContext : ModuleDbContext
    {
        services.AddDbContext<TContext>((sp, o) =>
        {
            var conexion = sp.GetRequiredService<UnitOfWork>().Connection;
            if (sp.GetRequiredService<DatabaseOptions>().Provider == DbProvider.Sqlite) o.UseSqlite(conexion);
            else o.UseSqlServer(conexion);
        });
        services.AddScoped<ModuleDbContext>(sp => sp.GetRequiredService<TContext>());
        return services;
    }

    /// <summary>SQL Server (stores, CI): applies each module's pending migrations, creating the database if needed.
    /// Sqlite (development and local tests, ADR-0002): creates every module's tables when the database is new.</summary>
    public static async Task CrearBaseDeDatosAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var contextos = scope.ServiceProvider.GetServices<ModuleDbContext>().ToList();
        if (scope.ServiceProvider.GetRequiredService<DatabaseOptions>().Provider == DbProvider.SqlServer)
        {
            foreach (var db in contextos) await db.Database.MigrateAsync(ct);
            return;
        }
        var creador = (RelationalDatabaseCreator)contextos[0].GetService<IDatabaseCreator>();
        if (await creador.ExistsAsync(ct) && await creador.HasTablesAsync(ct)) return;
        if (!await creador.ExistsAsync(ct)) await creador.CreateAsync(ct);
        foreach (var db in contextos) await ((RelationalDatabaseCreator)db.GetService<IDatabaseCreator>()).CreateTablesAsync(ct);
    }
}
