# Gives the user's pack lessons (%USERPROFILE%\LinkPacks) to the repo's built-in packs (docs\harness\packs), so that
# colleagues get them with git and Link: each file there the user or a shared lesson changed or added. A file still as
# Link put it (its hash in .link-seeded.json) is Link's: the repo's is the newer one, nothing goes back.
# Usage: sync-packs.ps1 [-Apply]   (without -Apply it only lists)
# ASCII only: Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI.
param([switch]$Apply)
$from = Join-Path $env:USERPROFILE 'LinkPacks'
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$to = Join-Path $repo 'docs\harness\packs'
if (-not (Test-Path $from)) { throw "No packs folder at $from" }

$seeded = @{}
$manifest = Join-Path $from '.link-seeded.json'
if (Test-Path $manifest) {
  (Get-Content $manifest -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $seeded[$_.Name] = $_.Value }
}

$count = 0
Get-ChildItem $from -Recurse -File | Where-Object { $_.Name -notlike '.link-seeded*' -and $_.FullName -notmatch '\\__pycache__\\' } | ForEach-Object {
  $relative = $_.FullName.Substring($from.Length + 1).Replace('\', '/')
  $target = Join-Path $to $relative
  $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
  if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash -eq $hash) { return }
  if ($seeded[$relative] -eq $hash) { return }
  $what = if (Test-Path $target) { 'changed' } else { 'new    ' }
  "$what $relative"
  $count++
  if ($Apply) {
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item $_.FullName $target -Force
  }
}
if ($count -eq 0) { 'nothing to give: the packs are as the repo has them' }
elseif (-not $Apply) { "$count files; run with -Apply to copy them into docs\harness\packs" }
