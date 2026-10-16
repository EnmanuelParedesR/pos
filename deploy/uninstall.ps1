#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the Abastalia service and its firewall rule. The database, C:\ProgramData\Abastalia (logs, backups, license)
  and the node certificate stay: reinstalling keeps the store's data and the registers' pinned certificate.
#>
param([string] $Service = "Abastalia", [string] $Destination = "$env:ProgramFiles\Abastalia")
$ErrorActionPreference = 'Stop'
if (Get-Service $Service -ErrorAction SilentlyContinue) {
    Stop-Service $Service -Force
    sc.exe delete $Service | Out-Null
}
Get-NetFirewallRule -DisplayName 'Abastalia HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
Write-Host "Abastalia se quitó. Los datos siguen en SQL Server y en C:\ProgramData\Abastalia."
