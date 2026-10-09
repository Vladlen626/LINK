# What Link gave an agent at the start of one session, by the prompt's # and ## sections and their sizes in
# characters (about 3.5 per token): which part rides along on every request of the run.
# Usage: prompt-sections.ps1 -Agent codex|claude -Session <part of the log's file name> [-Show <section heading part>]
# ASCII only: Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI.
param(
  [ValidateSet('codex', 'claude')] [string]$Agent = 'claude',
  [Parameter(Mandatory = $true)] [string]$Session,
  [string]$Show = ''
)
$root = if ($Agent -eq 'codex') { "$env:USERPROFILE\.codex\sessions" } else { "$env:USERPROFILE\.claude\projects" }
$file = Get-ChildItem $root -Recurse -Filter "*$Session*.jsonl" | Select-Object -First 1
if (-not $file) { throw "No $Agent session log like *$Session*" }

# The prompt is the longest user text among the session's first records (Codex puts its environment before it).
$prompt = ''; $count = 0
foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
  if (++$count -gt 40) { break }
  if ($line -notmatch '"role":"user"|"type":"user"') { continue }
  $entry = $line | ConvertFrom-Json
  $content = if ($Agent -eq 'claude') { $entry.message.content } else { $entry.payload.content }
  $text = if ($content -is [string]) { $content } else { ($content | ForEach-Object { $_.text }) -join "`n" }
  if ($text.Length -gt $prompt.Length) { $prompt = $text }
}
if (-not $prompt) { throw "No user prompt in the first records of $($file.Name)" }

$sections = New-Object System.Collections.ArrayList
$current = [pscustomobject]@{ Heading = '(before the first heading)'; Lines = New-Object System.Collections.ArrayList }
foreach ($line in $prompt -split "`n") {
  if ($line -match '^#{1,2} ') {
    [void]$sections.Add($current)
    $current = [pscustomobject]@{ Heading = $line.Trim(); Lines = New-Object System.Collections.ArrayList }
  }
  [void]$current.Lines.Add($line)
}
[void]$sections.Add($current)

"$($file.Name): $($prompt.Length) characters"
foreach ($section in $sections) {
  $size = ($section.Lines | Measure-Object -Property Length -Sum).Sum + $section.Lines.Count
  if ($size -le 1 -and $section.Heading -like '(before*') { continue }
  '{0,7}  {1}' -f $size, $section.Heading.Substring(0, [Math]::Min(70, $section.Heading.Length))
}
if ($Show) {
  $section = $sections | Where-Object { $_.Heading -like "*$Show*" } | Select-Object -First 1
  if ($section) { ''; $section.Lines -join "`n" }
}
