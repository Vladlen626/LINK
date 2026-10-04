# Drives the user's own Link through UI Automation, at the user's request: in the Bigsteps project, open the latest
# task in Tasks, Hand off to Claude Code, add the user's words to the brief on the card, and press Run.
# ASCII only: Windows PowerShell 5.1 runs it; the Russian words come from a UTF-8 file.
param([string]$Project = 'Bigsteps', [string]$Extra, [switch]$NoRun)
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

# The card would take the user's keys: wait until nobody types for 15 s (at most 2 minutes).
for ($i = 0; $i -lt 120 -and [Idle]::Seconds() -lt 15; $i++) { Start-Sleep -Seconds 1 }
"idle: $([Idle]::Seconds()) s"

$name = (Wait-Id 'ProjectNameText').Current.Name
"project: $name"
if ($name -ne $Project) { throw "Link shows the project '$name', not '$Project': switch it first" }

# The panel open, on Tasks, with the list.
if (-not (Find-Id 'TaskTabButton')) { Invoke-Element (Wait-Id 'ExpandButton'); Start-Sleep -Milliseconds 800 }
Invoke-Element (Wait-Id 'TaskTabButton'); Start-Sleep -Milliseconds 800
$list = Wait-Id 'TaskList'
$isButton = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$first = $list.FindFirst($Tree::Descendants, $isButton)
if (-not $first) { throw 'no task in the list' }
Invoke-Element $first; Start-Sleep -Milliseconds 1200
"task: $((Wait-Id 'GoalText').Current.Name)"

# Hand off: the other agent in a new session, with the brief on the card.
Invoke-Element (Wait-Id 'HandOffButton'); Start-Sleep -Milliseconds 800
$isItem = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
$items = $A::RootElement.FindAll($Tree::Descendants, $isItem)
$item = $null
foreach ($each in $items) { if ($each.Current.Name -like 'Claude Code*new session*') { $item = $each } }
if (-not $item) { throw "no 'Claude Code - new session' in the Hand off menu: $(($items | ForEach-Object { $_.Current.Name }) -join '; ')" }
Invoke-Element $item; Start-Sleep -Milliseconds 1500

$goal = Wait-Id 'GoalBox'
$value = $goal.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$brief = $value.Current.Value
$extraText = [System.IO.File]::ReadAllText($Extra, [System.Text.Encoding]::UTF8)
$value.SetValue($brief.TrimEnd() + $extraText)
"brief: $($brief.Length) + $($extraText.Length) chars"
$run = Wait-Id 'RunButton'
"run button: $($run.Current.Name)"
if ($NoRun) { 'stopped before Run'; return }
Invoke-Element $run
Start-Sleep -Seconds 6
"pill: $((Find-Id 'AgentPillText').Current.Name)"
"status: $((Find-Id 'StatusText').Current.Name)"
