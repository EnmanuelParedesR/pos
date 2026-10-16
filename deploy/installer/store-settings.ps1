<#
.SYNOPSIS
  TK-251: what an installed store needs in its published appsettings.json. build.ps1 dot-sources this file and calls Set-StoreSettings.
.DESCRIPTION
  - The register app talks to the server on the same PC: https on 5443 (Kestrel in the server's appsettings.Production.json). A register
    on another PC is pointed at the main PC when it joins the store ("Unir a una tienda"); that address is kept on the register PC,
    outside the app folder that updates replace.
  - The server uploads to the production cloud.
  - Both read the update feed, channel and the thumbprints of Abastalia's code-signing certificate (read by update-service.ps1).
  - The server's database is the dedicated .\ABASTALIA instance (appsettings.json; R11 C1).
  The repo's appsettings keep the development values (http://localhost:5000, no cloud).
#>
$ProductionCloudUrl = 'https://app-abastalia-prod-jhzq7n.azurewebsites.net'
$InstalledApiUrl = 'https://localhost:5443'

# Sets "Section:Key" values in a JSON file, creating the sections that are missing and keeping everything else.
function Set-JsonValues([string] $Path, [hashtable] $Values) {
    $json = Get-Content $Path -Raw | ConvertFrom-Json
    foreach ($key in $Values.Keys) {
        $parts = $key -split ':'
        $node = $json
        foreach ($part in ($parts | Select-Object -SkipLast 1)) {
            if (-not $node.$part) { $node | Add-Member -NotePropertyName $part -NotePropertyValue ([pscustomobject]@{}) }
            $node = $node.$part
        }
        $node | Add-Member -Force -NotePropertyName $parts[-1] -NotePropertyValue $Values[$key]
    }
    $json | ConvertTo-Json -Depth 20 | Set-Content $Path -Encoding utf8
}

function Set-StoreSettings([string] $AppSettings, [string] $ServerSettings, [string] $FeedUrl = '', [string] $Channel = 'stable', [string] $CloudUrl = $ProductionCloudUrl,
    [string[]] $SignerThumbprints = @()) {
    # R11 C3: the SYSTEM update task installs only setups signed by one of these certificates.
    $updates = @{ 'Updates:FeedUrl' = $FeedUrl; 'Updates:Channel' = $Channel; 'Updates:SignerThumbprints' = @($SignerThumbprints | Where-Object { $_ }) }
    Set-JsonValues $AppSettings ($updates + @{ 'Api:Url' = $InstalledApiUrl })
    Set-JsonValues $ServerSettings ($updates + @{ 'Cloud:Url' = $CloudUrl })
}
