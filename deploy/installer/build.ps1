#Requires -Version 7
<#
.SYNOPSIS
  Builds TokiSetup-<version>.exe (TK-242): publishes both apps self-contained, packs the register app with Velopack and compiles toki.iss.
.DESCRIPTION
  Needs on the build machine (none is downloaded by this script):
    - .NET SDK (global.json)
    - vpk, Velopack's CLI:  dotnet tool install -g vpk
    - Inno Setup 6 (ISCC.exe), https://jrsoftware.org/isinfo.php
  Without vpk or ISCC it publishes what it can and says what is missing.
  Output: artifacts\installer\TokiSetup-<version>.exe, and artifacts\releases\ (the Velopack feed: releases.<channel>.json + packages).
  Release feed: upload artifacts\releases\* and the setup to the feed URL (GitHub Release or an Azure Blob container); stores read it
  through "Updates": { "FeedUrl" } in both appsettings.
  Signing: set TOKI_SIGNTOOL to the signtool command (see toki.iss) once the Authenticode certificate exists.
.EXAMPLE
  .\deploy\installer\build.ps1 -Version 1.0.0 -Channel pilot -FeedUrl https://tokireleases.blob.core.windows.net/releases
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet('stable', 'pilot')] [string] $Channel = 'stable',
    [string] $FeedUrl = ''
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$publish = Join-Path $root 'artifacts\publish'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

function Publish($project, $out) {
    dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained -p:Version=$Version -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project falló" }
    # The feed and the channel go into the published settings; empty FeedUrl = no update checks.
    $settings = Join-Path $out 'appsettings.json'
    $json = Get-Content $settings -Raw | ConvertFrom-Json
    $json | Add-Member -Force -NotePropertyName Updates -NotePropertyValue ([pscustomobject]@{ FeedUrl = $FeedUrl; Channel = $Channel })
    $json | ConvertTo-Json -Depth 20 | Set-Content $settings -Encoding utf8
}
Publish 'src\Hosts\Toki.Host.Local' (Join-Path $publish 'server')
Publish 'src\Client\Toki.Client.Wpf' (Join-Path $publish 'app')

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Warning 'Falta vpk (dotnet tool install -g vpk): no se empaquetó la app de caja ni el instalador.'
    return
}
$releases = Join-Path $root 'artifacts\releases'
vpk pack --packId Toki --packVersion $Version --packDir (Join-Path $publish 'app') --mainExe Toki.exe --channel $Channel --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw 'vpk pack falló' }
New-Item -ItemType Directory -Force (Join-Path $publish 'app-setup') | Out-Null
Copy-Item (Join-Path $releases "Toki-$Channel-Setup.exe") (Join-Path $publish 'app-setup\Toki-Setup.exe')

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source ?? "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) {
    Write-Warning "Falta Inno Setup 6 (ISCC.exe). Instálelo y compile: ISCC /DAppVersion=$Version /DPublishDir=$publish deploy\installer\toki.iss"
    return
}
$sign = if ($env:TOKI_SIGNTOOL) { "/Stoki=$env:TOKI_SIGNTOOL" } else { $null }
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" "/DOutputDir=$(Join-Path $root 'artifacts\installer')" $sign (Join-Path $PSScriptRoot 'toki.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC falló' }
Copy-Item (Join-Path $root "artifacts\installer\TokiSetup-$Version.exe") $releases
Write-Host "Listo: artifacts\installer\TokiSetup-$Version.exe y el feed en artifacts\releases."
