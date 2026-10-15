namespace Toki.Tests;

/// <summary>TK-163: the one SQL Server image every test container uses (linked into each test project that starts one).
/// A dated CU, never "latest", so CI does not change under us; bump it on purpose (tags: mcr.microsoft.com/v2/mssql/server/tags/list).</summary>
internal static class SqlServerImage
{
    public const string Name = "mcr.microsoft.com/mssql/server:2025-CU9-GDR1-ubuntu-22.04";
}
