#Requires -Version 7
<#
.SYNOPSIS
  Builds TokiSetup-<version>.exe (TK-242): publishes both apps self-contained, signs them and compiles toki.iss.
.DESCRIPTION
  Needs on the build machine (none is downloaded by this script):
    - .NET SDK (global.json)
    - Inno Setup 6 (ISCC.exe), https://jrsoftware.org/isinfo.php
    - Windows SDK signtool.exe, for a signed release
  Without ISCC it publishes what it can and says what is missing.
  Output: artifacts\installer\TokiSetup-<version>.exe, and artifacts\releases\ (the feed: releases.<channel>.json, the setup and its
  TokiSetup-<version>.exe.sha256). The feed must be https. Upload artifacts\releases\* to the feed URL (GitHub Release or an Azure Blob
  container); stores read it through "Updates": { "FeedUrl" } in both appsettings, and both PC roles update from it with the SYSTEM task.
  Signing (R11 C3, docs/instalador-web.md "Firma de código"):
    TOKI_SIGNTOOL        the signtool command line without the file, e.g.
                         signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /sha1 <THUMBPRINT>
                         It signs Toki.exe, Toki.Host.Local.exe and (through Inno's SignTool=toki) the setup and its uninstaller.
    TOKI_SIGN_THUMBPRINT the certificate's SHA-1 thumbprint(s), comma-separated: written into both appsettings (Updates:SignerThumbprints),
                         so installed PCs only accept setups signed by it. Without it, installed PCs install no update at all.
  -RequireSigned refuses to build a release without both (use it for every release that leaves the office).
.EXAMPLE
  .\deploy\installer\build.ps1 -Version 1.0.0 -Channel pilot -FeedUrl https://tokireleases.blob.core.windows.net/releases -RequireSigned
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet('stable', 'pilot')] [string] $Channel = 'stable',
    [string] $FeedUrl = '',
    # Empty: the production cloud (store-settings.ps1).
    [string] $CloudUrl = '',
    [switch] $RequireSigned
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "La versión tiene que ser x.y.z: $Version" }
if ($FeedUrl -and -not $FeedUrl.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) { throw 'El feed tiene que ser https.' }
if ($RequireSigned -and -not ($env:TOKI_SIGNTOOL -and $env:TOKI_SIGN_THUMBPRINT)) { throw 'Falta TOKI_SIGNTOOL o TOKI_SIGN_THUMBPRINT: no se arma una versión sin firma.' }
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$publish = Join-Path $root 'artifacts\publish'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

function Publish($project, $out) {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -p:Version=$Version -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project falló" }
}
Publish 'src\Hosts\Toki.Host.Local' (Join-Path $publish 'server')
Publish 'src\Client\Toki.Client.Wpf' (Join-Path $publish 'app')
# TK-251: the app reaches the server on https 5443, the server the production cloud; the feed, channel and signer go into both.
. (Join-Path $PSScriptRoot 'store-settings.ps1')
$cloud = if ($CloudUrl) { $CloudUrl } else { $ProductionCloudUrl }
$thumbprints = @(($env:TOKI_SIGN_THUMBPRINT ?? '') -split ',' | ForEach-Object { $_.Trim().ToUpperInvariant() } | Where-Object { $_ })
Set-StoreSettings -AppSettings (Join-Path $publish 'app\appsettings.json') -ServerSettings (Join-Path $publish 'server\appsettings.json') `
    -FeedUrl $FeedUrl -Channel $Channel -CloudUrl $cloud -SignerThumbprints $thumbprints

if ($env:TOKI_SIGNTOOL) {
    foreach ($exe in (Join-Path $publish 'app\Toki.exe'), (Join-Path $publish 'server\Toki.Host.Local.exe')) {
        cmd /c "$env:TOKI_SIGNTOOL `"$exe`""
        if ($LASTEXITCODE -ne 0) { throw "No se pudo firmar $exe" }
    }
} else {
    Write-Warning 'Sin TOKI_SIGNTOOL: los programas y el instalador quedan sin firma (solo para pruebas).'
}

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source ?? "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    Write-Warning "Falta Inno Setup 6 (ISCC.exe). Instálelo y compile: ISCC /DAppVersion=$Version /DPublishDir=$publish deploy\installer\toki.iss"
    return
}
$sign = if ($env:TOKI_SIGNTOOL) { "/Stoki=$env:TOKI_SIGNTOOL `$f", '/DSigned' } else { @() }
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$(Join-Path $root 'artifacts\installer')" @sign (Join-Path $PSScriptRoot 'toki.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC falló' }

# The feed: one full release per channel file (ServerUpdateCheck and update-service.ps1 -Role register read the newest), the setup and its SHA-256.
$releases = Join-Path $root 'artifacts\releases'
New-Item -ItemType Directory -Force $releases | Out-Null
$setup = Join-Path $releases "TokiSetup-$Version.exe"
Copy-Item (Join-Path $root "artifacts\installer\TokiSetup-$Version.exe") $setup
"$((Get-FileHash $setup -Algorithm SHA256).Hash)  TokiSetup-$Version.exe" | Set-Content "$setup.sha256" -Encoding ascii
@{ Assets = @(@{ Type = 'Full'; Version = $Version; FileName = "TokiSetup-$Version.exe" }) } | ConvertTo-Json -Depth 5 |
    Set-Content (Join-Path $releases "releases.$Channel.json") -Encoding utf8
Write-Host "Listo: artifacts\installer\TokiSetup-$Version.exe y el feed en artifacts\releases."
