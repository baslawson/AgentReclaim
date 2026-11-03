<#
.SYNOPSIS
  Draws the AgentReclaim app icon (the mascot, a friendly robot vacuum, on an orange tile)
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

    # Small sizes drop the details and enlarge the face so they stay legible.
    $small = $size -le 32
    $inset = if ($small) { 4 } else { 10 }

    $tile = New-RoundedRect $inset $inset (256 - 2 * $inset) (256 - 2 * $inset) $(if ($small) { 44 } else { 52 })
    $bg = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new(0, 256),
        [System.Drawing.Color]::FromArgb(0xF9, 0xA8, 0x62), [System.Drawing.Color]::FromArgb(0xD2, 0x6A, 0x1E))
    $g.FillPath($bg, $tile)

    $body = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 40), [System.Drawing.PointF]::new(0, 240),
        [System.Drawing.Color]::FromArgb(0x4A, 0x5C, 0x70), [System.Drawing.Color]::FromArgb(0x24, 0x30, 0x3D))
    $visor = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(0x14, 0x1B, 0x24))
    $white = [System.Drawing.Brushes]::White

    if ($small) {
        $g.FillEllipse($body, 20, 42, 216, 192)
        $g.FillPath($visor, (New-RoundedRect 40 84 176 104 52))
        $g.FillEllipse($white, 72, 106, 42, 58)
        $g.FillEllipse($white, 142, 106, 42, 58)
    }
    else {
        $antenna = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(0x24, 0x30, 0x3D), 9)
        $antenna.StartCap = "Round"
        $g.DrawLine($antenna, 128, 64, 128, 38)
        $g.FillEllipse([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(0x4C, 0xC3, 0x8A)), 114, 22, 28, 28)

        $g.FillEllipse($body, 30, 58, 196, 170)
        $g.FillPath($visor, (New-RoundedRect 56 98 144 84 42))
        $g.FillEllipse($white, 88, 116, 26, 30)
        $g.FillEllipse($white, 142, 116, 26, 30)
        $smile = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 8)
        $smile.StartCap = "Round"; $smile.EndCap = "Round"
        $g.DrawArc($smile, 104, 124, 48, 40, 35, 110)

        $bumper = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 10)
        $bumper.StartCap = "Round"; $bumper.EndCap = "Round"
        $g.DrawArc($bumper, 42, 66, 172, 150, 35, 110)
    }

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
