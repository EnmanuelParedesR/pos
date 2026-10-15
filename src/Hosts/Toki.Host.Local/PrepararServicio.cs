using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Data.SqlClient;

namespace Toki.Host.Local;

/// <summary>`Toki.Host.Local preparar-servicio "NT SERVICE\Toki"`, run once by the installer as an administrator, so the service itself
/// runs with no admin rights: it creates the node certificate and lets the account read its private key, gives the account the data folders,
/// and creates its SQL Server login (allowed to create the Toki database, which it then owns).</summary>
[SupportedOSPlatform("windows")]
internal static class PrepararServicio
{
    public static async Task EjecutarAsync(string cuenta, string conexion)
    {
        var identidad = new NTAccount(cuenta);
        var certificado = CertificadoNodo.ObtenerOCrear(StoreLocation.LocalMachine, TimeProvider.System);
        var llave = new FileInfo(ArchivoDeLlave(certificado));
        llave.SetAccessControl(Permitir(llave.GetAccessControl(), identidad, FileSystemRights.Read));
        Console.WriteLine($"Certificado del nodo: {certificado.Subject}, huella {certificado.Thumbprint}, vence {certificado.NotAfter:yyyy-MM-dd}");

        var datos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Toki");
        var carpeta = Directory.CreateDirectory(datos);
        foreach (var sub in new[] { "logs", "respaldos", "claves" }) carpeta.CreateSubdirectory(sub);
        carpeta.SetAccessControl(Permitir(carpeta.GetAccessControl(), identidad, FileSystemRights.Modify));
        Console.WriteLine($"Carpeta de datos: {datos}");

        // Connect as the administrator running the installer, to the server only (the database does not exist yet).
        var servidor = new SqlConnectionStringBuilder(conexion) { InitialCatalog = "master", IntegratedSecurity = true };
        await using var sql = new SqlConnection(servidor.ConnectionString);
        await sql.OpenAsync();
        await using var comando = sql.CreateCommand();
        comando.CommandText = """
            IF SUSER_ID(@cuenta) IS NULL EXEC('CREATE LOGIN ' + QUOTENAME(@cuenta) + ' FROM WINDOWS');
            EXEC('ALTER SERVER ROLE dbcreator ADD MEMBER ' + QUOTENAME(@cuenta));
            """;
        comando.Parameters.AddWithValue("@cuenta", cuenta);
        await comando.ExecuteNonQueryAsync();
        Console.WriteLine($"Login de SQL Server listo para {cuenta} en {servidor.DataSource}");

        // SQL Server writes the plain .bak where Toki can read and delete it (its own Backup folder is closed to Toki's account).
        await using var servicio = sql.CreateCommand();
        servicio.CommandText = "SELECT TOP 1 service_account FROM sys.dm_server_services WHERE servicename LIKE 'SQL Server (%'";
        var cuentaSql = (string)(await servicio.ExecuteScalarAsync())!;
        var trabajo = carpeta.CreateSubdirectory(Path.Combine("respaldos", "trabajo"));
        trabajo.SetAccessControl(Permitir(trabajo.GetAccessControl(), new NTAccount(cuentaSql), FileSystemRights.Modify));
        Console.WriteLine($"SQL Server ({cuentaSql}) puede escribir respaldos en {trabajo.FullName}");
    }

    private static T Permitir<T>(T seguridad, NTAccount cuenta, FileSystemRights derechos) where T : FileSystemSecurity
    {
        var herencia = seguridad is DirectorySecurity ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        seguridad.AddAccessRule(new FileSystemAccessRule(cuenta, derechos, herencia, PropagationFlags.None, AccessControlType.Allow));
        return seguridad;
    }

    /// <summary>Where Windows keeps the certificate's machine key: CNG keys and the older CSP keys live in different folders.</summary>
    private static string ArchivoDeLlave(X509Certificate2 certificado)
    {
        var raiz = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto");
        return certificado.GetRSAPrivateKey() switch
        {
            RSACng cng => Path.Combine(raiz, "Keys", cng.Key.UniqueName!),
            RSACryptoServiceProvider csp => Path.Combine(raiz, "RSA", "MachineKeys", csp.CspKeyContainerInfo.UniqueKeyContainerName),
            _ => throw new InvalidOperationException("No se encontró la llave privada del certificado del nodo."),
        };
    }
}
