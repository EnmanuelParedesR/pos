<#
.SYNOPSIS
  Applies a Toki update (TK-242). Run hourly as SYSTEM by the "Toki\Actualizar" task the setup creates, on the main PC (-Role server)
  and on each register PC (-Role register, R11 C6: the register app is installed for the whole machine and updated like the server).
.DESCRIPTION
  Server: first it writes disk-encryption.json for the service's health check (R11 C2). The service (ServerUpdateCheck, only with Updates:FeedUrl set) writes C:\ProgramData\Toki\update.json when the feed has a newer
  version, with canApplyNow = no shift open or outside business hours. update.json is only a hint (R10 #1).
  Register: the script reads releases.<channel>.json from the feed itself and updates only while the register app is closed.
  Both: the setup comes from the https FeedUrl of the INSTALLED appsettings.json (only administrators can change it), the version is plain
  x.y.z and newer than the installed one (R11 C13), and (R11 C3) the file must carry a valid Authenticode signature by a certificate listed
  in Updates:SignerThumbprints. Without thumbprints nothing is installed, unless Updates:AllowUnsignedUpdates is true (pilot only: then the
  feed's SHA-256 is accepted, which proves nothing against someone who controls the feed). It is downloaded to a folder only SYSTEM can open
  and run silently with the same role. Data is never touched. Errors go to the Windows Application event log (source "Toki").
.PARAMETER DataRoot
  Where the download folder (and, for the server, update.json) lives. Tests point it elsewhere.
.PARAMETER SettingsPath
  The installed settings: ..\server\appsettings.json (server) or ..\app\appsettings.json (register) next to this script.
.PARAMETER InstalledVersion
  Only for tests; by default the file version of the installed Toki.Host.Local.exe or Toki.exe.
.PARAMETER PolicyPath
  Register only (TK-254): update-policy.json, written by the register app with the store's channel (pilot/stable, chosen in Configuración →
  Actualizaciones) and the cloud's cap (MaxVersion; "" = wait). Any local user can write it, so it only picks among the feed's releases or
  holds back: the feed, the version checks and the signature checks below do not depend on it.
#>
param(
    [ValidateSet('server', 'register')] [string] $Role = 'server',
    [string] $DataRoot = $(if ($Role -eq 'server') { Join-Path $env:ProgramData 'Toki' } else { Join-Path $PSScriptRoot '..' }),
    [string] $SettingsPath = (Join-Path $PSScriptRoot "..\$(if ($Role -eq 'server') { 'server' } else { 'app' })\appsettings.json"),
    [string] $InstalledVersion = '',
    [string] $PolicyPath = (Join-Path $env:ProgramData 'Toki\update-policy.json')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'remove-tree.ps1')

function Write-Log([string] $message, [string] $type) {
    try {
        if (-not [Diagnostics.EventLog]::SourceExists('Toki')) { New-EventLog -LogName Application -Source 'Toki' }
        Write-EventLog -LogName Application -Source 'Toki' -EventId 1001 -EntryType $type -Message $message
    } catch { } # not elevated (tests): the exit code and stderr still say it
}

function Get-Installed {
    if ($InstalledVersion) { return [version]$InstalledVersion }
    $exe = Join-Path (Split-Path $SettingsPath) $(if ($Role -eq 'server') { 'Toki.Host.Local.exe' } else { 'Toki.exe' })
    if (-not (Test-Path -LiteralPath $exe)) { return [version]'0.0.0' }
    $v = [version](Get-Item -LiteralPath $exe).VersionInfo.FileVersion
    [version]"$($v.Major).$($v.Minor).$($v.Build)"
}

try {
    $settings = Get-Content $SettingsPath -Raw | ConvertFrom-Json
    $feed = ([string]$settings.Updates.FeedUrl).TrimEnd('/')
    $sha256 = ''
    if ($Role -eq 'server') {
        # R11 C2: whether the system drive is encrypted (BitLocker or Device Encryption). Only an administrator or SYSTEM may ask Windows;
        # the service reads this file for its health check ("disk-encryption").
        $volume = Get-CimInstance -Namespace root/cimv2/Security/MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -ErrorAction SilentlyContinue |
            Where-Object DriveLetter -eq $env:SystemDrive
        @{ ProtectionStatus = $volume.ProtectionStatus; CheckedUtc = [datetime]::UtcNow.ToString('o') } | ConvertTo-Json |
            Set-Content (Join-Path $DataRoot 'disk-encryption.json') -Encoding ascii
        $marker = Join-Path $DataRoot 'update.json'
        if (-not (Test-Path $marker)) { return }
        $update = Get-Content $marker -Raw | ConvertFrom-Json
        # Stale (the service stopped checking) or not now: wait for the next check. CheckedUtc is ISO 8601 with Z (R10 #4).
        $checked = if ($update.CheckedUtc -is [datetime]) { $update.CheckedUtc.ToUniversalTime() }
                   else { [DateTimeOffset]::Parse([string]$update.CheckedUtc, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime }
        if (-not $update.CanApplyNow -or ([datetime]::UtcNow - $checked).TotalMinutes -gt 90) { return }
        $version = [string]$update.Version
        $sha256 = [string]$update.Sha256
        if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "update.json trae una versión inválida: '$version'." }
        if (-not $feed) { throw "El servidor instalado no tiene Updates:FeedUrl." }
        if ([string]$update.SetupUrl -ne "$feed/TokiSetup-$version.exe") { throw "update.json pide $($update.SetupUrl), que no es $feed/TokiSetup-$version.exe." }
    } else {
        if (-not $feed) { return } # no feed configured: no updates
        if (Get-Process -Name 'Toki' -ErrorAction SilentlyContinue) { return } # never in the middle of a sale: next hour
    }
    if (-not $feed.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) { throw "Updates:FeedUrl tiene que ser https." }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    if ($Role -eq 'register') {
        $channel = if ($settings.Updates.Channel) { [string]$settings.Updates.Channel } else { 'stable' }
        $max = $null
        if (Test-Path -LiteralPath $PolicyPath) {
            $policy = try { Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json } catch { $null }
            if ([string]$policy.Channel -in 'pilot', 'stable') { $channel = [string]$policy.Channel }
            if ($null -ne $policy.MaxVersion) {
                if ([string]$policy.MaxVersion -notmatch '^\d+\.\d+\.\d+$') { return } # the cloud says wait
                $max = [version][string]$policy.MaxVersion
            }
        }
        $releases = (New-Object Net.WebClient).DownloadString("$feed/releases.$channel.json") | ConvertFrom-Json
        $version = @($releases.Assets | Where-Object { $_.Type -eq 'Full' -and [string]$_.Version -match '^\d+\.\d+\.\d+$' } |
            ForEach-Object { [version][string]$_.Version } | Where-Object { -not $max -or $_ -le $max } | Sort-Object -Descending)[0]
        if (-not $version -or $version -le (Get-Installed)) { return }
        $version = $version.ToString(3)
    }
    if ([version]$version -le (Get-Installed)) { throw "La versión $version no es más nueva que la instalada ($(Get-Installed)): no se instala." }

    $thumbprints = @($settings.Updates.SignerThumbprints | Where-Object { $_ })
    $unsigned = $settings.Updates.AllowUnsignedUpdates -eq $true
    if ($thumbprints.Count -eq 0 -and -not $unsigned) { throw "No hay certificado de Toki configurado (Updates:SignerThumbprints): la versión $version no se instala." }
    if ($thumbprints.Count -eq 0 -and $sha256 -notmatch '^[0-9A-Fa-f]{64}$' -and $Role -eq 'server') { throw "La versión $version no trae SHA-256." }

    # A folder only SYSTEM can open, created fresh (nothing someone else left there is reused, and a planted link is refused, R11 C13).
    $folder = Join-Path $DataRoot 'updates'
    Remove-Tree $folder
    New-Item -ItemType Directory -Force $folder | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-18')),
        'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl $folder $acl

    $setup = Join-Path $folder "TokiSetup-$version.exe"
    (New-Object Net.WebClient).DownloadFile("$feed/TokiSetup-$version.exe", $setup)
    if ($Role -eq 'register' -and $thumbprints.Count -eq 0) { $sha256 = ((New-Object Net.WebClient).DownloadString("$feed/TokiSetup-$version.exe.sha256") -split '\s+')[0] }
    $signature = Get-AuthenticodeSignature $setup
    $signed = $signature.Status -eq 'Valid' -and $thumbprints -contains $signature.SignerCertificate.Thumbprint
    $hashed = $unsigned -and $thumbprints.Count -eq 0 -and $sha256 -and (Get-FileHash $setup -Algorithm SHA256).Hash -eq $sha256
    if (-not ($signed -or $hashed)) {
        Remove-Item $setup
        throw "La actualización $version no tiene la firma de Toki: no se instala."
    }
    $p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/TYPE=$Role" -Wait -PassThru
    Remove-Item $setup
    if ($p.ExitCode -ne 0) { throw "La instalación de $version terminó con el código $($p.ExitCode)." }
    if ($Role -eq 'server') { Remove-Item (Join-Path $DataRoot 'update.json') -ErrorAction SilentlyContinue }
    Write-Log "Toki $version instalado." Information
} catch {
    Write-Log "No se aplicó la actualización de Toki: $($_.Exception.Message)" Error
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
