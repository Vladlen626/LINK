# Renders an SVG to a PNG with headless Edge, so that a drawing can be looked at (the Read tool shows the PNG).
# Usage: render.ps1 <svg> <png> <width> <height>; the size is the SVG's own width and height.
param(
  [Parameter(Mandatory)] [string]$Svg,
  [Parameter(Mandatory)] [string]$Png,
  [int]$Width = 800,
  [int]$Height = 600
)
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe" }
$svgPath = (Resolve-Path $Svg).Path
$pngPath = [System.IO.Path]::GetFullPath($Png)
# A profile of its own beside the PNG: Edge's default one may be in use.
$edgeProfile = Join-Path ([System.IO.Path]::GetDirectoryName($pngPath)) "edge-render-profile"
$url = "file:///" + ($svgPath -replace '\\', '/')
& $edge --headless=new --disable-gpu --hide-scrollbars "--user-data-dir=$edgeProfile" "--screenshot=$pngPath" `
  "--window-size=$Width,$Height" --default-background-color=00000000 $url 2>$null | Out-Null
if (Test-Path $pngPath) { $pngPath } else { "not rendered"; exit 1 }
