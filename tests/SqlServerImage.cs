namespace Abastalia.Tests;

/// <summary>TK-163: the one SQL Server image every test container uses (linked into each test project that starts one).
/// A dated CU, never "latest", so CI does not change under us; bump it on purpose (tags: mcr.microsoft.com/v2/mssql/server/tags/list).</summary>
internal static class SqlServerImage
{
    public const string Name = "mcr.microsoft.com/mssql/server:2025-CU9-GDR1-ubuntu-22.04";

    /// <summary>TK-211: the container logs in as sa with a password. EF builds its "master" connection (to create the database)
    /// from the already opened connection, whose string has lost the password unless Persist Security Info is on; when a new
    /// database is briefly unavailable, EF goes back to master and fails with "Login failed for user 'sa'".
    /// Stores use Windows authentication (no password), so only the container needs this.</summary>
    public static string ConnectionString(Testcontainers.MsSql.MsSqlContainer container) =>
        container.GetConnectionString() + ";Persist Security Info=True";
}
