# Creates (idempotently) GitHub milestones, issues and a Project from backlog/BACKLOG.md.
param(
    [string] $Repo = "EnmanuelParedesR/pos",
    [string] $Owner = "EnmanuelParedesR",
    [string] $ProjectTitle = "Toki V1",
    [string] $Backlog = "$PSScriptRoot\..\backlog\BACKLOG.md",
    [switch] $DryRun
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')

# --- Parse the backlog ---
$items = @()
$milestone = $null
foreach ($line in Get-Content $Backlog -Encoding UTF8) {
    if ($line -match '^## (M\d+ · .+)$') { $milestone = $Matches[1].Trim(); continue }
    if ($line -match '^## Revisiones') { $milestone = 'Revisiones'; continue }
    if ($line -match '^## ') { $milestone = $null; continue }
    if (-not $milestone) { continue }
    $cells = $line -split '\|' | ForEach-Object { $_.Trim() }
    if ($cells.Count -lt 4) { continue }
    if ($milestone -eq 'Revisiones') {
        if ($cells[1] -match '^(R\d+)') {
            $id = $Matches[1]
            $items += [pscustomobject]@{ Id = $id; Title = "$id · Revisión de $($cells[2])"; Milestone = 'Revisiones'; State = $cells[3]; Detail = $cells[1] }
        }
        continue
    }
    if ($cells[1] -match '^(TK-\d+)$') {
        $items += [pscustomobject]@{ Id = $cells[1]; Title = "$($cells[1]) · $($cells[2])"; Milestone = $milestone; State = $cells[3]; Detail = '' }
    }
}
Write-Host "Parsed $($items.Count) tickets"

function Status($s) {
    if ($s -match '^↪') { 'Done' }
    elseif ($s -match '^✅' -and $s -notmatch 'Pendiente') { 'Done' }
    elseif ($s -match '^✅') { 'Done' }
    elseif ($s -match '^(🟡|🔨)') { 'In Progress' }
    else { 'Todo' }
}
function Short([string] $t, [int] $n) { if ($t.Length -le $n) { $t } else { $t.Substring(0, $n - 1) + '…' } }

if ($DryRun) { $items | ForEach-Object { "{0,-12} {1,-12} {2}" -f $_.Id, (Status $_.State), (Short $_.Title 80) }; return }

# --- Labels ---
$labels = (gh label list -R $Repo --limit 200 --json name | ConvertFrom-Json).name
foreach ($l in @(@('ticket','0B5CAD','Trabajo del backlog'), @('revisión','8A4B00','Hallazgos de una revisión senior'), @('bloqueado','B42318','En espera de algo externo'))) {
    if ($labels -notcontains $l[0]) { gh label create $l[0] -R $Repo --color $l[1] --description $l[2] | Out-Null }
}

# --- Milestones ---
$ms = @{}
foreach ($m in (gh api "repos/$Repo/milestones?state=all&per_page=100" | ConvertFrom-Json)) { $ms[$m.title] = $m.number }
foreach ($t in ($items.Milestone | Select-Object -Unique)) {
    if (-not $ms.ContainsKey($t)) { $ms[$t] = (gh api "repos/$Repo/milestones" -f title=$t | ConvertFrom-Json).number }
}

# --- Project ---
$project = (gh project list --owner $Owner --format json | ConvertFrom-Json).projects | Where-Object { $_.title -eq $ProjectTitle }
if (-not $project) {
    $project = gh project create --owner $Owner --title $ProjectTitle --format json | ConvertFrom-Json
    gh project link $project.number --owner $Owner --repo $Repo | Out-Null
}
$fields = (gh project field-list $project.number --owner $Owner --format json | ConvertFrom-Json).fields
$statusField = $fields | Where-Object { $_.name -eq 'Status' }
$opt = @{}; foreach ($o in $statusField.options) { $opt[$o.name] = $o.id }

# --- Issues ---
$existing = @{}
foreach ($i in (gh issue list -R $Repo --state all --limit 1000 --json number,title,url | ConvertFrom-Json)) {
    if ($i.title -match '^((TK-\d+)|(R\d+)) ·') { $existing[$Matches[1]] = $i }
}
foreach ($it in $items) {
    $status = Status $it.State
    if ($existing.ContainsKey($it.Id)) { Write-Host "skip $($it.Id)"; continue }
    $label = if ($it.Milestone -eq 'Revisiones') { 'revisión' } else { 'ticket' }
    $body = "**Hito:** $($it.Milestone)`n`n**Estado en el backlog:** $($it.State)`n`n" +
            $(if ($it.Detail) { "**Documento:** $($it.Detail)`n`n" } else { '' }) +
            "_Creado desde `backlog/BACKLOG.md`. Desde ahora este issue es la fuente del ticket; los commits lo referencian ($($it.Id))._"
    $args = @('issue','create','-R',$Repo,'--title',(Short $it.Title 250),'--body',$body,'--label',$label,'--milestone',$it.Milestone)
    if ($it.State -match '^⏸|⏸') { $args += @('--label','bloqueado') }
    $url = (gh @args).Trim()
    if ($it.State -match '^↪') { gh issue close $url -R $Repo --reason 'not planned' | Out-Null }
    elseif ($status -eq 'Done') { gh issue close $url -R $Repo --reason completed | Out-Null }
    $item = gh project item-add $project.number --owner $Owner --url $url --format json | ConvertFrom-Json
    gh project item-edit --id $item.id --project-id $project.id --field-id $statusField.id --single-select-option-id $opt[$status] | Out-Null
    Write-Host "$($it.Id) -> $url ($status)"
}
Write-Host "Project: https://github.com/users/$Owner/projects/$($project.number)"
