using System.Reflection;
using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;

namespace Abastalia.BuildingBlocks;

public static class AbastaliaServices
{
    /// <summary>Registers the building blocks and scans the module assemblies for handlers and validators.</summary>
    public static IServiceCollection AddAbastalia(this IServiceCollection services, IConfiguration config, IReadOnlyCollection<Assembly> modules)
    {
        var db = config.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();
        if (string.IsNullOrWhiteSpace(db.ConnectionString)) throw new InvalidOperationException("Missing Database:ConnectionString.");
        services.AddSingleton(db);
        services.AddScoped<UnitOfWork>();
        services.TryAddSingleton(TimeProvider.System); // a host may have registered its own first (ShiftedClock)
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, HttpUserContext>();

        services.AddModuleDbContext<InfraDbContext>();
        services.AddScoped<IOutbox, Outbox>();
        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<Endpoints.AssistantRun>(); // TK-343
        services.AddDataProtection(); // the Settings module keeps its key ring in the key folder
        services.AddSingleton<ServiceKeys>();
        services.AddSingleton<AuditKey>();
        services.AddHealthChecks().AddCheck<ClockHealthCheck>("clock").AddCheck<OutboxHealthCheck>("outbox");
        services.AddScoped<OutboxProcessor>();
        if (db.CreateOnStartup) services.AddHostedService<CreateDatabaseOnStartup>();
        services.AddHostedService<OutboxProcessorWorker>();

        services.Scan(s => s.FromAssemblies(modules)
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime());
        services.Scan(s => s.FromAssemblies(modules)
            .AddClasses(c => c.AssignableTo(typeof(IEventHandler<>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime());
        services.AddSingleton(new EventTypes(modules.SelectMany(a => a.GetTypes()).SelectMany(t => t.GetInterfaces())
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventHandler<>)).Select(i => i.GetGenericArguments()[0])
            .Distinct().ToDictionary(t => t.FullName!)));
        services.AddValidatorsFromAssemblies(modules, includeInternalTypes: true);

        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ApprovalDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(TransactionDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidationDecorator<,>));
        return services;
    }

    /// <summary>Registers a module DbContext on the request's shared connection.</summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services) where TContext : ModuleDbContext
    {
        services.AddDbContext<TContext>((sp, o) =>
        {
            var connection = sp.GetRequiredService<UnitOfWork>().Connection;
            if (sp.GetRequiredService<DatabaseOptions>().Provider == DbProvider.Sqlite) o.UseSqlite(connection);
            else o.UseSqlServer(connection);
        });
        services.AddScoped<ModuleDbContext>(sp => sp.GetRequiredService<TContext>());
        return services;
    }

    /// <summary>SQL Server (stores, CI): applies each module's pending migrations, creating the database if needed.
    /// Sqlite (development and local tests, ADR-0002): creates every module's tables when the database is new.</summary>
    public static async Task CreateDatabaseAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var contexts = scope.ServiceProvider.GetServices<ModuleDbContext>().ToList();
        var creator = (RelationalDatabaseCreator)contexts[0].GetService<IDatabaseCreator>();
        if (scope.ServiceProvider.GetRequiredService<DatabaseOptions>().Provider == DbProvider.SqlServer)
        {
            foreach (var db in contexts)
            {
                db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30)); // an index over a year of sales takes a while on SQL Server Express
                await db.Database.MigrateAsync(ct);
            }
            return;
        }
        if (await creator.ExistsAsync(ct) && await creator.HasTablesAsync(ct)) return;
        if (!await creator.ExistsAsync(ct)) await creator.CreateAsync(ct);
        foreach (var db in contexts) await ((RelationalDatabaseCreator)db.GetService<IDatabaseCreator>()).CreateTablesAsync(ct);
    }
}
