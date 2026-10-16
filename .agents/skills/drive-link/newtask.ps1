# Drives the user's own Link through UI Automation, at the user's request: in the project Link shows, Studio -> New
# task, the goal from a UTF-8 file, the agent picked on the card (or its default), and Run.
# ASCII only: Windows PowerShell 5.1 runs it; the Russian goal comes from a UTF-8 file.
param([Parameter(Mandatory = $true)] [string]$Project, [Parameter(Mandatory = $true)] [string]$Goal, [string]$Agent = '', [switch]$NoRun)
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
function Selected($combo) {
  $sel = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
  ($sel | ForEach-Object { $_.Current.Name }) -join ', '
}
function Select-Item($combo, [string]$like) {
  $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep -Milliseconds 500
  $isItem = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
  $item = $null
  foreach ($each in $combo.FindAll($Tree::Descendants, $isItem)) { if ($each.Current.Name -like $like) { $item = $each } }
  if (-not $item) { throw "no item like '$like'" }
  $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); Start-Sleep -Milliseconds 800
}

# The card would take the user's keys: wait until nobody types for 15 s (at most 2 minutes).
for ($i = 0; $i -lt 120 -and [Idle]::Seconds() -lt 15; $i++) { Start-Sleep -Seconds 1 }
"idle: $([Idle]::Seconds()) s"

$name = (Wait-Id 'ProjectNameText').Current.Name
"project: $name"
if ($name -ne $Project) { throw "Link shows the project '$name', not '$Project': switch it first" }

# The side panel open, its Studio button pressed (it opens the Studio); New task opens the card (Link refuses while
# an agent works).
if (-not (Find-Id 'StudioButton')) { Invoke-Element (Wait-Id 'ExpandButton'); Start-Sleep -Milliseconds 800 }
Invoke-Element (Wait-Id 'StudioButton'); Start-Sleep -Milliseconds 800
if (-not (Find-Id 'NewTaskButton')) {
  # A task is open: its back button (no name, only the tooltip) leads to the list.
  $back = New-Object System.Windows.Automation.PropertyCondition($A::HelpTextProperty, 'All tasks of the project')
  foreach ($w in (Main-Window)) { $e = $w.FindFirst($Tree::Descendants, $back); if ($e) { Invoke-Element $e; break } }
  Start-Sleep -Milliseconds 800
}
Invoke-Element (Wait-Id 'NewTaskButton'); Start-Sleep -Milliseconds 1500
$provider = Wait-Id 'ProviderCombo'
if ($Agent -and (Selected $provider) -notlike "$Agent*") { Select-Item $provider "$Agent*" }

$box = (Wait-Id 'GoalBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$box.SetValue([System.IO.File]::ReadAllText($Goal, [System.Text.Encoding]::UTF8).Trim())
$plan = (Wait-Id 'PlanFirstBox').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
"agent: $(Selected $provider) | model: $(Selected (Wait-Id 'ModelCombo')) | effort: $(Selected (Wait-Id 'EffortCombo')) | access: $(Selected (Wait-Id 'AccessCombo')) | plan first: $plan"
"attachments: $((Find-Id 'AttachmentsText').Current.Name)"
"goal: $($box.Current.Value.Length) chars"
if ($NoRun) { 'stopped before Run'; return }
Invoke-Element (Wait-Id 'RunButton')
Start-Sleep -Seconds 6
"pill: $((Find-Id 'AgentPillText').Current.Name)"
"status: $((Find-Id 'StatusText').Current.Name)"
