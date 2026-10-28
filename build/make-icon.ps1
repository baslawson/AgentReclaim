<#
.SYNOPSIS
  Draws the AgentReclaim app icon (a usage gauge whose green arc is an arrow sweeping back to "low")
  at every Windows icon size and writes src/AgentReclaim.App/Assets/AgentReclaim.ico plus a 256 px PNG.
#>
param([string]$OutDir = (Join-Path $PSScriptRoot "..\src\AgentReclaim.App\Assets"))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Get-Point([single]$cx, [single]$cy, [single]$r, [double]$deg) {
    $a = $deg * [Math]::PI / 180
    [System.Drawing.PointF]::new($cx + $r * [Math]::Cos($a), $cy + $r * [Math]::Sin($a))
}

function Draw-Icon([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"
    $g.PixelOffsetMode = "HighQuality"
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Small sizes get bolder strokes and fewer details so they stay legible.
    $small = $size -le 32
    $inset = if ($small) { 4 } else { 10 }

    # Tile: dark slate, subtle top-to-bottom gradient.
    $tile = New-RoundedRect $inset $inset (256 - 2 * $inset) (256 - 2 * $inset) $(if ($small) { 44 } else { 52 })
    $bg = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new(0, 256),
        [System.Drawing.Color]::FromArgb(0x2E, 0x3D, 0x4F), [System.Drawing.Color]::FromArgb(0x14, 0x1B, 0x24))
    $g.FillPath($bg, $tile)

    $cx = 128; $cy = 146; $r = if ($small) { 80 } else { 84 }
    $w = if ($small) { 40 } else { 28 }
    $box = [System.Drawing.RectangleF]::new($cx - $r, $cy - $r, 2 * $r, 2 * $r)
    $gap = if ($small) { 0 } else { 5 }

    # Gauge: 240 degrees from lower left (150) clockwise to lower right (30).
    $green = [System.Drawing.Color]::FromArgb(0x4C, 0xB8, 0x72)
    $amber = [System.Drawing.Color]::FromArgb(0xE8, 0xA8, 0x2A)
    $red = [System.Drawing.Color]::FromArgb(0xD9, 0x4A, 0x3B)
    foreach ($seg in @(@{ C = $amber; S = 270; E = 330 }, @{ C = $red; S = 330; E = 390 })) {
        $pen = [System.Drawing.Pen]::new($seg.C, $w)
        $g.DrawArc($pen, $box, $seg.S + $gap / 2, $seg.E - $seg.S - $gap)
    }

    # Green arc ending in an arrowhead at the low end: usage swept back down.
    $arrowLen = if ($small) { 30 } else { 34 }
    $tipDeg = 150 + $arrowLen * 0.55
    $pen = [System.Drawing.Pen]::new($green, $w)
    $g.DrawArc($pen, $box, $tipDeg + 8, 270 - $tipDeg - 8 - $gap / 2)
    $head = $w * 1.05
    $tip = Get-Point $cx $cy $r 150
    $outer = Get-Point $cx $cy ($r + $head) ($tipDeg + 10)
    $inner = Get-Point $cx $cy ($r - $head) ($tipDeg + 10)
    $g.FillPolygon([System.Drawing.SolidBrush]::new($green), [System.Drawing.PointF[]]@($tip, $outer, $inner))

    # Needle pointing into the green, with a hub.
    $needle = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $(if ($small) { 20 } else { 14 }))
    $needle.StartCap = "Round"; $needle.EndCap = "Round"
    $g.DrawLine($needle, [System.Drawing.PointF]::new($cx, $cy), (Get-Point $cx $cy ($r - $(if ($small) { 30 } else { 34 })) 212))
    $hub = if ($small) { 22 } else { 17 }
    $g.FillEllipse([System.Drawing.Brushes]::White, $cx - $hub, $cy - $hub, 2 * $hub, 2 * $hub)

    $g.Dispose()
    $bmp
}

# 32-bit BGRA bitmap entry as stored in an .ico: header with doubled height, bottom-up rows, empty AND mask.
function ConvertTo-IconDib([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $ms = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($ms)
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write([byte[]]::new($maskRow * $s))
    $w.Flush()
    , $ms.ToArray() # the comma stops PowerShell unrolling the bytes
}

# Bitmaps for the small sizes (readable everywhere), PNG for 256 px (keeps the file small).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    if ($s -eq 256) {
        $ms = [IO.MemoryStream]::new()
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Save((Join-Path $OutDir "AgentReclaim-256.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
    }
    else {
        [byte[]]$data = ConvertTo-IconDib $bmp
    }
    $bmp.Dispose()
    , $data
}

$ico = [IO.MemoryStream]::new()
$wr = [IO.BinaryWriter]::new($ico)
$wr.Write([uint16]0); $wr.Write([uint16]1); $wr.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $wr.Write([byte]$dim); $wr.Write([byte]$dim); $wr.Write([byte]0); $wr.Write([byte]0)
    $wr.Write([uint16]1); $wr.Write([uint16]32)
    $wr.Write([uint32]$images[$i].Length); $wr.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $wr.Write($img) }
$wr.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir "AgentReclaim.ico"), $ico.ToArray())
"Wrote $(Join-Path $OutDir 'AgentReclaim.ico') ($($sizes -join ', ') px)"
