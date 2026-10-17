using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.SqlClient;

namespace Abastalia.Host.Local;

/// <summary>`Abastalia.Host.Local prepare-service "NT SERVICE\Abastalia"`, run once by the installer as an administrator, so the service itself
/// runs with no admin rights: it gives the account the data folders (the store CA and its TLS certificates live in keys, TK-091),
/// and creates its SQL Server login (allowed to create the Abastalia database, which it then owns).</summary>
[SupportedOSPlatform("windows")]
internal static class PrepareService
{
    /// <summary>The service's login, allowed to create the Abastalia database. EXEC('...' + QUOTENAME(...)) is a syntax error (EXEC takes only
    /// literals and variables), found by the clean-VM test (TK-160): the statement is built in a variable first.</summary>
    internal const string LoginSql = """
        DECLARE @create nvarchar(max) = N'CREATE LOGIN ' + QUOTENAME(@account) + N' FROM WINDOWS';
        DECLARE @role nvarchar(max) = N'ALTER SERVER ROLE dbcreator ADD MEMBER ' + QUOTENAME(@account);
        IF SUSER_ID(@account) IS NULL EXEC(@create);
        EXEC(@role);
        """;

    public static async Task RunAsync(string account, string connectionString)
    {
        var identity = new NTAccount(account);

        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Abastalia");
        var folder = Directory.CreateDirectory(data);
        foreach (var sub in new[] { "logs", "backups", "keys" }) folder.CreateSubdirectory(sub);
        // The whole folder is closed (R10 #1, #6): the plain .bak, the logs and update.json (which the SYSTEM update task reads) only for
        // the service, SYSTEM and administrators, not the Users read/write that C:\ProgramData hands down. keys (the store CA and the e-CF key
        // material, protected with machine DPAPI that any local user could open) gets the same closed ACL of its own (TK-242).
        folder.SetAccessControl(Closed(identity));
        new DirectoryInfo(Path.Combine(data, "keys")).SetAccessControl(Closed(identity));
        Console.WriteLine($"Carpeta de datos: {data}");

        // Connect as the administrator running the installer, to the server only (the database does not exist yet).
        var server = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", IntegratedSecurity = true };
        await using var sql = new SqlConnection(server.ConnectionString);
        await sql.OpenAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = LoginSql;
        command.Parameters.AddWithValue("@account", account);
        await command.ExecuteNonQueryAsync();
        Console.WriteLine($"Login de SQL Server listo para {account} en {server.DataSource}");

        // SQL Server writes the plain .bak where Abastalia can read and delete it (its own Backup folder is closed to Abastalia's account).
        await using var service = sql.CreateCommand();
        service.CommandText = "SELECT TOP 1 service_account FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server (%'";
        var sqlAccount = (string)(await service.ExecuteScalarAsync())!;
        var work = folder.CreateSubdirectory(Path.Combine("backups", "work"));
        work.SetAccessControl(Allow(work.GetAccessControl(), new NTAccount(sqlAccount), FileSystemRights.Modify));
        Console.WriteLine($"SQL Server ({sqlAccount}) puede escribir respaldos en {work.FullName}");
    }

    private static DirectorySecurity Closed(IdentityReference service)
    {
        var closed = new DirectorySecurity();
        closed.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        Allow(closed, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
        Allow(closed, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl);
        Allow(closed, service, FileSystemRights.Modify);
        return closed;
    }

    private static T Allow<T>(T security, IdentityReference account, FileSystemRights rights) where T : FileSystemSecurity
    {
        var inheritance = security is DirectorySecurity ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        security.AddAccessRule(new FileSystemAccessRule(account, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
