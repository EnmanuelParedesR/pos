using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.SqlClient;

namespace Toki.Host.Local;

/// <summary>`Toki.Host.Local prepare-service "NT SERVICE\Toki"`, run once by the installer as an administrator, so the service itself
/// runs with no admin rights: it gives the account the data folders (the store CA and its TLS certificates live in keys, TK-091),
/// and creates its SQL Server login (allowed to create the Toki database, which it then owns).</summary>
[SupportedOSPlatform("windows")]
internal static class PrepareService
{
    public static async Task RunAsync(string account, string connectionString)
    {
        var identity = new NTAccount(account);

        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki");
        var folder = Directory.CreateDirectory(data);
        foreach (var sub in new[] { "logs", "backups", "keys" }) folder.CreateSubdirectory(sub);
        folder.SetAccessControl(Allow(folder.GetAccessControl(), identity, FileSystemRights.Modify));
        Console.WriteLine($"Carpeta de datos: {data}");

        // Connect as the administrator running the installer, to the server only (the database does not exist yet).
        var server = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", IntegratedSecurity = true };
        await using var sql = new SqlConnection(server.ConnectionString);
        await sql.OpenAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = """
            IF SUSER_ID(@account) IS NULL EXEC('CREATE LOGIN ' + QUOTENAME(@account) + ' FROM WINDOWS');
            EXEC('ALTER SERVER ROLE dbcreator ADD MEMBER ' + QUOTENAME(@account));
            """;
        command.Parameters.AddWithValue("@account", account);
        await command.ExecuteNonQueryAsync();
        Console.WriteLine($"Login de SQL Server listo para {account} en {server.DataSource}");

        // SQL Server writes the plain .bak where Toki can read and delete it (its own Backup folder is closed to Toki's account).
        await using var service = sql.CreateCommand();
        service.CommandText = "SELECT TOP 1 service_account FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server (%'";
        var sqlAccount = (string)(await service.ExecuteScalarAsync())!;
        var work = folder.CreateSubdirectory(Path.Combine("backups", "work"));
        work.SetAccessControl(Allow(work.GetAccessControl(), new NTAccount(sqlAccount), FileSystemRights.Modify));
        Console.WriteLine($"SQL Server ({sqlAccount}) puede escribir respaldos en {work.FullName}");
    }

    private static T Allow<T>(T security, NTAccount account, FileSystemRights rights) where T : FileSystemSecurity
    {
        var inheritance = security is DirectorySecurity ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(account, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
