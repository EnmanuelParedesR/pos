#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the Toki service and its firewall rule. The database, C:\ProgramData\Toki (logs, backups, license)
  and the node certificate stay: reinstalling keeps the store's data and the registers' pinned certificate.
#>
param([string] $Servicio = "Toki", [string] $Destino = "$env:ProgramFiles\Toki")
$ErrorActionPreference = 'Stop'
if (Get-Service $Servicio -ErrorAction SilentlyContinue) {
    Stop-Service $Servicio -Force
    sc.exe delete $Servicio | Out-Null
}
Get-NetFirewallRule -DisplayName 'Toki HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (Test-Path $Destino) { Remove-Item $Destino -Recurse -Force }
Write-Host "Toki se quitó. Los datos siguen en SQL Server y en C:\ProgramData\Toki."
