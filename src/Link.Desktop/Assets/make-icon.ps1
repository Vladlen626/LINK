# Draws Link.ico, the exe's icon: the jellyfish of LinkIconFactory (and LinkGlyphTemplate) on no background, each
# size drawn at its own pixels so that the eyes stay sharp. Run it again after the glyph changes, then rebuild.
# ASCII only: Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$legs = 6.5, 13.4, 4.2, 17.6, 9.75, 13.9, 8.9, 18.3, 17.5, 13.4, 19.8, 17.6, 14.25, 13.9, 15.1, 18.3

function Rounded($graphics, $brush, $x, $y, $w, $h, $r) {
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = 2 * $r
  $path.AddArc($x, $y, $d, $d, 180, 90)
  $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $path.CloseFigure()
  $graphics.FillPath($brush, $path)
  $path.Dispose()
}

# An image of the icon: PNG at 256, and below it a 32-bit bitmap with its mask, which every reader of icons takes.
function Entry($bitmap) {
  $stream = New-Object System.IO.MemoryStream
  if ($bitmap.Width -ge 256) {
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return $stream.ToArray()
  }
  $size = $bitmap.Width
  $writer = New-Object System.IO.BinaryWriter($stream)
  # BITMAPINFOHEADER: the height counts the colours and the mask.
  $writer.Write([uint32]40); $writer.Write([int32]$size); $writer.Write([int32]($size * 2))
  $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]0); $writer.Write([uint32]0)
  $writer.Write([int32]0); $writer.Write([int32]0); $writer.Write([uint32]0); $writer.Write([uint32]0)
  # BGRA rows from the bottom up; the alpha draws the shape, so the mask is all clear.
  for ($y = $size - 1; $y -ge 0; $y--) {
    for ($x = 0; $x -lt $size; $x++) {
      $c = $bitmap.GetPixel($x, $y)
      $writer.Write([byte]$c.B); $writer.Write([byte]$c.G); $writer.Write([byte]$c.R); $writer.Write([byte]$c.A)
    }
  }
  $maskRow = [math]::Ceiling($size / 32) * 4
  $writer.Write((New-Object byte[] ($maskRow * $size)))
  $writer.Flush()
  return $stream.ToArray()
}

$images = foreach ($size in $sizes) {
  $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bitmap)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.ScaleTransform($size / 24.0, $size / 24.0)
  $body = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(236, 106, 38))
  $eye = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(43, 24, 16))
  $leg = New-Object System.Drawing.Pen($body, 2.7)
  $leg.StartCap = $leg.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  for ($i = 0; $i -lt $legs.Count; $i += 4) { $g.DrawLine($leg, [single]$legs[$i], [single]$legs[$i + 1], [single]$legs[$i + 2], [single]$legs[$i + 3]) }
  Rounded $g $body 3.25 4 17.5 10.9 3.8
  Rounded $g $eye 7.5 6.7 2.6 4.4 1.3
  Rounded $g $eye 13.9 6.7 2.6 4.4 1.3
  $g.Dispose()
  , (Entry $bitmap)
  $bitmap.Dispose()
}

# An .ico: a header, one directory entry per size, then the images.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $side = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
  $writer.Write([byte]$side); $writer.Write([byte]$side); $writer.Write([byte]0); $writer.Write([byte]0)
  $writer.Write([uint16]1); $writer.Write([uint16]32)
  $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
  $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Flush()
[System.IO.File]::WriteAllBytes("$PSScriptRoot\Link.ico", $out.ToArray())
"$PSScriptRoot\Link.ico"
