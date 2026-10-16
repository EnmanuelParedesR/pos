#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Uninstall with "borrar datos" ticked (TK-242): deletes the Toki database and C:\ProgramData\Toki (backups, license, keys, logs).
  Cannot be undone. Without it, uninstalling keeps everything so a reinstall or a move to a new PC finds the data.
  First (R10 #9) it copies the newest encrypted backup (.toki-backup, opens with the store's backup phrase) to $BackupTo\Toki - ultimo respaldo,
  outside ProgramData. There is no backup command outside the running service, so it is the last one the service made.
  Exit 2 = no backup to copy or the copy failed, and nothing was deleted. -NoFinalBackup deletes without that copy.
#>
param([string] $Server = '.\SQLEXPRESS', [string] $Database = 'Toki',
    [string] $BackupTo = [Environment]::GetFolderPath('Desktop'), [switch] $NoFinalBackup)
$ErrorActionPreference = 'Continue'
$data = Join-Path $env:ProgramData 'Toki'

if (-not $NoFinalBackup) {
    $latest = Get-ChildItem (Join-Path $data 'backups') -Filter '*.toki-backup' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $target = Join-Path $BackupTo 'Toki - ultimo respaldo'
    if (-not $latest -or -not $BackupTo -or $target.StartsWith($env:ProgramData, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning 'No hay un respaldo de Toki para guardar fuera de ProgramData. No se borró nada (-NoFinalBackup borra sin él).'
        exit 2
    }
    try {
        New-Item $target -ItemType Directory -Force -ErrorAction Stop | Out-Null
        Copy-Item $latest.FullName $target -Force -ErrorAction Stop
    } catch {
        Write-Warning "No se pudo copiar el último respaldo a ${target}: $_. No se borró nada."
        exit 2
    }
    Write-Host "Último respaldo guardado en $(Join-Path $target $latest.Name). Se abre con la frase de los respaldos."
}

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

Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Datos de Toki borrados.'
