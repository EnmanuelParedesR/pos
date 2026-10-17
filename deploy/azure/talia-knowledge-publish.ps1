<#
.SYNOPSIS
  TK-350: publishes Talia's knowledge base (src/Hosts/Abastalia.Host.Cloud/Assistant/Knowledge/*.md) to the talia-knowledge container, so the
  API uses it within 10 minutes without a deploy (docs/talia.md §4).

.DESCRIPTION
  1. Runs the knowledge tests (format, links, nothing internal, no "fiado" in public articles, prices = the site, recall@3 >= 90 %). If they fail,
     nothing is uploaded.
  2. Uploads every article and deletes from the container the ones that no longer exist (the _state/ folder of the DGII watcher is kept).
  The API checks the same rules again before using the files and keeps its last good base if they fail.
  Who runs it needs "Storage Blob Data Contributor" on the storage account (az login; no account keys).

.EXAMPLE
  ./deploy/azure/talia-knowledge-publish.ps1
  ./deploy/azure/talia-knowledge-publish.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ResourceGroup = "rg-abastalia-prod",
    [string]$Container = "talia-knowledge",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$folder = Join-Path $root "src/Hosts/Abastalia.Host.Cloud/Assistant/Knowledge"

if (-not $SkipTests) {
    dotnet test (Join-Path $root "tests/Abastalia.Cloud.Tests") -- --filter-class "*TaliaKnowledgeTests" --filter-class "*TaliaUpdatesTests"
    if ($LASTEXITCODE -ne 0) { throw "The knowledge tests failed: nothing was published." }
}

$account = az storage account list -g $ResourceGroup --query "[?starts_with(name, 'stabastalia')].name | [0]" -o tsv
if (-not $account) { throw "No stabastalia* storage account in $ResourceGroup." }
$local = Get-ChildItem $folder -Filter *.md | Where-Object { -not $_.Name.StartsWith("_") }
Write-Host "Publishing $($local.Count) articles to $account/$Container"

if ($PSCmdlet.ShouldProcess("$account/$Container", "upload $($local.Count) articles")) {
    az storage blob upload-batch --auth-mode login --account-name $account -d $Container -s $folder --pattern "[!_]*.md" --overwrite --only-show-errors | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Upload failed (do you have Storage Blob Data Contributor on $account?)." }
    $remote = az storage blob list --auth-mode login --account-name $account -c $Container --query "[?!starts_with(name, '_')].name" -o tsv
    foreach ($name in $remote) {
        if (-not ($local.Name -contains $name)) {
            Write-Host "Deleting $name (no longer in the repository)"
            az storage blob delete --auth-mode login --account-name $account -c $Container -n $name --only-show-errors | Out-Null
        }
    }
    Write-Host "Done. The API picks it up within Talia:Knowledge:RefreshMinutes (10); its log says 'Talia knowledge <version> from blob'."
}
