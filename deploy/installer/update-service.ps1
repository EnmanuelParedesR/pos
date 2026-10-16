#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Applies a store-server update when the service says it may (TK-242). Run hourly by the "Toki\Actualizar servidor" task the setup creates.
.DESCRIPTION
  The service (ServerUpdateCheck, only with Updates:FeedUrl set) writes C:\ProgramData\Toki\update.json when the feed has a newer version,
  with canApplyNow = no shift open or outside business hours. This script downloads that TokiSetup-<version>.exe, checks its signature
  and runs it silently as "PC principal": the setup stops the service, replaces the files and starts it again. Data is never touched.
.PARAMETER AllowUnsigned
  Only until the Authenticode certificate exists (docs/instalador-web.md): accepts a setup without a valid signature.
#>
param([switch] $AllowUnsigned)
$ErrorActionPreference = 'Stop'
$marker = Join-Path $env:ProgramData 'Toki\update.json'
if (-not (Test-Path $marker)) { return }
$update = Get-Content $marker -Raw | ConvertFrom-Json

# Stale (the service stopped checking) or not now: wait for the next check.
if (-not $update.CanApplyNow -or ([datetime]::UtcNow - [datetime]$update.CheckedUtc).TotalMinutes -gt 90) { return }

$setup = Join-Path $env:TEMP "TokiSetup-$($update.Version).exe"
Invoke-WebRequest -Uri $update.SetupUrl -OutFile $setup -UseBasicParsing
if ((Get-AuthenticodeSignature $setup).Status -ne 'Valid' -and -not $AllowUnsigned) {
    Remove-Item $setup
    throw "La actualización $($update.Version) no tiene una firma válida: no se instala."
}
$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TYPE=server' -Wait -PassThru
Remove-Item $setup
if ($p.ExitCode -ne 0) { throw "La instalación de $($update.Version) terminó con el código $($p.ExitCode)." }
Remove-Item $marker -ErrorAction SilentlyContinue
Write-Host "Toki $($update.Version) instalado."
