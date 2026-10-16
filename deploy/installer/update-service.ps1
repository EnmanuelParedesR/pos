<#
.SYNOPSIS
  Applies a store-server update when the service says it may (TK-242). Run hourly as SYSTEM by the "Toki\Actualizar servidor" task the setup creates.
.DESCRIPTION
  The service (ServerUpdateCheck, only with Updates:FeedUrl set) writes C:\ProgramData\Toki\update.json when the feed has a newer version,
  with canApplyNow = no shift open or outside business hours, and the setup's SHA-256 published next to it in the feed.
  update.json is only a hint (R10 #1): the setup must come from the FeedUrl of the INSTALLED appsettings.json (only administrators can
  change it), the version must be plain x.y.z, and the file must be signed by a certificate listed in Updates:SignerThumbprints or match
  the feed's SHA-256. It is downloaded to a folder only SYSTEM can open and run silently as "PC principal": the setup stops the service,
  replaces the files and starts it again. Data is never touched. Errors go to the Windows Application event log (source "Toki").
.PARAMETER DataRoot
  Only for tests: the folder with update.json (default C:\ProgramData\Toki).
.PARAMETER SettingsPath
  Only for tests: the installed server settings (default ..\server\appsettings.json next to this script).
#>
param(
    [string] $DataRoot = (Join-Path $env:ProgramData 'Toki'),
    [string] $SettingsPath = (Join-Path $PSScriptRoot '..\server\appsettings.json')
)
$ErrorActionPreference = 'Stop'

function Write-Log([string] $message, [string] $type) {
    try {
        if (-not [Diagnostics.EventLog]::SourceExists('Toki')) { New-EventLog -LogName Application -Source 'Toki' }
        Write-EventLog -LogName Application -Source 'Toki' -EventId 1001 -EntryType $type -Message $message
    } catch { } # not elevated (tests): the exit code and stderr still say it
}

try {
    $marker = Join-Path $DataRoot 'update.json'
    if (-not (Test-Path $marker)) { return }
    $update = Get-Content $marker -Raw | ConvertFrom-Json

    # Stale (the service stopped checking) or not now: wait for the next check. CheckedUtc is ISO 8601 with Z (R10 #4).
    $checked = if ($update.CheckedUtc -is [datetime]) { $update.CheckedUtc.ToUniversalTime() }
               else { [DateTimeOffset]::Parse([string]$update.CheckedUtc, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime }
    if (-not $update.CanApplyNow -or ([datetime]::UtcNow - $checked).TotalMinutes -gt 90) { return }

    $version = [string]$update.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "update.json trae una versión inválida: '$version'." }
    $settings = Get-Content $SettingsPath -Raw | ConvertFrom-Json
    $feed = ([string]$settings.Updates.FeedUrl).TrimEnd('/')
    if (-not $feed) { throw "El servidor instalado no tiene Updates:FeedUrl." }
    $expected = "$feed/TokiSetup-$version.exe"
    if ([string]$update.SetupUrl -ne $expected) { throw "update.json pide $($update.SetupUrl), que no es $expected." }
    $thumbprints = @($settings.Updates.SignerThumbprints | Where-Object { $_ })
    $sha256 = [string]$update.Sha256
    if ($thumbprints.Count -eq 0 -and $sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw "La versión $version no trae SHA-256 ni hay certificado de Toki configurado." }

    # A folder only SYSTEM can open, created fresh (nothing someone else left there is reused).
    $folder = Join-Path $DataRoot 'updates'
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
    New-Item -ItemType Directory $folder | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-18')),
        'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl $folder $acl

    $setup = Join-Path $folder "TokiSetup-$version.exe"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    (New-Object Net.WebClient).DownloadFile($expected, $setup)
    $signature = Get-AuthenticodeSignature $setup
    $signed = $signature.Status -eq 'Valid' -and $thumbprints -contains $signature.SignerCertificate.Thumbprint
    $hashed = $sha256 -and (Get-FileHash $setup -Algorithm SHA256).Hash -eq $sha256
    if (-not ($signed -or $hashed)) {
        Remove-Item $setup
        throw "La actualización $version no tiene la firma de Toki ni el SHA-256 del feed: no se instala."
    }
    $p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TYPE=server' -Wait -PassThru
    Remove-Item $setup
    if ($p.ExitCode -ne 0) { throw "La instalación de $version terminó con el código $($p.ExitCode)." }
    Remove-Item $marker -ErrorAction SilentlyContinue
    Write-Log "Toki $version instalado." Information
} catch {
    Write-Log "No se aplicó la actualización del servidor: $($_.Exception.Message)" Error
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
