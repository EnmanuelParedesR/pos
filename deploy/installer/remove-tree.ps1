<#
.SYNOPSIS
  R11 C13: dot-sourced by remove-data.ps1 and update-service.ps1. C:\ProgramData\Abastalia is writable by the service account, so a link
  (junction or symbolic link) planted there could make an administrator's or SYSTEM's recursive delete reach any folder. Remove-Tree
  refuses to delete a tree that is, or contains, a reparse point; it looks at each level without following links.
#>
function Assert-NoReparsePoint([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$Path es un enlace (reparse point): no se borra." }
    if ($item.PSIsContainer) { foreach ($child in Get-ChildItem -LiteralPath $Path -Force) { Assert-NoReparsePoint $child.FullName } }
}

function Remove-Tree([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Assert-NoReparsePoint $Path
    Remove-Item -LiteralPath $Path -Recurse -Force
}
