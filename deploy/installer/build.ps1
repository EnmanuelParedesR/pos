#Requires -Version 7
<#
.SYNOPSIS
  Builds AbastaliaSetup-<version>.exe (TK-242): publishes both apps self-contained, signs them and compiles abastalia.iss.
.DESCRIPTION
  Needs on the build machine (none is downloaded by this script):
    - .NET SDK (global.json)
    - Inno Setup 6 (ISCC.exe), https://jrsoftware.org/isinfo.php
    - Windows SDK signtool.exe, for a signed release
  Without ISCC it publishes what it can and says what is missing.
  Output: artifacts\installer\AbastaliaSetup-<version>.exe, and artifacts\releases\ (the feed: releases.<channel>.json, the setup and its
  AbastaliaSetup-<version>.exe.sha256). The feed must be https. Upload artifacts\releases\* to the feed URL (GitHub Release or an Azure Blob
  container); stores read it through "Updates": { "FeedUrl" } in both appsettings, and both PC roles update from it with the SYSTEM task.
  Signing (R11 C3, docs/instalador-web.md "Firma de código"):
    ABASTALIA_SIGNTOOL        the signtool command line without the file, e.g.
                         signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /sha1 <THUMBPRINT>
                         It signs Abastalia.exe, Abastalia.Host.Local.exe and (through Inno's SignTool=abastalia) the setup and its uninstaller.
    ABASTALIA_SIGN_THUMBPRINT the certificate's SHA-1 thumbprint(s), comma-separated: written into both appsettings (Updates:SignerThumbprints),
                         so installed PCs only accept setups signed by it. Without it, installed PCs install no update at all.
  -RequireSigned refuses to build a release without both (use it for every release that leaves the office).
  -AllowUnsignedUpdates (pilot only, before the certificate exists) writes Updates:AllowUnsignedUpdates = true into both appsettings: the
  pilot PCs then accept the feed's SHA-256 instead of a signature. It goes in the build because every update replaces the installed
  appsettings (TK-292: set by hand on a PC, it was lost with the first update). Refused with -RequireSigned or a signer thumbprint.
.EXAMPLE
  .\deploy\installer\build.ps1 -Version 1.0.0 -Channel pilot -FeedUrl https://abastaliareleases.blob.core.windows.net/releases -RequireSigned
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet('stable', 'pilot')] [string] $Channel = 'stable',
    [string] $FeedUrl = '',
    # Empty: the production cloud (store-settings.ps1).
    [string] $CloudUrl = '',
    [switch] $RequireSigned,
    [switch] $AllowUnsignedUpdates
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "La versión tiene que ser x.y.z: $Version" }
if ($FeedUrl -and -not $FeedUrl.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) { throw 'El feed tiene que ser https.' }
if ($RequireSigned -and -not ($env:ABASTALIA_SIGNTOOL -and $env:ABASTALIA_SIGN_THUMBPRINT)) { throw 'Falta ABASTALIA_SIGNTOOL o ABASTALIA_SIGN_THUMBPRINT: no se arma una versión sin firma.' }
if ($AllowUnsignedUpdates -and ($RequireSigned -or $env:ABASTALIA_SIGN_THUMBPRINT)) { throw '-AllowUnsignedUpdates es solo para el piloto sin certificado.' }
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$publish = Join-Path $root 'artifacts\publish'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

function Publish($project, $out) {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -p:Version=$Version -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project falló" }
}
Publish 'src\Hosts\Abastalia.Host.Local' (Join-Path $publish 'server')
Publish 'src\Client\Abastalia.Client.Wpf' (Join-Path $publish 'app')
# TK-251: the app reaches the server on https 5443, the server the production cloud; the feed, channel and signer go into both.
. (Join-Path $PSScriptRoot 'store-settings.ps1')
$cloud = if ($CloudUrl) { $CloudUrl } else { $ProductionCloudUrl }
$thumbprints = @(($env:ABASTALIA_SIGN_THUMBPRINT ?? '') -split ',' | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ })
Set-StoreSettings -AppSettings (Join-Path $publish 'app\appsettings.json') -ServerSettings (Join-Path $publish 'server\appsettings.json') `
    -FeedUrl $FeedUrl -Channel $Channel -CloudUrl $cloud -SignerThumbprints $thumbprints -AllowUnsignedUpdates:$AllowUnsignedUpdates

if ($env:ABASTALIA_SIGNTOOL) {
    foreach ($exe in (Join-Path $publish 'app\Abastalia.exe'), (Join-Path $publish 'server\Abastalia.Host.Local.exe')) {
        cmd /c "$env:ABASTALIA_SIGNTOOL `"$exe`""
        if ($LASTEXITCODE -ne 0) { throw "No se pudo firmar $exe" }
    }
} else {
    Write-Warning 'Sin ABASTALIA_SIGNTOOL: los programas y el instalador quedan sin firma (solo para pruebas).'
}

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source ?? "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    Write-Warning "Falta Inno Setup 6 (ISCC.exe). Instálelo y compile: ISCC /DAppVersion=$Version /DPublishDir=$publish deploy\installer\abastalia.iss"
    return
}
$sign = if ($env:ABASTALIA_SIGNTOOL) { "/Sabastalia=$env:ABASTALIA_SIGNTOOL `$f", '/DSigned' } else { @() }
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$(Join-Path $root 'artifacts\installer')" @sign (Join-Path $PSScriptRoot 'abastalia.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC falló' }

# The feed: one full release per channel file (ServerUpdateCheck and update-service.ps1 -Role register read the newest), the setup and its SHA-256.
$releases = Join-Path $root 'artifacts\releases'
New-Item -ItemType Directory -Force $releases | Out-Null
$setup = Join-Path $releases "AbastaliaSetup-$Version.exe"
Copy-Item (Join-Path $root "artifacts\installer\AbastaliaSetup-$Version.exe") $setup
"$((Get-FileHash $setup -Algorithm SHA256).Hash)  AbastaliaSetup-$Version.exe" | Set-Content "$setup.sha256" -Encoding ascii
@{ Assets = @(@{ Type = 'Full'; Version = $Version; FileName = "AbastaliaSetup-$Version.exe" }) } | ConvertTo-Json -Depth 5 |
    Set-Content (Join-Path $releases "releases.$Channel.json") -Encoding utf8
Write-Host "Listo: artifacts\installer\AbastaliaSetup-$Version.exe y el feed en artifacts\releases."
