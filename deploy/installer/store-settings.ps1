<#
.SYNOPSIS
  TK-251: what an installed store needs in its published appsettings.json. build.ps1 dot-sources this file and calls Set-StoreSettings.
.DESCRIPTION
  - The register app talks to the server on the same PC: https on 5443 (Kestrel in the server's appsettings.Production.json). A register
    on another PC is pointed at the main PC when it joins the store ("Unir a una tienda"); that address is kept on the register PC,
    outside the app folder that updates replace.
  - The server uploads to the production cloud.
  - Both read the update feed and channel.
  The repo's appsettings keep the development values (http://localhost:5000, no cloud).
#>
$ProductionCloudUrl = 'https://app-toki-prod-jhzq7n.azurewebsites.net'
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

function Set-StoreSettings([string] $AppSettings, [string] $ServerSettings, [string] $FeedUrl = '', [string] $Channel = 'stable', [string] $CloudUrl = $ProductionCloudUrl) {
    $updates = @{ 'Updates:FeedUrl' = $FeedUrl; 'Updates:Channel' = $Channel }
    Set-JsonValues $AppSettings ($updates + @{ 'Api:Url' = $InstalledApiUrl })
    Set-JsonValues $ServerSettings ($updates + @{ 'Cloud:Url' = $CloudUrl })
}
