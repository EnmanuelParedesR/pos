#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the Toki service and its firewall rule. The database, C:\ProgramData\Toki (logs, backups, license)
  and the node certificate stay: reinstalling keeps the store's data and the registers' pinned certificate.
#>
param([string] $Service = "Toki", [string] $Destination = "$env:ProgramFiles\Toki")
$ErrorActionPreference = 'Stop'
if (Get-Service $Service -ErrorAction SilentlyContinue) {
    Stop-Service $Service -Force
    sc.exe delete $Service | Out-Null
}
Get-NetFirewallRule -DisplayName 'Toki HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
Write-Host "Toki se quitó. Los datos siguen en SQL Server y en C:\ProgramData\Toki."
