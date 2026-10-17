<#
.SYNOPSIS
  TK-348: runs Talia's live evals (tests/Abastalia.Cloud.Tests/Talia) against the eval Azure OpenAI resource and prints the report.

.DESCRIPTION
  Uses the dev resource oai-abastalia-dev-evals (rg-abastalia-dev), which takes keys; production's takes only its managed identity and is never
  used for evals. The key is read with az at run time and lives only in this process. Report: TestResults/talia/report-*.md (docs/talia.md).

.EXAMPLE
  ./deploy/azure/talia-evals.ps1
  ./deploy/azure/talia-evals.ps1 -Models "gpt-5-nano:low:low,gpt-5-nano:minimal:low" -Repeat 3 -Only panel-
#>
param(
    [string]$Models = "gpt-5-nano:low:low,gpt-5-nano:minimal:low,gpt-5.4-nano:low:low,gpt-5.6-luna:low:low,gpt-5-mini:low:low,gpt-4.1-nano",
    [string]$Judge = "gpt-5-mini",
    [int]$Repeat = 1,
    [string]$Only = "",
    [string]$ResourceGroup = "rg-abastalia-dev",
    [string]$Account = "oai-abastalia-dev-evals"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$env:ABASTALIA_TALIA_EVAL_ENDPOINT = az cognitiveservices account show -g $ResourceGroup -n $Account --query properties.endpoint -o tsv
$env:ABASTALIA_TALIA_EVAL_KEY = az cognitiveservices account keys list -g $ResourceGroup -n $Account --query key1 -o tsv
if (-not $env:ABASTALIA_TALIA_EVAL_KEY) { throw "No key for $Account (az login, or the resource is gone)." }
$env:ABASTALIA_TALIA_EVAL_MODELS = $Models
$env:ABASTALIA_TALIA_EVAL_JUDGE = $Judge
$env:ABASTALIA_TALIA_EVAL_REPEAT = "$Repeat"
$env:ABASTALIA_TALIA_EVAL_ONLY = $Only
try {
    dotnet test (Join-Path $root "tests/Abastalia.Cloud.Tests") -- --filter-class "*TaliaLiveEvals" --output Detailed
}
finally {
    Remove-Item Env:ABASTALIA_TALIA_EVAL_KEY -ErrorAction SilentlyContinue
}
