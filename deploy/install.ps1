#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs Toki on the store's main PC as a Windows service that runs without admin rights.
.DESCRIPTION
  1. Copies the published API to C:\Program Files\Toki.
  2. Creates the "Toki" service under its own virtual account (NT SERVICE\Toki), delayed start, restart on failure.
  3. Runs `Toki.Host.Local prepare-service`: node certificate (and read access to its key for the service),
     C:\ProgramData\Toki for logs, backups and the license, and the SQL Server login of the service.
  4. Opens TCP 5443 (HTTPS) in the firewall for private networks only, and starts the service.
  Publish first:  dotnet publish src/Hosts/Toki.Host.Local -c Release -r win-x64 --self-contained -o publish
.EXAMPLE
  .\deploy\install.ps1 -Source .\publish
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [string] $Destination = "$env:ProgramFiles\Toki",
    [string] $Service = "Toki"
)
$ErrorActionPreference = 'Stop'
$account = "NT SERVICE\$Service"
$exe = Join-Path $Destination 'Toki.Host.Local.exe'

if (Get-Service $Service -ErrorAction SilentlyContinue) { Stop-Service $Service -Force }
New-Item -ItemType Directory -Force $Destination | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force

if (-not (Get-Service $Service -ErrorAction SilentlyContinue)) {
    sc.exe create $Service binPath= "`"$exe`"" start= delayed-auto obj= $account DisplayName= "Toki (punto de venta)" | Out-Null
    sc.exe description $Service "API de Toki para las cajas de la tienda." | Out-Null
}
sc.exe failure $Service reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

& $exe prepare-service $account
if ($LASTEXITCODE -ne 0) { throw "prepare-service falló ($LASTEXITCODE)" }

if (-not (Get-NetFirewallRule -DisplayName 'Toki HTTPS' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'Toki HTTPS' -Direction Inbound -Protocol TCP -LocalPort 5443 -Action Allow -Profile Private | Out-Null
}

Start-Service $Service
Write-Host "Toki está corriendo. Pruebe: https://localhost:5443/health"
