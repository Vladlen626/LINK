# Where an agent's tokens went, per session, from the CLI's own session logs: requests, the first request's input (what
# rides along on every request: system prompt, tools, skills, rules), the last one's, totals by billing type, tool
# calls and the tool troubles seen in Link's runs. Reads sizes and patterns only.
# Usage: session-stats.ps1 -Agent codex|claude [-Like <part of the workspace path>] [-Days 7]
# ASCII only: Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI.
param(
  [ValidateSet('codex', 'claude')] [string]$Agent = 'codex',
  [string]$Like = '',
  [int]$Days = 7
)
$since = (Get-Date).AddDays(-$Days)
$c = { param($n) [string][char]$n }
# UTF-8 Cyrillic read as ANSI: R or S followed by a high Windows-1251 character, three times or more.
$moji = [regex]('(?:[' + (& $c 0x420) + (& $c 0x421) + '][' + (& $c 0xA0) + '-' + (& $c 0xBF) + (& $c 0x401) + '-' + (& $c 0x40F) +
  (& $c 0x451) + '-' + (& $c 0x45F) + (& $c 0x490) + (& $c 0x491) + (& $c 0x2013) + '-' + (& $c 0x203A) + (& $c 0x20AC) +
  (& $c 0x2116) + (& $c 0x2122) + ']){3,}')
$total = @{ Sessions = 0; Requests = 0; In = 0L; Cached = 0L; Out = 0L; Calls = 0 }

function Report($name, $s) {
  "{0}: requests {1}, first in {2}, last in {3}, in {4} (cached {5}), out {6}, calls {7}, errors {8}, garbled {9}, git ownership {10}, sandbox refused {11}, plugin guides {12}" -f `
    $name, $s.Requests, $s.First, $s.Last, $s.In, $s.Cached, $s.Out, $s.Calls, $s.Errors, $s.Garbled, $s.Git, $s.Refused, $s.Guides
  foreach ($k in 'Requests', 'In', 'Cached', 'Out', 'Calls') { $total[$k] += $s[$k] }
  $total.Sessions++
}

function NewStats { @{ Requests = 0; First = $null; Last = 0; In = 0L; Cached = 0L; Out = 0L; Calls = 0; Errors = 0; Garbled = 0; Git = 0; Refused = 0; Guides = 0 } }

function Troubles($s, $line) {
  if ($moji.IsMatch($line)) { $s.Garbled++ }
  if ($line -match 'dubious ownership') { $s.Git++ }
  if ($line -match 'Rejected\(') { $s.Refused++ }
}

if ($Agent -eq 'codex') {
  $files = Get-ChildItem "$env:USERPROFILE\.codex\sessions" -Recurse -Filter 'rollout-*.jsonl' | Where-Object { $_.LastWriteTime -ge $since }
  foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName)
    if ($lines.Length -eq 0) { continue }
    $cwd = ($lines[0] | ConvertFrom-Json).payload.cwd
    if ($Like -and $cwd -notlike "*$Like*") { continue }
    $s = NewStats
    foreach ($line in $lines) {
      if ($line -match '"type":"token_count"' -and $line -match '"last_token_usage":\{([^}]*)\}') {
        $usage = $Matches[1]
        $n = if ($usage -match '"input_tokens":(\d+)') { [long]$Matches[1] } else { 0L }
        $s.Requests++
        if ($null -eq $s.First) { $s.First = $n }
        $s.Last = $n; $s.In += $n
        if ($usage -match '"cached_input_tokens":(\d+)') { $s.Cached += [long]$Matches[1] }
        if ($usage -match '"output_tokens":(\d+)') { $s.Out += [long]$Matches[1] }
      }
      elseif ($line -match '"type":"(custom_tool_call|function_call)","') {
        $s.Calls++
        if ($line -match 'plugins[\\/]+cache') { $s.Guides++ }
      }
      elseif ($line -match '"type":"(custom_tool_call_output|function_call_output)"') {
        if ($line -match 'Exit code: [1-9]|exit_code\\?":[1-9]|Script failed') { $s.Errors++ }
        Troubles $s $line
      }
    }
    Report ("{0} {1}" -f $f.Name.Substring(8, 19), (Split-Path $cwd -Leaf)) $s
  }
}
else {
  $folder = if ($Like) { $Like -replace '[^A-Za-z0-9]', '-' } else { '' }
  # Link runs Claude under its own accounts (CLAUDE_CONFIG_DIR), so their logs are there too.
  $roots = @("$env:USERPROFILE\.claude\projects") + @(Get-ChildItem "$env:LOCALAPPDATA\Link\ClaudeAccounts\*\projects" -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
  $files = Get-ChildItem $roots -Recurse -Filter '*.jsonl' |
    Where-Object { $_.LastWriteTime -ge $since -and (-not $folder -or $_.Directory.Name -like "*$folder*") }
  foreach ($f in $files) {
    $s = NewStats
    $seen = @{}
    foreach ($line in [System.IO.File]::ReadLines($f.FullName)) {
      if ($line -match '"type":"assistant"' -and $line -match '"usage":\{') {
        # One response is written as several lines, one per content block, with the same message id.
        $id = if ($line -match '"message":\{"id":"([^"]+)"|"id":"(msg_[^"]+)"') { "$($Matches[1])$($Matches[2])" } else { [guid]::NewGuid() }
        if (-not $seen.ContainsKey($id)) {
          $seen[$id] = 1
          $in = 0L; $cached = 0L; $out = 0L
          if ($line -match '"input_tokens":(\d+)') { $in += [long]$Matches[1] }
          if ($line -match '"cache_creation_input_tokens":(\d+)') { $in += [long]$Matches[1] }
          if ($line -match '"cache_read_input_tokens":(\d+)') { $in += [long]$Matches[1]; $cached = [long]$Matches[1] }
          if ($line -match '"output_tokens":(\d+)') { $out = [long]$Matches[1] }
          $s.Requests++
          if ($null -eq $s.First) { $s.First = $in }
          $s.Last = $in; $s.In += $in; $s.Cached += $cached; $s.Out += $out
        }
        $s.Calls += ([regex]::Matches($line, '"type":"tool_use"')).Count
      }
      elseif ($line -match '"type":"user"' -and $line -match '"tool_result"') {
        $s.Errors += ([regex]::Matches($line, '"is_error":true')).Count
        Troubles $s $line
      }
    }
    if ($s.Requests -gt 0) { Report ("{0} {1}" -f $f.BaseName.Substring(0, 8), $f.Directory.Name) $s }
  }
}
"total: {0} sessions, {1} requests, in {2} (cached {3}), out {4}, calls {5}" -f $total.Sessions, $total.Requests, $total.In, $total.Cached, $total.Out, $total.Calls
