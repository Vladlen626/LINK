# Puts "Link" (this repo's bin\Release build) and "Rebuild Link" (rebuild-link.ps1) into the Start menu. Pin either
# from Start (right click > Pin to taskbar): Windows lets no program pin itself. Run again after the repo moves.
# ASCII only: Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI.
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$exe = "$repo\src\Link.Desktop\bin\Release\net10.0-windows\Link.exe"
$programs = [Environment]::GetFolderPath('Programs')
$shell = New-Object -ComObject WScript.Shell

$link = $shell.CreateShortcut("$programs\Link.lnk")
$link.TargetPath = $exe
$link.WorkingDirectory = Split-Path $exe
$link.Description = 'Link: the AI companion overlay'
$link.Save()

$rebuild = $shell.CreateShortcut("$programs\Rebuild Link.lnk")
$rebuild.TargetPath = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"
$rebuild.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\rebuild-link.ps1`""
$rebuild.WorkingDirectory = $repo
$rebuild.IconLocation = "$env:WINDIR\System32\shell32.dll,238"
$rebuild.Description = 'Close Link, build it from the repo, test it and start it again'
$rebuild.Save()

"Start menu: $programs\Link.lnk, $programs\Rebuild Link.lnk"
