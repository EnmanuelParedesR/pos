#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs Abastalia on the store's main PC as a Windows service that runs without admin rights.
.DESCRIPTION
  1. Copies the published API to C:\Program Files\Abastalia.
  2. Creates the "Abastalia" service under its own virtual account (NT SERVICE\Abastalia), delayed start, restart on failure,
     with only SeChangeNotifyPrivilege (R11 C7).
  3. Runs `Abastalia.Host.Local prepare-service`: C:\ProgramData\Abastalia for logs, backups, keys and the license, and the SQL Server login of the
     service in the instance of the connection string (.\ABASTALIA, which the setup installs; R11 C1).
  4. Opens TCP 5443 (HTTPS) in the firewall for private networks only and for the server's program only, and starts the service.
  Publish first:  dotnet publish src/Hosts/Abastalia.Host.Local -c Release -r win-x64 --self-contained -o publish
.EXAMPLE
  .\deploy\install.ps1 -Source .\publish
#>
param(
    [Parameter(Mandatory)] [string] $Source,
    [string] $Destination = "$env:ProgramFiles\Abastalia",
    [string] $Service = "Abastalia"
)
$ErrorActionPreference = 'Stop'
$account = "NT SERVICE\$Service"
$exe = Join-Path $Destination 'Abastalia.Host.Local.exe'

if (Get-Service $Service -ErrorAction SilentlyContinue) { Stop-Service $Service -Force }
New-Item -ItemType Directory -Force $Destination | Out-Null
# The setup (deploy/installer/abastalia.iss) already put the files in place: then there is nothing to copy.
if ((Resolve-Path $Source).Path.TrimEnd('\') -ne (Resolve-Path $Destination).Path.TrimEnd('\')) {
    Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
}

if (-not (Get-Service $Service -ErrorAction SilentlyContinue)) {
    sc.exe create $Service binPath= "`"$exe`"" start= delayed-auto obj= $account DisplayName= "Abastalia (punto de venta)" | Out-Null
    sc.exe description $Service "API de Abastalia para las cajas de la tienda." | Out-Null
}
sc.exe failure $Service reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
# R11 C7: only the privilege it needs. A service keeps SeImpersonatePrivilege by default, which turns any code run inside it into SYSTEM.
sc.exe privs $Service SeChangeNotifyPrivilege | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe privs falló ($LASTEXITCODE)" }

& $exe prepare-service $account
if ($LASTEXITCODE -ne 0) { throw "prepare-service falló ($LASTEXITCODE)" }

# TK-359 (ADR-0016): only the server's own program may take 5443; a rule left by an older install is rewritten the same way.
if (Get-NetFirewallRule -DisplayName 'Abastalia HTTPS' -ErrorAction SilentlyContinue) {
    Set-NetFirewallRule -DisplayName 'Abastalia HTTPS' -Direction Inbound -Protocol TCP -LocalPort 5443 -Action Allow -Profile Private -Program $exe
} else {
    New-NetFirewallRule -DisplayName 'Abastalia HTTPS' -Direction Inbound -Protocol TCP -LocalPort 5443 -Action Allow -Profile Private -Program $exe | Out-Null
}

Start-Service $Service
Write-Host "Abastalia está corriendo. Pruebe: https://localhost:5443/health"
