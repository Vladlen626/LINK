# Closes the user's Link from bin\Release gracefully, as Windows does at log-off: WM_QUERYENDSESSION and
# WM_ENDSESSION to each of its HwndWrapper[Link...] windows in turn (the visible "Link" window alone does nothing).
# Refuses while an agent task runs in Link: closing would stop the user's agent. ASCII only: Windows PowerShell 5.1
# reads a BOM-less UTF-8 script as ANSI.
# Output: "not running", "closed", "agent running" (exit 2) or "still running" (exit 1). -Check only tells
# whether Link runs and whether an agent works in it, and closes nothing.
param([switch]$Check)
$src = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class LinkWindows {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
  public static List<IntPtr> Find(uint target) {
    var list = new List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      var sb = new StringBuilder(256); GetClassName(h, sb, 256);
      if (pid == target && sb.ToString().StartsWith("HwndWrapper[Link")) list.Add(h);
      return true; }, IntPtr.Zero);
    return list;
  }
}
"@
$p = Get-Process Link -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\src\Link.Desktop\bin\Release\*' } | Select-Object -First 1
if (-not $p) { "not running"; exit 0 }

$agents = Get-CimInstance Win32_Process | Where-Object { $_.ParentProcessId -eq $p.Id -and $_.Name -match '^(claude|codex|cmd)\.exe$' }
if ($agents) { "agent running: " + (($agents | ForEach-Object { $_.Name }) -join ', '); exit 2 }
if ($Check) { "running, idle"; exit 0 }

Add-Type -TypeDefinition $src
foreach ($h in [LinkWindows]::Find([uint32]$p.Id)) {
  $r = [IntPtr]::Zero
  [void][LinkWindows]::SendMessageTimeout($h, 0x11, [IntPtr]::Zero, [IntPtr]::Zero, 2, 5000, [ref]$r)
  [void][LinkWindows]::SendMessageTimeout($h, 0x16, [IntPtr]1, [IntPtr]::Zero, 2, 5000, [ref]$r)
  Start-Sleep -Milliseconds 2500
  if (-not (Get-Process -Id $p.Id -ErrorAction SilentlyContinue)) { "closed"; exit 0 }
}
"still running"
exit 1
