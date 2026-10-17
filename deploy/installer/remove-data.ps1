#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Uninstall with "borrar datos" ticked (TK-242): deletes the Abastalia database and C:\ProgramData\Abastalia (backups, license, keys, logs).
  Cannot be undone. Without it, uninstalling keeps everything so a reinstall or a move to a new PC finds the data.
  First (R10 #9) it copies the newest encrypted backup (.abastalia-backup, opens with the store's backup phrase) to $BackupTo\Abastalia - ultimo respaldo,
  outside ProgramData. There is no backup command outside the running service, so it is the last one the service made, looked up in
  Backup:Folder of the installed server's settings (as Backups.Folder in the Settings module), C:\ProgramData\Abastalia\backups by default.
  Exit 2 = no backup to copy or the copy failed, and nothing was deleted. Exit 3 = something could not be deleted.
  -NoFinalBackup deletes without that copy.
  R11 C9: it also deletes what lives outside that folder: the e-CF certificate and its key (the key of NT SERVICE\Abastalia, with that account's
  profile; a certificate imported into a Windows store, by the thumbprint in keys\ecf-certificate.json), the self-signed "CN=Abastalia Nodo"
  certificate of stores installed before TK-091, and the service's SQL Server login. R11 C13: a link planted in C:\ProgramData\Abastalia stops it.
#>
param([string] $Server = '', [string] $Database = 'Abastalia',
    [string] $BackupTo = [Environment]::GetFolderPath('Desktop'), [switch] $NoFinalBackup,
    [string] $ServerDir = (Join-Path $PSScriptRoot '..\server'), [string] $Service = 'Abastalia')
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'remove-tree.ps1')
$data = Join-Path $env:ProgramData 'Abastalia'
$failed = $false

# Same order as the service reads its configuration: appsettings.json, appsettings.Production.json, then the Section__Key variable.
function Get-Setting([string] $Section, [string] $Key, [string] $Default) {
    $value = $Default
    foreach ($name in 'appsettings.json', 'appsettings.Production.json') {
        $file = Join-Path $ServerDir $name
        if (-not (Test-Path -LiteralPath $file)) { continue }
        try { $found = (Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).$Section.$Key } catch { $found = $null }
        if ($found) { $value = $found }
    }
    $fromVariable = [Environment]::GetEnvironmentVariable("${Section}__$Key", 'Machine')
    if ($fromVariable) { $value = $fromVariable }
    $value
}
function Get-BackupFolder { Get-Setting 'Backup' 'Folder' (Join-Path $data 'backups') }
if (-not $Server) {
    $Server = try { (New-Object System.Data.SqlClient.SqlConnectionStringBuilder (Get-Setting 'Database' 'ConnectionString' 'Server=.\ABASTALIA')).DataSource } catch { '.\ABASTALIA' }
}

if (-not $NoFinalBackup) {
    $latest = Get-ChildItem -LiteralPath (Get-BackupFolder) -Filter '*.abastalia-backup' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $target = Join-Path $BackupTo 'Abastalia - ultimo respaldo'
    if (-not $latest -or -not $BackupTo -or $target.StartsWith($env:ProgramData, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning 'No hay un respaldo de Abastalia para guardar fuera de ProgramData. No se borró nada (-NoFinalBackup borra sin él).'
        exit 2
    }
    try {
        New-Item $target -ItemType Directory -Force -ErrorAction Stop | Out-Null
        Copy-Item -LiteralPath $latest.FullName $target -Force -ErrorAction Stop
    } catch {
        Write-Warning "No se pudo copiar el último respaldo a ${target}: $_. No se borró nada."
        exit 2
    }
    Write-Host "Último respaldo guardado en $(Join-Path $target $latest.Name). Se abre con la frase de los respaldos."
}

if (Get-Service $Service -ErrorAction SilentlyContinue) { Stop-Service $Service -Force }
$account = "NT SERVICE\$Service"

$dropSql = @'
DECLARE @dropDb nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@db);
DECLARE @dropLogin nvarchar(max) = N'DROP LOGIN ' + QUOTENAME(@account);
IF DB_ID(@db) IS NOT NULL EXEC(@dropDb);
IF SUSER_ID(@account) IS NOT NULL EXEC(@dropLogin);
'@
$sql = New-Object System.Data.SqlClient.SqlConnection "Server=$Server;Database=master;Integrated Security=True;TrustServerCertificate=True"
try {
    $sql.Open()
    $cmd = $sql.CreateCommand()
    # EXEC takes only literals and variables: each statement is built in a variable (TK-160).
    $cmd.CommandText = $dropSql
    [void]$cmd.Parameters.AddWithValue('@db', $Database)
    [void]$cmd.Parameters.AddWithValue('@account', $account)
    [void]$cmd.ExecuteNonQuery()
} catch {
    Write-Warning "No se pudo borrar la base de datos $Database o el login $account en ${Server}: $_"
    $failed = $true
} finally {
    $sql.Dispose()
}

# The e-CF certificate: imported by an older version (or by hand) into a Windows store, its thumbprint is in ecf-certificate.json.
$ecf = Join-Path $data 'keys\ecf-certificate.json'
$thumbprint = try { [string](Get-Content -LiteralPath $ecf -Raw | ConvertFrom-Json).Fiscal.Certificate.Thumbprint } catch { '' }
$certificates = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object { ($thumbprint -and $_.Thumbprint -eq $thumbprint) -or
    ($_.Subject -eq 'CN=Abastalia Nodo' -and $_.Issuer -eq 'CN=Abastalia Nodo') })
foreach ($certificate in $certificates) {
    try { Remove-Item -LiteralPath $certificate.PSPath -DeleteKey -ErrorAction Stop }
    catch { Write-Warning "No se pudo borrar el certificado $($certificate.Subject): $_"; $failed = $true }
}
# The service account's own keys and store (the e-CF key since R11 C8, in the TPM or not; CurrentUser\My of older versions) live in its
# profile: deleting the profile deletes them.
$sid = ((sc.exe showsid $Service) -match 'S-1-5-80-[\d-]+' | Select-Object -First 1) -replace '.*(S-1-5-80-[\d-]+).*', '$1'
$serviceProfile = if ($sid) { Get-CimInstance Win32_UserProfile -ErrorAction SilentlyContinue | Where-Object SID -eq $sid }
if ($serviceProfile) {
    # TK-160 (clean VM): the profile stays loaded after the service stops (until a restart), so deleting it fails. The keys go first, on
    # their own: CNG and TPM keys (Crypto), DPAPI master keys (Protect) and the account's certificate store (SystemCertificates).
    foreach ($keys in 'Local\Microsoft\Crypto', 'Roaming\Microsoft\Crypto', 'Roaming\Microsoft\Protect', 'Roaming\Microsoft\SystemCertificates') {
        try { Remove-Tree (Join-Path $serviceProfile.LocalPath "AppData\$keys") }
        catch { Write-Warning "No se pudo borrar la llave de los comprobantes ($keys) de ${account}: $_"; $failed = $true }
    }
    try { $serviceProfile | Remove-CimInstance -ErrorAction Stop }
    catch { Write-Warning "El perfil de $account queda (ya sin llaves) hasta reiniciar la PC; después se puede borrar: $_" }
}

try { Remove-Tree $data } catch { Write-Warning "$_" }
if (Test-Path -LiteralPath $data) {
    Write-Warning "No se pudo borrar por completo $data."
    $failed = $true
}
if ($failed) {
    Write-Warning 'No se pudieron borrar todos los datos de Abastalia.'
    exit 3
}
Write-Host 'Datos de Abastalia borrados.'
