# Rebuilds the user's Link from this repo and starts it again: closes it gracefully (never while an agent works in
# it), builds Release, runs the tests and starts the new Link through Explorer. The "Rebuild Link" Start shortcut
# runs it (install-shortcuts.ps1). ASCII only: Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI.
$repo = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$exe = "$repo\src\Link.Desktop\bin\Release\net10.0-windows\Link.exe"

# Through Explorer, so that Link does not belong to this console or to whatever started it.
function Start-Link { if (Test-Path $exe) { explorer.exe $exe } }

function Stop-WithError($message) {
  Write-Host $message -ForegroundColor Red
  Read-Host 'Press Enter to close'
  exit 1
}

Write-Host 'Closing Link...'
$state = & "$PSScriptRoot\close-link.ps1"
if ($LASTEXITCODE -eq 2) { Stop-WithError "An agent works in Link ($state): rebuild when its task is over." }
if ($LASTEXITCODE -ne 0) { Stop-WithError 'Link did not close: exit it from the tray, then rebuild again.' }

Write-Host 'Building...'
dotnet build "$repo\Link.sln" -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { Start-Link; Stop-WithError 'The build failed: Link was started as it was.' }

Write-Host 'Testing...'
dotnet run --project "$repo\tests\Link.Desktop.Tests" -c Release --no-build
$tests = $LASTEXITCODE
Start-Link
if ($tests -ne 0) { Stop-WithError 'Tests failed: the new Link is started anyway.' }

Write-Host 'The new Link is started.' -ForegroundColor Green
Start-Sleep -Seconds 3
