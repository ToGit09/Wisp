param([string]$Assets = "src/Wisp.App/Assets")
# Builds the app icons of both editions (Windows, from the repository root):
#   powershell -ExecutionPolicy Bypass -File tools/make-icons.ps1
#
# 1. Assets/wisp.ico gets every size Windows asks for at 100–300 % display scaling (title bar 16/20/24/28/32/40/48,
#    taskbar and Alt+Tab 32–96, Explorer 256). A missing size is scaled by Windows and looks blurry (e.g. 24 px in the
#    title bar at 150 %). The hand-made sizes 16, 32, 48 and 256 are kept; the others are scaled down from 256.
# 2. Assets/Ai/wisp.ico and wisp.png (AIEdition): the same icon plus a small sparkle in the empty top-left corner.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$Sizes = 16, 20, 24, 28, 32, 40, 48, 56, 64, 96, 256
$HandMade = 16, 32, 48, 256

function Read-Ico([string]$path) {
    $ico = [IO.File]::ReadAllBytes($path)
    $entries = @{}
    for ($i = 0; $i -lt [BitConverter]::ToUInt16($ico, 4); $i++) {
        $o = 6 + 16 * $i
        $w = if ($ico[$o] -eq 0) { 256 } else { [int]$ico[$o] }
        $size = [BitConverter]::ToUInt32($ico, $o + 8); $off = [BitConverter]::ToUInt32($ico, $o + 12)
        $png = New-Object byte[] $size; [Array]::Copy($ico, $off, $png, 0, $size)
        $entries[$w] = $png
    }
    return $entries
}

function Write-Ico([string]$path, $entries) {
    $sizes = @($entries.Keys | Sort-Object)
    $out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter($out)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    foreach ($s in $sizes) {
        $b = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$entries[$s].Length); $w.Write([uint32]$offset)
        $offset += $entries[$s].Length
    }
    foreach ($s in $sizes) { $w.Write([byte[]]$entries[$s]) }
    $w.Flush()
    [IO.File]::WriteAllBytes($path, $out.ToArray())
}

function To-Png($bmp) {
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

function Resize([byte[]]$png, [int]$size) {
    $src = [System.Drawing.Image]::FromStream([IO.MemoryStream]::new($png))
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $attr = New-Object System.Drawing.Imaging.ImageAttributes
    $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY) # no dark fringe at the edges
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)), 0, 0, $src.Width, $src.Height, 'Pixel', $attr)
    $g.Dispose(); $src.Dispose()
    $result = To-Png $bmp; $bmp.Dispose()
    return ,$result
}

function Add-Sparkle($g, [double]$cx, [double]$cy, [double]$r) {
    $k = $r * 0.16
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $top = [System.Drawing.PointF]::new($cx, $cy - $r)
    $right = [System.Drawing.PointF]::new($cx + $r, $cy)
    $bottom = [System.Drawing.PointF]::new($cx, $cy + $r)
    $left = [System.Drawing.PointF]::new($cx - $r, $cy)
    $p.AddBezier($top, [System.Drawing.PointF]::new($cx + $k, $cy - $k), [System.Drawing.PointF]::new($cx + $k, $cy - $k), $right)
    $p.AddBezier($right, [System.Drawing.PointF]::new($cx + $k, $cy + $k), [System.Drawing.PointF]::new($cx + $k, $cy + $k), $bottom)
    $p.AddBezier($bottom, [System.Drawing.PointF]::new($cx - $k, $cy + $k), [System.Drawing.PointF]::new($cx - $k, $cy + $k), $left)
    $p.AddBezier($left, [System.Drawing.PointF]::new($cx - $k, $cy - $k), [System.Drawing.PointF]::new($cx - $k, $cy - $k), $top)
    $p.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $p)
}

function New-AiBitmap([byte[]]$png) {
    $src = [System.Drawing.Image]::FromStream([IO.MemoryStream]::new($png))
    $s = $src.Width
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($src, 0, 0, $s, $s)
    $u = $s / 256.0
    # Small icons get a relatively bigger sparkle so it stays visible; the second, tiny one only from 64 px on.
    $scale = if ($s -le 16) { 1.45 } elseif ($s -le 32) { 1.2 } else { 1.0 }
    Add-Sparkle $g (66 * $u) (64 * $u) (22 * $u * $scale)
    if ($s -ge 64) { Add-Sparkle $g (101 * $u) (40 * $u) (9 * $u) }
    $g.Dispose(); $src.Dispose()
    $result = To-Png $bmp; $bmp.Dispose()
    return ,$result
}

# 1. Light edition: all sizes.
$icoPath = Join-Path $Assets 'wisp.ico'
$base = Read-Ico $icoPath
$light = @{}
foreach ($s in $Sizes) {
    $light[$s] = [byte[]]$(if ($HandMade -contains $s -and $base.ContainsKey($s)) { ,$base[$s] } else { Resize $base[256] $s })
}
Write-Ico $icoPath $light

# 2. AI edition.
$aiDir = Join-Path $Assets 'Ai'
New-Item -ItemType Directory -Force $aiDir | Out-Null
$ai = @{}
foreach ($s in $Sizes) { $ai[$s] = [byte[]](New-AiBitmap $light[$s]) }
Write-Ico (Join-Path $aiDir 'wisp.ico') $ai
[IO.File]::WriteAllBytes((Join-Path $aiDir 'wisp.png'), (New-AiBitmap ([IO.File]::ReadAllBytes((Join-Path $Assets 'wisp.png')))))
"icons: " + ($Sizes -join ', ')
