using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Shouldly;
using Abastalia.Host.Local;
using Abastalia.IntegrationTests.Infra;

namespace Abastalia.IntegrationTests.Platform;

/// <summary>TK-160: the clean-VM test found that prepare-service (and remove-data.ps1) sent EXEC('...' + QUOTENAME(...)), a syntax error,
/// so no store could install. SQL Server compiles each batch with NOEXEC on: a syntax error still fails, nothing is created.</summary>
public sealed class InstallerSqlTests
{
    [Fact, System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Prepare_service_and_remove_data_send_SQL_that_compiles()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only, as the installer.");
        if (!TestSqlServer.IsEnabled) Assert.Skip("SQL Server only (ABASTALIA_TEST_DB=sqlserver).");
        var removeData = File.ReadAllText(Repo(@"deploy\installer\remove-data.ps1"));
        var dropSql = Regex.Match(removeData, @"\$dropSql = @'\r?\n(?<sql>.*?)\r?\n'@", RegexOptions.Singleline).Groups["sql"].Value;
        dropSql.ShouldNotBeEmpty();

        var master = new SqlConnectionStringBuilder(TestSqlServer.BaseConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var sql = new SqlConnection(master);
        await sql.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var batch in new[] { PrepareService.LoginSql, dropSql })
        {
            await using var command = sql.CreateCommand();
            command.CommandText = "SET NOEXEC ON;\n" + batch + "\nSET NOEXEC OFF;";
            command.Parameters.AddWithValue("@account", @"NT SERVICE\AbastaliaTest");
            command.Parameters.AddWithValue("@db", "abastalia_test_missing");
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static string Repo(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Abastalia.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Abastalia.slnx not found"), relative);
    }
}
