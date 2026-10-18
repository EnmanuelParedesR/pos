# TK-324 (ADR-0013): turns a business of the panel into a Lite business (sold from venta.abastalia.com), until TK-325 does it on its own.
# Runs as an Entra admin of the SQL server who can read the vault's Lite--ProvisioningKey. Nothing secret is printed or written.
#
#   pwsh -File deploy/azure/lite-provision.ps1 -Email dueno@ejemplo.com -ConnectionCode 12345678
#
# -ConnectionCode: Panel -> Tiendas -> "Conectar una tienda" (8 digits, 10 minutes). With it the business also becomes its own node of
# the cloud (sales reach the panel). The owner's PIN is asked for and only travels to Host.Lite.
param(
    [Parameter(Mandatory)] [string] $Email,
    [string] $ConnectionCode,
    [string] $OwnerCode = 'dueno',
    [string] $ResourceGroup = 'rg-abastalia-prod',
    [string] $SqlServer = 'sql-abastalia-prod-eigklc',
    [string] $Database = 'abastalia',
    [string] $Vault = 'kv-abastalia-prod-5qsy5n',
    [string] $LiteUrl = 'https://app-abastalia-prod-lite-5qsy5n.azurewebsites.net'
)
$ErrorActionPreference = 'Stop'

if (-not (Get-Module -ListAvailable SqlServer)) { Install-Module SqlServer -Scope CurrentUser -Force -AllowClobber }
Import-Module SqlServer

# The panel's businesses where this e-mail is the owner: the Lite business keeps the cloud's TenantId (ADR-0013, decision 1).
$ip = (Invoke-RestMethod https://api.ipify.org)
$rule = 'temp-lite-provision'
az sql server firewall-rule create -g $ResourceGroup -s $SqlServer -n $rule --start-ip-address $ip --end-ip-address $ip -o none
try {
    $token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
    $query = @"
SELECT t.Id, t.Name, t.[Plan] FROM cloud.AspNetUsers u
JOIN cloud.TenantMembers m ON m.UserId = u.Id AND m.Role = 'Owner'
JOIN cloud.Tenants t ON t.Id = m.TenantId AND t.DisabledUtc IS NULL
WHERE u.NormalizedEmail = UPPER(@email)
"@
    $rows = @(Invoke-Sqlcmd -ServerInstance "$SqlServer.database.windows.net" -Database $Database -AccessToken $token `
        -Query ($query -replace '@email', "'$($Email.Replace("'", "''"))'") -Encrypt Mandatory)
}
finally {
    az sql server firewall-rule delete -g $ResourceGroup -s $SqlServer -n $rule -o none
}

if ($rows.Count -eq 0) { throw "Ningún negocio tiene a $Email como dueño (o la cuenta no existe)." }
for ($i = 0; $i -lt $rows.Count; $i++) { Write-Host "[$($i + 1)] $($rows[$i].Name)  ($($rows[$i].Plan))  $($rows[$i].Id)" }
$pick = if ($rows.Count -eq 1) { 0 } else { [int](Read-Host 'Número del negocio que pasa a Lite') - 1 }
$tenant = $rows[$pick]

$ownerName = Read-Host "Nombre del dueño en la caja (Enter: $($tenant.Name))"
if ([string]::IsNullOrWhiteSpace($ownerName)) { $ownerName = $tenant.Name }
$secure = Read-Host 'PIN del dueño (4 a 8 dígitos)' -AsSecureString
$pin = [Net.NetworkCredential]::new('', $secure).Password
if ($pin -notmatch '^\d{4,8}$') { throw 'El PIN tiene que ser de 4 a 8 dígitos.' }

$key = az keyvault secret show --vault-name $Vault -n Lite--ProvisioningKey --query value -o tsv
$body = @{ tenantId = $tenant.Id; ownerName = $ownerName; ownerCode = $OwnerCode; pin = $pin }
if ($ConnectionCode) { $body.connectionCode = $ConnectionCode }
try {
    $r = Invoke-RestMethod -Method Post -Uri "$LiteUrl/internal/tenants" -Headers @{ 'X-Provisioning-Key' = $key } `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
}
finally { $key = $null; $pin = $null }

Write-Host "Listo: $($tenant.Name) vende en https://venta.abastalia.com (dueño '$OwnerCode')."
if ($r.cloudError) { Write-Warning "El negocio quedó en Lite, pero no se unió a la nube ($($r.cloudError)): sus ventas no llegan al panel todavía." }
Write-Host 'Siguiente: Panel -> Tiendas -> Dispositivo de venta -> Unir un dispositivo, y ese código en venta.abastalia.com.'
