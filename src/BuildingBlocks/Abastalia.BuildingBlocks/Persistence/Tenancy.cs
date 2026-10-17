using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

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
