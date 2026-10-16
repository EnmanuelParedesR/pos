#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Uninstall with "borrar datos" ticked (TK-242): deletes the Toki database and C:\ProgramData\Toki (backups, license, keys, logs).
  Cannot be undone. Without it, uninstalling keeps everything so a reinstall or a move to a new PC finds the data.
#>
param([string] $Server = '.\SQLEXPRESS', [string] $Database = 'Toki')
$ErrorActionPreference = 'Continue'
if (Get-Service Toki -ErrorAction SilentlyContinue) { Stop-Service Toki -Force }

$sql = New-Object System.Data.SqlClient.SqlConnection "Server=$Server;Database=master;Integrated Security=True;TrustServerCertificate=True"
try {
    $sql.Open()
    $cmd = $sql.CreateCommand()
    $cmd.CommandText = "IF DB_ID(@db) IS NOT NULL EXEC('ALTER DATABASE ' + QUOTENAME(@db) + ' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@db))"
    [void]$cmd.Parameters.AddWithValue('@db', $Database)
    [void]$cmd.ExecuteNonQuery()
} catch {
    Write-Warning "No se pudo borrar la base de datos $Database en ${Server}: $_"
} finally {
    $sql.Dispose()
}

Remove-Item (Join-Path $env:ProgramData 'Toki') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Datos de Toki borrados.'
