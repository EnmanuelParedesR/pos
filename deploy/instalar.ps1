#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs Toki on the store's main PC as a Windows service that runs without admin rights.
.DESCRIPTION
  1. Copies the published API to C:\Program Files\Toki.
  2. Creates the "Toki" service under its own virtual account (NT SERVICE\Toki), delayed start, restart on failure.
  3. Runs `Toki.Host.Local preparar-servicio`: node certificate (and read access to its key for the service),
     C:\ProgramData\Toki for logs, backups and the license, and the SQL Server login of the service.
  4. Opens TCP 5443 (HTTPS) in the firewall for private networks only, and starts the service.
  Publish first:  dotnet publish src/Hosts/Toki.Host.Local -c Release -r win-x64 --self-contained -o publicado
.EXAMPLE
  .\deploy\instalar.ps1 -Origen .\publicado
#>
param(
    [Parameter(Mandatory)] [string] $Origen,
    [string] $Destino = "$env:ProgramFiles\Toki",
    [string] $Servicio = "Toki"
)
$ErrorActionPreference = 'Stop'
$cuenta = "NT SERVICE\$Servicio"
$exe = Join-Path $Destino 'Toki.Host.Local.exe'

if (Get-Service $Servicio -ErrorAction SilentlyContinue) { Stop-Service $Servicio -Force }
New-Item -ItemType Directory -Force $Destino | Out-Null
Copy-Item -Path (Join-Path $Origen '*') -Destination $Destino -Recurse -Force

if (-not (Get-Service $Servicio -ErrorAction SilentlyContinue)) {
    sc.exe create $Servicio binPath= "`"$exe`"" start= delayed-auto obj= $cuenta DisplayName= "Toki (punto de venta)" | Out-Null
    sc.exe description $Servicio "API de Toki para las cajas de la tienda." | Out-Null
}
sc.exe failure $Servicio reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

& $exe preparar-servicio $cuenta
if ($LASTEXITCODE -ne 0) { throw "preparar-servicio falló ($LASTEXITCODE)" }

if (-not (Get-NetFirewallRule -DisplayName 'Toki HTTPS' -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName 'Toki HTTPS' -Direction Inbound -Protocol TCP -LocalPort 5443 -Action Allow -Profile Private | Out-Null
}

Start-Service $Servicio
Write-Host "Toki está corriendo. Pruebe: https://localhost:5443/health"
