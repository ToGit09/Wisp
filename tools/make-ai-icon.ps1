param([string]$Assets = "src/Wisp.App/Assets", [string]$OutDir = "src/Wisp.App/Assets/Ai")
# Builds the AI edition icon: the normal Wisp icon plus a small sparkle in the empty top-left corner.
# Run again after changing wisp.ico / wisp.png (Windows, from the repository root):
#   powershell -ExecutionPolicy Bypass -File tools/make-ai-icon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

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
    # Small icons get a relatively bigger sparkle so it stays visible.
    $scale = if ($s -le 16) { 1.45 } elseif ($s -le 32) { 1.2 } else { 1.0 }
    Add-Sparkle $g (66 * $u) (64 * $u) (22 * $u * $scale)
    if ($s -ge 64) { Add-Sparkle $g (101 * $u) (40 * $u) (9 * $u) }
    $g.Dispose(); $src.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

$ico = [IO.File]::ReadAllBytes((Join-Path $Assets 'wisp.ico'))
$n = [BitConverter]::ToUInt16($ico, 4)
$entries = @()
for ($i = 0; $i -lt $n; $i++) {
    $o = 6 + 16 * $i
    $size = [BitConverter]::ToUInt32($ico, $o + 8); $off = [BitConverter]::ToUInt32($ico, $o + 12)
    $png = New-Object byte[] $size; [Array]::Copy($ico, $off, $png, 0, $size)
    $entries += ,@{ W = $ico[$o]; H = $ico[$o + 1]; Png = (New-AiBitmap $png) }
}

# Write the ICO (PNG-compressed entries, same layout as the original).
$out = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$n)
$offset = 6 + 16 * $n
foreach ($e in $entries) {
    $w.Write([byte]$e.W); $w.Write([byte]$e.H); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$e.Png.Length); $w.Write([uint32]$offset)
    $offset += $e.Png.Length
}
foreach ($e in $entries) { $w.Write($e.Png) }
$w.Flush()
New-Item -ItemType Directory -Force $OutDir | Out-Null
[IO.File]::WriteAllBytes((Join-Path $OutDir 'wisp.ico'), $out.ToArray())
[IO.File]::WriteAllBytes((Join-Path $OutDir 'wisp.png'), (New-AiBitmap ([IO.File]::ReadAllBytes((Join-Path $Assets 'wisp.png')))))
"done"

