# Drives the user's own Link through UI Automation, at the user's request: in the project, open the newest task in
# Tasks and send words to its session (the reply box, then Send), as the user would.
# ASCII only: Windows PowerShell 5.1 runs it; the words come from a UTF-8 file.
param([string]$Project = 'Bigsteps', [string]$Words, [switch]$NoSend)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Idle {
  [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
  public static uint Seconds() { var i = new LASTINPUTINFO(); i.cbSize = (uint)Marshal.SizeOf(i); GetLastInputInfo(ref i); return ((uint)Environment.TickCount - i.dwTime) / 1000; }
}
"@
$A = [System.Windows.Automation.AutomationElement]
$Tree = [System.Windows.Automation.TreeScope]

function Main-Window {
  $link = Get-Process Link -ErrorAction Stop | Select-Object -First 1
  $byProcess = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $link.Id)
  $A::RootElement.FindAll($Tree::Children, $byProcess)
}
function Find-Id([string]$id) {
  $cond = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)
  foreach ($w in (Main-Window)) { $e = $w.FindFirst($Tree::Descendants, $cond); if ($e) { return $e } }
  return $null
}
function Invoke-Element($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Wait-Id([string]$id, [int]$seconds = 8) {
  for ($i = 0; $i -lt $seconds * 4; $i++) { $e = Find-Id $id; if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
  throw "not found: $id"
}

$text = [System.IO.File]::ReadAllText($Words, [System.Text.Encoding]::UTF8).Trim()
if ($text.Length -eq 0) { throw "no words in $Words" }

# The panel takes the keyboard: wait until nobody types for 15 s (at most 2 minutes).
for ($i = 0; $i -lt 120 -and [Idle]::Seconds() -lt 15; $i++) { Start-Sleep -Seconds 1 }
"idle: $([Idle]::Seconds()) s"

$name = (Wait-Id 'ProjectNameText').Current.Name
"project: $name"
if ($name -ne $Project) { throw "Link shows the project '$name', not '$Project': switch it first" }

# The panel open, on Tasks: the task Link shows there (it opens the last one), or else the newest of the list.
if (-not (Find-Id 'TaskTabButton')) { Invoke-Element (Wait-Id 'ExpandButton'); Start-Sleep -Milliseconds 800 }
Invoke-Element (Wait-Id 'TaskTabButton'); Start-Sleep -Milliseconds 800
if (-not (Find-Id 'GoalText')) {
  $list = Wait-Id 'TaskList'
  $isButton = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  $first = $list.FindFirst($Tree::Descendants, $isButton)
  if (-not $first) { throw 'no task in the list' }
  Invoke-Element $first; Start-Sleep -Milliseconds 1200
}
"task: $((Wait-Id 'GoalText').Current.Name)"
$plan = Find-Id 'PlanModeButton'
"plan mode chip: $(if ($plan -and -not $plan.Current.IsOffscreen) { 'shown' } else { 'hidden' })"

$box = Wait-Id 'ReplyBox'
$box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
"words: $($text.Length) chars"
if ($NoSend) { 'stopped before Send'; return }
Invoke-Element (Wait-Id 'ContinueButton')
Start-Sleep -Seconds 6
"pill: $((Find-Id 'AgentPillText').Current.Name)"
"status: $((Find-Id 'StatusText').Current.Name)"
