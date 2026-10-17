using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Abastalia.BuildingBlocks.Persistence;

/// <summary>TK-319 (ADR-0013, arquitectura §10): the business whose rows a request reads and writes. Never from the body or the URL.</summary>
public interface ITenantContext
{
    Guid TenantId { get; }
}

/// <summary>The store: one database, one business, always the same fixed tenant.</summary>
public sealed class StoreTenant : ITenantContext
{
    /// <summary>The value every store row has (the column's default when an upgraded store got it).</summary>
    public static readonly Guid Id = Guid.Empty;

    public Guid TenantId => Id;
}

/// <summary>TK-320: the Lite host's business for one DI scope: set from the authenticated device (a request) or by
/// <see cref="TenantScopes.CreateTenantScope"/> (a worker, provisioning). Unset it is <see cref="Guid.Empty"/>, which RLS shows nothing.</summary>
public sealed class ScopedTenant : ITenantContext
{
    private Guid _id;

    public Guid TenantId
    {
        get => _id;
        set
        {
            // Once a scope has a business it keeps it: no later code path can move a request or a worker pass to another one.
            if (_id != Guid.Empty && _id != value) throw new InvalidOperationException("This scope already belongs to another business.");
            _id = value;
        }
    }
}

/// <summary>TK-320: the businesses background work runs for: the store's one, or every business of the Lite host.</summary>
public interface ITenantDirectory
{
    Task<IReadOnlyList<Guid>> AllAsync(CancellationToken ct);
}

internal sealed class StoreTenants : ITenantDirectory
{
    public Task<IReadOnlyList<Guid>> AllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>([StoreTenant.Id]);
}

public static class TenantScopes
{
    /// <summary>A scope that reads and writes as <paramref name="tenantId"/>. In a store the tenant is fixed, so it is a plain scope.</summary>
    public static AsyncServiceScope CreateTenantScope(this IServiceScopeFactory scopes, Guid tenantId)
    {
        var scope = scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetRequiredService<ITenantContext>() is ScopedTenant tenant) tenant.TenantId = tenantId;
        return scope;
    }

    /// <summary>Runs <paramref name="work"/> once per business, each in its own scope. One business failing does not stop the others; the failures
    /// are thrown together at the end (a store's single one as it was).</summary>
    public static async Task ForEachTenantAsync(this IServiceScopeFactory scopes, Func<IServiceProvider, Task> work, CancellationToken ct)
    {
        IReadOnlyList<Guid> tenants;
        await using (var scope = scopes.CreateAsyncScope()) tenants = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().AllAsync(ct);
        List<Exception>? failed = null;
        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateTenantScope(tenant);
                await work(scope.ServiceProvider);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                (failed ??= []).Add(e);
            }
        }
        if (failed is [var one]) ExceptionDispatchInfo.Throw(one);
        if (failed is not null) throw new AggregateException(failed);
    }

    /// <summary>TK-320: state a store keeps once per process (a singleton) and the Lite host once per business, resolved for the scope's tenant.</summary>
    public static IServiceCollection AddPerTenant<T>(this IServiceCollection services, Func<IServiceProvider, Guid, T> create) where T : class
    {
        services.AddSingleton(sp => new PerTenant<T>(tenant => create(sp, tenant)));
        return services.AddScoped(sp => sp.GetRequiredService<PerTenant<T>>().For(sp.GetRequiredService<ITenantContext>().TenantId));
    }
}

/// <summary>See <see cref="TenantScopes.AddPerTenant"/>.</summary>
public sealed class PerTenant<T>(Func<Guid, T> create) where T : class
{
    private readonly ConcurrentDictionary<Guid, T> _items = new();

    /// <summary>The instances made so far (a store: at most one).</summary>
    public ICollection<T> All => _items.Values;

    public T For(Guid tenant) => _items.GetOrAdd(tenant, create);
}

/// <summary>TK-320: what a store's migrations seed (the business, the roles, the main warehouse, the label formats, the audit head), added for a new
/// Lite business. Read from each context's model, so a seed added later reaches new businesses too. The caller's transaction saves it; the
/// tenant is stamped on save.</summary>
public static class TenantSeed
{
    public static void Add(IEnumerable<ModuleDbContext> contexts)
    {
        foreach (var db in contexts)
            foreach (var type in db.GetService<IDesignTimeModel>().Model.GetEntityTypes())
                foreach (var row in type.GetSeedData())
                {
                    var entry = db.Entry(Activator.CreateInstance(type.ClrType, nonPublic: true)!);
                    foreach (var (name, value) in row) entry.Property(name).CurrentValue = value;
                    entry.State = EntityState.Added;
                }
    }
}

/// <summary>A table every business reads alike (public DGII data): no TenantId, no filter, no RLS.</summary>
public interface ISharedAcrossTenants;

/// <summary>TK-319: Row-Level Security for the shared Lite database (<see cref="DatabaseOptions.MultiTenant"/>, SQL Server only), the barrier
/// that holds when an EF filter is ignored or SQL is written by hand. Never in a store. The policy is built from the EF model, so a new table
/// is covered at the next start; it is dropped before migrations (a schema-bound policy blocks altering its tables) and created after them.</summary>
public static class RowLevelSecurity
{
    private const string Policy = "rls.TenantPolicy";

    private const string Drop = "IF OBJECT_ID(N'" + Policy + "', N'SP') IS NOT NULL DROP SECURITY POLICY " + Policy + ";";

    internal static Task DropAsync(DbContext db, CancellationToken ct) => db.Database.ExecuteSqlRawAsync(Drop, ct);

    internal static async Task CreateAsync(IReadOnlyList<ModuleDbContext> contexts, CancellationToken ct)
    {
        var tables = contexts.SelectMany(c => c.Model.GetEntityTypes())
            .Where(e => e.FindProperty(ModuleDbContext.TenantColumn) is not null)
            .Select(e => $"[{e.GetSchema()}].[{e.GetTableName()}]").Distinct().Order().ToList();
        var db = contexts[0].Database;
        await db.ExecuteSqlRawAsync("IF SCHEMA_ID(N'rls') IS NULL EXEC(N'CREATE SCHEMA rls');", ct);
        // No session tenant (NULL) matches nothing: a connection that did not say who it is sees and writes no row.
        await db.ExecuteSqlRawAsync("""
            CREATE OR ALTER FUNCTION rls.TenantPredicate(@TenantId uniqueidentifier) RETURNS TABLE WITH SCHEMABINDING AS
            RETURN SELECT 1 AS Allowed WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
            """, ct);
        var predicates = tables.SelectMany(t => new[]
        {
            $"ADD FILTER PREDICATE rls.TenantPredicate({ModuleDbContext.TenantColumn}) ON {t}",
            $"ADD BLOCK PREDICATE rls.TenantPredicate({ModuleDbContext.TenantColumn}) ON {t} AFTER INSERT",
            $"ADD BLOCK PREDICATE rls.TenantPredicate({ModuleDbContext.TenantColumn}) ON {t} AFTER UPDATE",
        });
        var create = $"CREATE SECURITY POLICY {Policy} {string.Join(",\n", predicates)} WITH (STATE = ON);";
        await db.ExecuteSqlRawAsync(create, ct);
    }

    /// <summary>Tells SQL Server which business this connection is, read-only for the session: every open, because pooled connections are reused
    /// (and reset). Hooked on StateChange so it also covers connections EF or SqlBulkCopy open, not only <see cref="UnitOfWork.BeginAsync"/>.</summary>
    internal static void SetSessionTenantOnOpen(DbConnection connection, ITenantContext tenant) =>
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState != ConnectionState.Open) return;
            // Lite never runs as the store's tenant: without a real one the connection stays anonymous and RLS shows it nothing.
            if (tenant.TenantId == StoreTenant.Id) return;
            using var command = connection.CreateCommand();
            command.CommandText = "EXEC sp_set_session_context @key = N'TenantId', @value = @tenant, @read_only = 1;";
            var p = command.CreateParameter();
            (p.ParameterName, p.Value) = ("@tenant", tenant.TenantId);
            command.Parameters.Add(p);
            command.ExecuteNonQuery(); // shortcut: synchronous inside the open event (one round trip); move to a DbConnectionInterceptor if it shows up
        };
}
