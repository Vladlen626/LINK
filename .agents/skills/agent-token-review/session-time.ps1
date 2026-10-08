# Where an agent's wall-clock time went in one session, from the CLI's own session log: the span, the time inside tool
# calls (the rest is the model and waits between runs), the time by script or tool, and the slowest calls.
# Usage: session-time.ps1 -Agent codex|claude -Session <part of the log's file name> [-Since <ISO time>] [-Top 12]
# ASCII only: Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI.
param(
  [ValidateSet('codex', 'claude')] [string]$Agent = 'claude',
  [Parameter(Mandatory = $true)] [string]$Session,
  [string]$Since = '',
  [int]$Top = 12
)
$root = if ($Agent -eq 'codex') { "$env:USERPROFILE\.codex\sessions" } else { "$env:USERPROFILE\.claude\projects" }
$file = Get-ChildItem $root -Recurse -Filter "*$Session*.jsonl" | Select-Object -First 1
if (-not $file) { throw "No $Agent session log like *$Session*" }

# A call is named by the script it runs (verify_all.mjs, ue_mcp.py), else by its first word or the tool's name.
function Kind($name, $command) {
  if ($command -match '\bsleep \d|Start-Sleep|seq 1 \d') { return 'wait (sleep)' }
  if ($command -match '([\w-]+\.(?:mjs|js|py|ps1|cmd|sh))\b') { return $Matches[1] }
  if ($command -match '^\s*(?:cd [^;&]+(?:&&|;)\s*)?([\w.-]+)') { return "$name $($Matches[1])" }
  return $name
}

$calls = @{}; $done = New-Object System.Collections.ArrayList; $first = $null; $last = $null
foreach ($line in [System.IO.File]::ReadLines($file.FullName)) {
  if ($line -notmatch '"timestamp":"([^"]+)"') { continue }
  $time = [datetime]::Parse($Matches[1]).ToUniversalTime()
  if ($Since -and $time -lt [datetime]::Parse($Since).ToUniversalTime()) { continue }
  if ($null -eq $first) { $first = $time }
  $last = $time
  if ($Agent -eq 'claude') {
    foreach ($m in [regex]::Matches($line, '"type":"tool_use","id":"([^"]+)","name":"([^"]+)","input":\{(?:"command":"((?:[^"\\]|\\.){0,300}))?')) {
      $calls[$m.Groups[1].Value] = @{ Time = $time; Name = $m.Groups[2].Value; Command = $m.Groups[3].Value }
    }
    foreach ($m in [regex]::Matches($line, '"tool_use_id":"([^"]+)"')) {
      $c = $calls[$m.Groups[1].Value]
      if ($c) { [void]$done.Add([pscustomobject]@{ Seconds = ($time - $c.Time).TotalSeconds; Name = $c.Name; Command = $c.Command }); $calls.Remove($m.Groups[1].Value) }
    }
  }
  elseif ($line -match '"type":"(?:custom_tool_call|function_call)",.*"call_id":"([^"]+)","name":"([^"]+)"') {
    $id = $Matches[1]; $name = $Matches[2]
    # Codex calls tools from a script: a shell command's text, else the name of the tool it calls (an MCP server).
    $command = if ($line -match '\bcmd(?:\\?")?\s*:\s*\\?"((?:[^"\\]|\\.){0,300})') { $Matches[1] }
      elseif ($line -match 'tools\.(\w+)\(') { $Matches[1] } else { '' }
    $calls[$id] = @{ Time = $time; Name = $name; Command = $command }
  }
  elseif ($line -match '"type":"(?:custom_tool_call_output|function_call_output)",.*"call_id":"([^"]+)"') {
    $c = $calls[$Matches[1]]
    if ($c) {
      $seconds = if ($line -match 'Wall time ([\d.]+) seconds') { [double]$Matches[1] } else { ($time - $c.Time).TotalSeconds }
      [void]$done.Add([pscustomobject]@{ Seconds = $seconds; Name = $c.Name; Command = $c.Command }); $calls.Remove($Matches[1])
    }
  }
}

$inTools = ($done | Measure-Object -Property Seconds -Sum).Sum
"{0}: span {1:0} s, in tool calls {2:0} s ({3} calls), the rest (model, waits between runs) {4:0} s" -f `
  $file.BaseName, ($last - $first).TotalSeconds, $inTools, $done.Count, (($last - $first).TotalSeconds - $inTools)
"By kind:"
$done | Group-Object { Kind $_.Name $_.Command } | ForEach-Object {
  [pscustomobject]@{ Kind = $_.Name; Seconds = [math]::Round(($_.Group | Measure-Object -Property Seconds -Sum).Sum); Calls = $_.Count }
} | Sort-Object Seconds -Descending | Select-Object -First $Top | ForEach-Object { "  {0,7} s {1,4}x  {2}" -f $_.Seconds, $_.Calls, $_.Kind }
"Slowest calls:"
$done | Sort-Object { $_.Seconds } -Descending | Select-Object -First $Top | ForEach-Object {
  $text = if ($_.Command) { $_.Command } else { $_.Name }
  "  {0,7:0} s  {1}" -f $_.Seconds, ($text.Substring(0, [math]::Min(150, $text.Length)) -replace '\\n', ' ')
}
