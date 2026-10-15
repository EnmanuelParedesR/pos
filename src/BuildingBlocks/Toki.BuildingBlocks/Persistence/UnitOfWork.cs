using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Toki.BuildingBlocks.Persistence;

public enum DbProvider { SqlServer, Sqlite }

public sealed class DatabaseOptions
{
    public const string Seccion = "Database";

    /// <summary>SqlServer in production. Sqlite for development and tests on machines without SQL Server (ADR-0002).</summary>
    public DbProvider Provider { get; set; } = DbProvider.SqlServer;
    public string ConnectionString { get; set; } = "";
}

/// <summary>One connection and one transaction per request, shared by every module's DbContext,
/// so a sale writes ventas, inventario, caja, fiscal, outbox and audit atomically.</summary>
public sealed class UnitOfWork(DatabaseOptions options) : IAsyncDisposable
{
    public DbConnection Connection { get; } = options.Provider == DbProvider.Sqlite
        ? new SqliteConnection(options.ConnectionString)
        : new SqlConnection(options.ConnectionString);

    public DbTransaction? Transaction { get; private set; }

    public async Task<DbTransaction> BeginAsync(CancellationToken ct)
    {
        if (Connection.State != System.Data.ConnectionState.Open) await Connection.OpenAsync(ct);
        // Sqlite: BEGIN IMMEDIATE (the default) takes the write lock up front, so two writers queue instead of deadlocking.
        Transaction = await Connection.BeginTransactionAsync(ct);
        return Transaction;
    }

    public async Task EndAsync(bool commit, CancellationToken ct)
    {
        if (Transaction is null) return;
        if (commit) await Transaction.CommitAsync(ct);
        else await Transaction.RollbackAsync(CancellationToken.None);
        await Transaction.DisposeAsync();
        Transaction = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Transaction is not null) await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }
}
